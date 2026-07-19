# catches: an author-tests task that writes only a trivial store test (e.g. one Enqueue test) and skips
#          the load-bearing behaviors - the thread-safe Drain and the CONCURRENCY race (the plan's flagged
#          store-concurrency open item: a concurrent poll + prompts must not lose or corrupt annotations).
#          Lower-bound presence check: a token present != the behavior fully asserted, but its ABSENCE
#          proves the behavior is unwritten. Scoped to this task's AnnotationStore test files only.
$files = Get-ChildItem -Path tests/Charter.Server.Tests -Recurse -Filter *.cs -File |
    Where-Object { (Get-Content -Raw $_.FullName) -match 'AnnotationStore' }
if (-not $files) {
    Write-Output "No AnnotationStore test file found under tests/Charter.Server.Tests - the author-tests task produced no covered store tests."
    exit 1
}
$content = ($files | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"
$required = [ordered]@{
    'category trait'   = 'Trait\("Category",\s*"AnnotationStore"\)'
    'enqueue'          = 'Enqueue'
    'drain'            = 'Drain'
    'concurrency race' = '(Task\.WhenAll|Parallel|Task\.Run)'
}
$missing = @()
foreach ($k in $required.Keys) {
    if ($content -notmatch $required[$k]) { $missing += "$k (/$($required[$k])/)" }
}
if ($missing.Count -gt 0) {
    Write-Output ("AnnotationStore tests are missing required behaviors: " + ($missing -join '; ') + " - the store's thread-safe Enqueue/Drain and the concurrency race must be asserted.")
    exit 1
}
exit 0
