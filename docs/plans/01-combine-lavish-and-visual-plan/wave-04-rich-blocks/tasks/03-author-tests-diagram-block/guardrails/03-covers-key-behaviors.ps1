# catches: a DiagramBlock author-tests task that skips the load-bearing facts - classification to
#          BlockKind.Diagram, the mermaid markup + inlined offline runtime/init, and the source-map round-trip
#          - or that names the domain in comments only (no real [Fact]/[Theory]). Lower-bound STRUCTURAL
#          presence check scoped to this task's DiagramBlock test file only (grep-scope rule).
$files = Get-ChildItem -Path tests/Charter.Core.Tests -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue |
    Where-Object { (Get-Content -Raw $_.FullName) -match 'DiagramBlock' }
if (-not $files) {
    Write-Output "No DiagramBlock test file found under tests/Charter.Core.Tests - the author-tests task produced no covered tests."
    exit 1
}
$content = ($files | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"
$required = [ordered]@{
    'category trait'           = 'Trait\("Category",\s*"DiagramBlock"\)'
    'classification'           = 'BlockKind\.Diagram'
    'mermaid markup'           = 'mermaid'
    'theme-aware init'         = 'mermaid\.initialize|mermaid\.run'
    'source-map round-trip'    = 'SourceMap'
    'a real test attribute'    = '(?m)^\s*\[(Fact|Theory)\]'
}
$missing = @()
foreach ($k in $required.Keys) {
    if ($content -notmatch $required[$k]) { $missing += "$k (/$($required[$k])/)" }
}
if ($missing.Count -gt 0) {
    Write-Output ("DiagramBlock tests are missing required coverage: " + ($missing -join '; ') + ".")
    exit 1
}
exit 0
