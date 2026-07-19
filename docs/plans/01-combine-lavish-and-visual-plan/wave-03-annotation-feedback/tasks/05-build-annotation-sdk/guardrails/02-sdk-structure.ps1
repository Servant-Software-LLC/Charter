# catches: an SDK file that exists but is missing its load-bearing surface - the postMessage boundary
#          (the narrow C#<->JS contract), the three annotation kinds (element / text-range / diagram-node),
#          the CharterAnnotate namespace the server's served-content guardrail asserts, and the MIT/Lavish
#          attribution the plan requires. Structural presence check scoped to the one SDK file.
#          (No JS lint/bundle check - greenfield sdk/, no node toolchain: an honest gap, not a fake green.)
$sdk = "sdk/charter-annotate.js"
$content = Get-Content -Raw $sdk
$required = [ordered]@{
    'postMessage boundary'      = 'postMessage'
    'CharterAnnotate namespace' = 'CharterAnnotate'
    'element annotation'        = 'element'
    'text-range annotation'     = '(text-range|textRange|text_range)'
    'diagram-node annotation'   = '(diagram|node)'
    'MIT attribution'           = 'MIT'
    'Lavish attribution'        = 'Lavish'
}
$missing = @()
foreach ($k in $required.Keys) {
    if ($content -notmatch $required[$k]) { $missing += "$k (/$($required[$k])/)" }
}
if ($missing.Count -gt 0) {
    Write-Output ("sdk/charter-annotate.js is missing required surface: " + ($missing -join '; ') + ".")
    exit 1
}
exit 0
