# catches: a task that claims the SDK but wrote no SDK file (or an empty placeholder) - sdk/charter-annotate.js
#          must exist and carry real content, since wave-3's whole point is replacing the wave-2 placeholder
#          script with the real annotation SDK. Scoped to the one SDK file this task owns.
$sdk = "sdk/charter-annotate.js"
if (-not (Test-Path $sdk)) {
    Write-Output "$sdk does not exist - the annotation SDK was never authored."
    exit 1
}
if ((Get-Item $sdk).Length -lt 400) {
    Write-Output "$sdk is present but under 400 bytes - it looks like an empty placeholder, not the real annotation SDK."
    exit 1
}
exit 0
