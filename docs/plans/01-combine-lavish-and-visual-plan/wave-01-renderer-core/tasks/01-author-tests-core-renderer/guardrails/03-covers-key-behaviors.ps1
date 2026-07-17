# catches: an author-tests task that writes only a trivial subset (e.g. one stable-ID test) and skips
#          the load-bearing behaviors - so the wave goes green having never encoded golden-HTML
#          rendering or the ANCHOR-SURVIVAL proof (the plan's deepest risk-reduction, the reason the
#          standalone M0 spike was dropped). Lower bound: a token present != the behavior fully
#          asserted, but its ABSENCE proves the behavior is unwritten. Scoped to this task's
#          CoreRenderer test files only (grep-scope, not the whole tree).
$files = Get-ChildItem -Path tests/Charter.Core.Tests -Recurse -Filter *.cs -File |
    Where-Object { (Get-Content -Raw $_.FullName) -match 'CoreRenderer' }
if (-not $files) {
    Write-Output "No CoreRenderer-traited test file found under tests/Charter.Core.Tests - the author-tests task produced no covered tests."
    exit 1
}
$content = ($files | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"

# Each behavior the action prompt enumerates must appear (lower-bound presence check).
$required = [ordered]@{
    'stable-ID determinism'          = 'StableId'
    'golden HTML rendering'          = '\.Render\s*\('
    'source-map line mapping'        = 'SourceMap'
    'anchor-survival (load-bearing)' = 'Anchor'
    'anchor-survival edit-then-resolve' = 'Surviv'
}
$missing = @()
foreach ($k in $required.Keys) {
    if ($content -notmatch $required[$k]) { $missing += ("$k [/$($required[$k])/]") }
}
if ($missing.Count -gt 0) {
    Write-Output ("Required behavior token(s) absent from the CoreRenderer tests: " + ($missing -join '; '))
    Write-Output "The action prompt enumerates these behaviors; each needs at least one test. The anchor-survival test (Anchor + Surviv) is load-bearing."
    exit 1
}
exit 0
