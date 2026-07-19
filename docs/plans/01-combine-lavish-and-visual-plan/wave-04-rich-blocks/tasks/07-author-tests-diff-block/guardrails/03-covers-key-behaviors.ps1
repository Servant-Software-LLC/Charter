# catches: a DiffBlock author-tests task that skips the load-bearing facts - classification to BlockKind.Diff,
#          the per-line sub-anchors (the 'annotatable per-line' invariant), and the sub-anchor source-map
#          round-trip - or names the domain in comments only (no real [Fact]/[Theory]). Lower-bound STRUCTURAL
#          presence check scoped to this task's DiffBlock test file only.
$files = Get-ChildItem -Path tests/Charter.Core.Tests -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue |
    Where-Object { (Get-Content -Raw $_.FullName) -match 'DiffBlock' }
if (-not $files) {
    Write-Output "No DiffBlock test file found under tests/Charter.Core.Tests - the author-tests task produced no covered tests."
    exit 1
}
$content = ($files | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"
$required = [ordered]@{
    'category trait'         = 'Trait\("Category",\s*"DiffBlock"\)'
    'classification'         = 'BlockKind\.Diff'
    'per-line sub-anchor'    = 'data-anchor|StableId'
    'content-derived id'     = 'Block\.StableId'
    'source-map round-trip'  = 'SourceMap'
    'a real test attribute'  = '(?m)^\s*\[(Fact|Theory)\]'
}
$missing = @()
foreach ($k in $required.Keys) {
    if ($content -notmatch $required[$k]) { $missing += "$k (/$($required[$k])/)" }
}
if ($missing.Count -gt 0) {
    Write-Output ("DiffBlock tests are missing required coverage: " + ($missing -join '; ') + ".")
    exit 1
}
exit 0
