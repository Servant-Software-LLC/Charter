# catches: the wave-2 placeholder SDK still being served instead of the real annotation SDK - the SDK
#          file exists and is embedded, but ReviewServer still injects the placeholder comment, so the
#          browser gets no annotation loop (a green build + embedded resource cannot see this). Builds
#          Charter.Cli, starts `charter review` as a background server, polls the printed loopback URL, and
#          asserts the served body carries BOTH the data-charter-sdk marker AND the real SDK's
#          CharterAnnotate namespace (not just the placeholder) - the served-markup wiring proof (#66).
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("charter-sdk-smoke-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null
$proc = $null
try {
    $mdx = Join-Path $tmp "sample.mdx"
    $stdout = Join-Path $tmp "stdout.log"
    $stderr = Join-Path $tmp "stderr.log"
    "# Hello Charter`n`nA smoke paragraph." | Set-Content -Path $mdx -Encoding utf8

    # Build once so we launch a single, killable process (dotnet run would fork a child we couldn't stop).
    dotnet build src/Charter.Cli/Charter.Cli.csproj -c Debug 2>&1 | Out-String | Write-Output
    if ($LASTEXITCODE -ne 0) { Write-Output "sdk-smoke: Charter.Cli did not build."; exit 1 }
    $dll = "src/Charter.Cli/bin/Debug/net8.0/Charter.Cli.dll"
    if (-not (Test-Path $dll)) { Write-Output "sdk-smoke: built Charter.Cli.dll not found at $dll."; exit 1 }

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
        Write-Output "sdk-smoke: charter review did not print a loopback URL (http://127.0.0.1:<port>/?key=<key>) within the timeout."
        if (Test-Path $stderr) { Write-Output "--- stderr ---"; Get-Content $stderr | Write-Output }
        exit 1
    }

    $ok = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10
    if ($ok.StatusCode -ne 200) { Write-Output "sdk-smoke: served URL returned $($ok.StatusCode), expected 200."; exit 1 }
    if ($ok.Content -notmatch 'data-charter-sdk') {
        Write-Output "sdk-smoke: served body does not carry the data-charter-sdk marker - serve-time injection is broken."
        exit 1
    }
    if ($ok.Content -notmatch 'CharterAnnotate') {
        Write-Output "sdk-smoke: served body carries the marker but NOT the real SDK (CharterAnnotate) - the wave-2 placeholder is still being served instead of the real annotation SDK."
        exit 1
    }
    Write-Output "sdk-smoke: charter review served the real annotation SDK (CharterAnnotate) over loopback with the data-charter-sdk marker."
    exit 0
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
