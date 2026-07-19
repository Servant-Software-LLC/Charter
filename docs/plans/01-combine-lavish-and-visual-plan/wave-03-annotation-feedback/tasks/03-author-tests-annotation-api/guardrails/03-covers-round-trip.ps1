# catches: an author-tests task that skips the M3 acceptance - the round-trip (POST a prompt -> poll
#          returns it) and, load-bearing, the ANCHOR resolution (the returned annotation carries the
#          correct markdown source line via SourceMap.LineForAnchor - the browser half's server
#          counterpart) - or that drops /api/sessions, the /events SSE stream, or the CSRF check. Lower-
#          bound presence check, scoped to this task's AnnotationApi test files only.
$files = Get-ChildItem -Path tests/Charter.Server.Tests -Recurse -Filter *.cs -File |
    Where-Object { (Get-Content -Raw $_.FullName) -match 'AnnotationApi' }
if (-not $files) {
    Write-Output "No AnnotationApi test file found under tests/Charter.Server.Tests - the author-tests task produced no covered API tests."
    exit 1
}
$content = ($files | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"
$required = [ordered]@{
    'category trait'          = 'Trait\("Category",\s*"AnnotationApi"\)'
    'submit prompts endpoint' = 'prompts'
    'poll endpoint'           = 'poll'
    'sessions endpoint'       = 'sessions'
    'SSE events stream'       = '(events|event-stream)'
    'anchor source-line'      = '(LineForAnchor|SourceLine|SourceMap)'
    'CSRF / same-origin'      = '(Origin|[Cc]srf)'
}
$missing = @()
foreach ($k in $required.Keys) {
    if ($content -notmatch $required[$k]) { $missing += "$k (/$($required[$k])/)" }
}
if ($missing.Count -gt 0) {
    Write-Output ("AnnotationApi tests are missing required behaviors: " + ($missing -join '; ') + " - the round-trip + anchor source-line (the M3 acceptance), /api/sessions, /events SSE, and CSRF must all be asserted.")
    exit 1
}
exit 0
