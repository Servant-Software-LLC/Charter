<#
  install.ps1 — Windows bootstrap for Charter (NO .NET required).

  Downloads the prebuilt self-contained binary from the GitHub Release, installs it under
  %LOCALAPPDATA%\Programs\Charter, and adds it to the user PATH.

  Usage:
    irm https://raw.githubusercontent.com/Servant-Software-LLC/Charter/master/install.ps1 | iex
    .\install.ps1                  # latest release
    .\install.ps1 0.1.0-preview.1  # specific version
#>
[CmdletBinding()]
param([string]$Version)

$ErrorActionPreference = "Stop"
$Repo    = "Servant-Software-LLC/Charter"
$HomeDir = Join-Path $env:LOCALAPPDATA "Programs\Charter"
$rid     = "win-x64"

if (-not $Version) {
  $rel = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases" -Headers @{ "User-Agent" = "charter-install" }
  $Version = ($rel | Select-Object -First 1).tag_name
}
$Version = $Version -replace '^v',''
$tag   = "v$Version"
$asset = "charter-$Version-$rid.zip"
$url   = "https://github.com/$Repo/releases/download/$tag/$asset"
Write-Host "Installing Charter $Version ($rid)`n  from $url"

$tmp = New-Item -ItemType Directory -Path (Join-Path $env:TEMP ([guid]::NewGuid()))
try {
  $zip = Join-Path $tmp $asset
  Invoke-WebRequest $url -OutFile $zip
  try {
    Invoke-WebRequest "$url.sha256" -OutFile "$zip.sha256"
    $expected = (Get-Content "$zip.sha256").Split(" ")[0]
    $actual   = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    if ($expected -ne $actual) { throw "checksum mismatch for $asset" }
    Write-Host "  checksum OK"
  } catch { Write-Host "  (checksum skipped)" }

  if (Test-Path $HomeDir) { Remove-Item $HomeDir -Recurse -Force }
  New-Item -ItemType Directory -Path $HomeDir | Out-Null
  Expand-Archive -Path $zip -DestinationPath $HomeDir -Force
} finally { Remove-Item $tmp -Recurse -Force }

$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$HomeDir*") {
  [Environment]::SetEnvironmentVariable("Path", "$userPath;$HomeDir", "User")
  $env:Path = "$env:Path;$HomeDir"
  Write-Host "Added $HomeDir to your user PATH (restart shells to pick it up)."
}

Write-Host "`nCharter $Version installed to $HomeDir"
Write-Host "Run:  charter --version"
