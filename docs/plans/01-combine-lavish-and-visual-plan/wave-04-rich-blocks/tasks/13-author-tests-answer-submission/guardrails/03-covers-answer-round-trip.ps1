# catches: an AnswerApi author-tests task that skips the load-bearing facts - the answer round-trip
#          (POST /api/{key}/answers -> GET /api/answers with questionId + values), the target (human/agent)
#          routing, and the CSRF check - or names the domain in comments only (no real [Fact]/[Theory]).
#          Lower-bound STRUCTURAL presence check scoped to this task's AnswerApi test files only.
$files = Get-ChildItem -Path tests/Charter.Server.Tests -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue |
    Where-Object { (Get-Content -Raw $_.FullName) -match 'AnswerApi' }
if (-not $files) {
    Write-Output "No AnswerApi test file found under tests/Charter.Server.Tests - the author-tests task produced no covered tests."
    exit 1
}
$content = ($files | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"
$required = [ordered]@{
    'category trait'     = 'Trait\("Category",\s*"AnswerApi"\)'
    'answers endpoint'   = 'answers'
    'question id'        = 'questionId|question-id'
    'target routing'     = '(?i)target'
    'CSRF / same-origin' = 'Origin'
    'a real test attribute' = '(?m)^\s*\[(Fact|Theory)\]'
}
$missing = @()
foreach ($k in $required.Keys) {
    if ($content -notmatch $required[$k]) { $missing += "$k (/$($required[$k])/)" }
}
if ($missing.Count -gt 0) {
    Write-Output ("AnswerApi tests are missing required coverage: " + ($missing -join '; ') + " - the answer round-trip, target routing, and CSRF must all be asserted.")
    exit 1
}
exit 0
