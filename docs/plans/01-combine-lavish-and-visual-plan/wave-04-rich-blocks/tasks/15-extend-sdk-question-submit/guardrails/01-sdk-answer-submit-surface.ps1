# catches: an SDK edit that claims question-form submit but is missing the load-bearing surface, OR that
#          REGRESSED the existing annotation SDK (dropped the CharterAnnotate namespace, the three annotation
#          kinds, the postMessage boundary, or the MIT/Lavish attribution). Structural presence check scoped
#          to the one SDK file. (No JS lint/bundle - greenfield sdk/, no node toolchain: the real browser
#          submit behavior is verified server-side by the AnswerApi round-trip + surfaced, not checked here.)
$sdk = "sdk/charter-annotate.js"
$content = Get-Content -Raw $sdk
$required = [ordered]@{
    'CharterAnnotate namespace (preserved)' = 'CharterAnnotate'
    'postMessage boundary (preserved)'      = 'postMessage'
    'diagram-node annotation (preserved)'   = '(diagram|node)'
    'MIT attribution (preserved)'           = 'MIT'
    'Lavish attribution (preserved)'        = 'Lavish'
    'answers route (new)'                   = '[''"`]/api/[^''"`]*answers|fetch\([\s\S]{0,40}answers|answers[\s\S]{0,40}fetch\('
    'answer POST over HTTP boundary (new)'  = 'fetch|XMLHttpRequest'
    'question-form submit hook (new)'       = 'submit'
}
$missing = @()
foreach ($k in $required.Keys) {
    if ($content -notmatch $required[$k]) { $missing += "$k (/$($required[$k])/)" }
}
if ($missing.Count -gt 0) {
    Write-Output ("sdk/charter-annotate.js is missing required surface (existing SDK preserved + answers-submit added): " + ($missing -join '; ') + ".")
    exit 1
}
exit 0
