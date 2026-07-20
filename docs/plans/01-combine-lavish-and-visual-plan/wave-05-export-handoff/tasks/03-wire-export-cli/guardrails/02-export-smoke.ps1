# catches: `charter export` that builds but does not actually inline the local asset / scrub the local
#          path end to end (a wired-but-broken command) - AND (post-#302-review hardening) a BROKEN
#          IMPLEMENTATION that ships despite passing the unit-test suite: a naive whole-HTML asset regex
#          that corrupts the vendored Mermaid runtime, or a bare `resolved.StartsWith(planDirectory)`
#          confinement check that leaks a sibling directory sharing planDirectory as a raw string prefix
#          (".../plan-evil/secret.png" for a planDirectory of ".../plan"). This is the ONLY check in the
#          wave that runs the ACTUAL BUILT BINARY end to end - the cheapest high-leverage backstop behind
#          the unit tests, so it exercises the two highest-risk behaviors directly, not just the basic
#          "does it inline anything" case.
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("charter-export-smoke-" + [System.Guid]::NewGuid().ToString("N"))
$planDir = Join-Path $tmp "plan"
$evilDir = Join-Path $tmp "plan-evil"
New-Item -ItemType Directory -Path $planDir | Out-Null
New-Item -ItemType Directory -Path $evilDir | Out-Null
try {
    $mdx = Join-Path $planDir "sample.mdx"
    $png = Join-Path $planDir "diagram.png"
    $secret = Join-Path $evilDir "secret.png"
    $out = Join-Path $tmp "out.html"
    [byte[]]$picBytes = 1..64 | ForEach-Object { [byte]($_ % 256) }
    [byte[]]$secretBytes = 1..8 | ForEach-Object { [byte]$_ }
    [System.IO.File]::WriteAllBytes($png, $picBytes)
    [System.IO.File]::WriteAllBytes($secret, $secretBytes)
    $secretBase64 = [Convert]::ToBase64String($secretBytes)

    @'
# Hello Export

:::diagram
graph TD; A-->B;
:::

![Diagram](./diagram.png)

![Secret](../plan-evil/secret.png)
'@ | Set-Content -Path $mdx -Encoding utf8

    dotnet run --project src/Charter.Cli -c Debug -- export $mdx -o $out 2>&1 | Out-String | Write-Output
    if (-not (Test-Path $out)) {
        Write-Output "charter export produced no output file at $out"
        exit 1
    }
    $html = Get-Content -Raw $out

    if ([string]::IsNullOrWhiteSpace($html) -or ($html -notmatch 'data:image/png;base64,')) {
        Write-Output "charter export output does not contain an inlined data:image/png;base64, URI for the local asset."
        exit 1
    }

    # The vendored Mermaid runtime must survive completely intact - proves the asset-inlining pass did
    # NOT corrupt the <script> region (the naive whole-HTML-regex failure mode).
    if ($html -notmatch '__esbuild_esm_mermaid_nm') {
        Write-Output "charter export output does not contain the vendored Mermaid runtime marker - the diagram runtime was lost or corrupted by the asset-inlining pass."
        exit 1
    }

    # The sibling directory "plan-evil" must NEVER be inlined or leaked - proves path confinement is
    # separator-safe, not a bare string-prefix check.
    if ($html -match [regex]::Escape($secretBase64)) {
        Write-Output "charter export inlined the sibling-directory asset (plan-evil/secret.png) - path confinement accepted a directory that merely shares planDirectory as a raw string prefix."
        exit 1
    }
    if ($html -match 'plan-evil') {
        Write-Output "charter export output leaks the literal sibling-directory name 'plan-evil' - the confinement-refused asset's path was not fully scrubbed."
        exit 1
    }

    if ($html -match [regex]::Escape($tmp)) {
        Write-Output "charter export output leaks the local temp-directory path - the artifact must not carry the author's local filesystem layout."
        exit 1
    }
    if ($html -match 'data-charter-sdk') {
        Write-Output "charter export output carries the data-charter-sdk marker - the annotation SDK must never be present in an exported artifact."
        exit 1
    }
    exit 0
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
