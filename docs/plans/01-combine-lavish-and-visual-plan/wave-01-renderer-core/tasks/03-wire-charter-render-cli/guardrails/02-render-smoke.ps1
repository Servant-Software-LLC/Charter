# catches: `charter render` that builds but does not actually render a .mdx to HTML end to end (a
#          wired-but-broken command). Synthesizes a sample, runs the command, asserts non-empty HTML
#          output containing the rendered heading.
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("charter-smoke-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $mdx = Join-Path $tmp "sample.mdx"
    $out = Join-Path $tmp "out.html"
    "# Hello Charter`n`nA smoke paragraph." | Set-Content -Path $mdx -Encoding utf8
    dotnet run --project src/Charter.Cli -c Debug -- render $mdx -o $out 2>&1 | Out-String | Write-Output
    if (-not (Test-Path $out)) {
        Write-Output "charter render produced no output file at $out"
        exit 1
    }
    $html = Get-Content -Raw $out
    if ([string]::IsNullOrWhiteSpace($html) -or ($html -notmatch 'Hello Charter')) {
        Write-Output "charter render output is empty or does not contain the rendered heading 'Hello Charter'."
        exit 1
    }
    exit 0
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
