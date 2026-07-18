# catches: a `charter review` that builds/wires but does not actually serve the rendered plan over
#          loopback with the injected SDK and capability enforcement. Builds, starts the command as a
#          background server, polls the printed loopback URL, and asserts: 200 + rendered content +
#          injected SDK marker with the valid key; non-200 without the key; non-200 on a path-traversal.
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("charter-review-smoke-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null
$proc = $null
try {
    $mdx = Join-Path $tmp "sample.mdx"
    $stdout = Join-Path $tmp "stdout.log"
    $stderr = Join-Path $tmp "stderr.log"
    "# Hello Charter`n`nA smoke paragraph." | Set-Content -Path $mdx -Encoding utf8

    # Build once so we launch a single, killable process (dotnet run would fork a child we couldn't stop).
    dotnet build src/Charter.Cli/Charter.Cli.csproj -c Debug 2>&1 | Out-String | Write-Output
    if ($LASTEXITCODE -ne 0) { Write-Output "smoke: Charter.Cli did not build."; exit 1 }
    $dll = "src/Charter.Cli/bin/Debug/net8.0/Charter.Cli.dll"
    if (-not (Test-Path $dll)) { Write-Output "smoke: built Charter.Cli.dll not found at $dll."; exit 1 }

    $proc = Start-Process -FilePath "dotnet" -ArgumentList @($dll, "review", $mdx, "--no-open") `
        -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr

    # Poll stdout for the server-ready loopback URL: http://127.0.0.1:<port>/?key=<key>
    $url = $null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        if (Test-Path $stdout) {
            $m = Select-String -Path $stdout -Pattern 'http://127\.0\.0\.1:\d+/\?key=\S+' | Select-Object -First 1
            if ($m) { $url = $m.Matches[0].Value; break }
        }
        if ($proc.HasExited) { break }
    }
    if (-not $url) {
        Write-Output "smoke: charter review did not print a loopback URL (http://127.0.0.1:<port>/?key=<key>) within the timeout."
        if (Test-Path $stdout) { Write-Output "--- stdout ---"; Get-Content $stdout | Write-Output }
        if (Test-Path $stderr) { Write-Output "--- stderr ---"; Get-Content $stderr | Write-Output }
        exit 1
    }

    # (1) valid key -> 200 + rendered content + injected SDK marker
    $ok = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10
    if ($ok.StatusCode -ne 200) { Write-Output "smoke: served URL returned $($ok.StatusCode), expected 200."; exit 1 }
    if ($ok.Content -notmatch 'Hello Charter') { Write-Output "smoke: served body does not contain the rendered heading 'Hello Charter'."; exit 1 }
    if ($ok.Content -notmatch 'data-charter-sdk') { Write-Output "smoke: served body does not carry the injected SDK marker (data-charter-sdk) - serve-time SDK injection is missing."; exit 1 }

    $baseUrl = $url -replace '\?key=\S+$', ''
    $keyQuery = ($url -split '\?')[1]

    # (2) missing capability key -> non-200 (loopback + capability enforcement)
    $status = 0
    try { $r = Invoke-WebRequest -Uri $baseUrl -UseBasicParsing -TimeoutSec 10; $status = [int]$r.StatusCode }
    catch { $status = [int]$_.Exception.Response.StatusCode }
    if ($status -eq 200) { Write-Output "smoke: a request WITHOUT a capability key returned 200 - the per-session capability key is not enforced."; exit 1 }

    # (3) path traversal (with the valid key) -> non-200 (path-confinement)
    $traversal = "$baseUrl../Program.cs?$keyQuery"
    $tstatus = 0
    try { $r = Invoke-WebRequest -Uri $traversal -UseBasicParsing -TimeoutSec 10; $tstatus = [int]$r.StatusCode }
    catch { $tstatus = [int]$_.Exception.Response.StatusCode }
    if ($tstatus -eq 200) { Write-Output "smoke: a path-traversal request ($traversal) returned 200 - path-confinement is not enforced."; exit 1 }

    Write-Output "smoke: charter review served the plan over loopback with SDK injection, capability enforcement, and path-confinement."
    exit 0
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
