# catches: a QuestionForm author-tests task that skips the load-bearing facts - classification to
#          BlockKind.Question, the native <form> with mode-matched inputs, and the question-id correlation -
#          or names the domain in comments only (no real [Fact]/[Theory]). Lower-bound STRUCTURAL presence
#          check scoped to this task's QuestionForm test file only.
$files = Get-ChildItem -Path tests/Charter.Core.Tests -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue |
    Where-Object { (Get-Content -Raw $_.FullName) -match 'QuestionForm' }
if (-not $files) {
    Write-Output "No QuestionForm test file found under tests/Charter.Core.Tests - the author-tests task produced no covered tests."
    exit 1
}
$content = ($files | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"
$required = [ordered]@{
    'category trait'         = 'Trait\("Category",\s*"QuestionForm"\)'
    'classification'         = 'BlockKind\.Question'
    'a native form'          = '<form'
    'mode-matched input'     = '(?i)radio|checkbox|<textarea|type=\\?"?number'
    'question-id correlation'= 'data-question-id|questionId|question-id'
    'a real test attribute'  = '(?m)^\s*\[(Fact|Theory)\]'
}
$missing = @()
foreach ($k in $required.Keys) {
    if ($content -notmatch $required[$k]) { $missing += "$k (/$($required[$k])/)" }
}
if ($missing.Count -gt 0) {
    Write-Output ("QuestionForm tests are missing required coverage: " + ($missing -join '; ') + ".")
    exit 1
}
exit 0
