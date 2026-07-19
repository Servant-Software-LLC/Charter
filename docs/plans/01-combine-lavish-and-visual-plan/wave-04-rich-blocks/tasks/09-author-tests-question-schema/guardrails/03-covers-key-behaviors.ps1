# catches: a QuestionSchema author-tests task that skips the load-bearing facts - a valid parse, the
#          KNOWN-BAD validation reject (the anti-tautology teeth), the five modes + the human/agent target -
#          or names the domain in comments only (no real [Fact]/[Theory]). Lower-bound STRUCTURAL presence
#          check scoped to this task's QuestionSchema test file only.
$files = Get-ChildItem -Path tests/Charter.Core.Tests -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue |
    Where-Object { (Get-Content -Raw $_.FullName) -match 'QuestionSchema|QuestionSpec' }
if (-not $files) {
    Write-Output "No QuestionSchema test file found under tests/Charter.Core.Tests - the author-tests task produced no covered tests."
    exit 1
}
$content = ($files | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"
$required = [ordered]@{
    'category trait'        = 'Trait\("Category",\s*"QuestionSchema"\)'
    'the schema type'       = 'QuestionSpec'
    'known-bad reject'      = '(?i)Assert\.Throws|Assert\.False|invalid|missing|unknown'
    'mode surface'          = '(?i)mode|SingleSelect|FreeText'
    'human/agent target'    = '(?i)target|human|agent'
    'a real test attribute' = '(?m)^\s*\[(Fact|Theory)\]'
}
$missing = @()
foreach ($k in $required.Keys) {
    if ($content -notmatch $required[$k]) { $missing += "$k (/$($required[$k])/)" }
}
if ($missing.Count -gt 0) {
    Write-Output ("QuestionSchema tests are missing required coverage: " + ($missing -join '; ') + ".")
    exit 1
}
exit 0
