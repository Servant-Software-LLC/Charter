# catches: an author-tests task that writes only a trivial subset and skips the load-bearing export
#          behaviors, so the wave goes green having never encoded asset inlining, the size-cap omission,
#          path-traversal refusal, file:// redaction, or the SDK-free guarantee. Lower bound: a token
#          present != the behavior fully asserted, but its ABSENCE proves the behavior is unwritten.
#          Scoped to this task's own test file.
$path = 'tests/Charter.Core.Tests/ArtifactExporterTests.cs'
if (-not (Test-Path $path)) {
    Write-Output "$path not found - the author-tests task produced no ArtifactExporter test file."
    exit 1
}
$content = Get-Content -Raw -Path $path

$required = [ordered]@{
    'Category trait'              = '\[Trait\("Category",\s*"ArtifactExporter"\)\]'
    'Export call'                  = 'ArtifactExporter\.Export'
    'data: URI inlining'           = 'data:'
    'file:// redaction (bare, no basename)' = 'file:///\[redacted\]'
    'size-cap / missing omission'  = 'data-charter-export-omitted'
    'SDK-free guarantee'           = 'data-charter-sdk'
    'cumulative total-cap test'    = 'total-cap-exceeded'
    # FORCING tokens (post-#302-review hardening): each below is satisfied ONLY by the specific
    # regression test it names, not by a passing mention/comment - a bare 'mermaid' token (the
    # pre-hardening form) is satisfied by ANY comment mentioning Mermaid, so a test file that
    # OMITS test 12/13/8 entirely still passed. These force the actual fixture content.
    'Mermaid script-region regression test (test 12 - the vendored library marker, not a bare "mermaid" mention)' = '__esbuild_esm_mermaid_nm'
    'sibling-directory path-confinement regression test (test 13)' = 'plan-evil'
    'tag-agnostic src= scan via :::custom-html (test 8)' = ':::custom-html'
    'tag-agnostic scan inlines a non-image MIME (test 8)' = 'video/mp4'
    'a real test attribute'        = '\[(Fact|Theory)\]'
}
$missing = @()
foreach ($k in $required.Keys) {
    if ($content -notmatch $required[$k]) { $missing += ("$k [/$($required[$k])/]") }
}
if ($missing.Count -gt 0) {
    Write-Output ("Required behavior token(s) absent from ArtifactExporterTests.cs: " + ($missing -join '; '))
    Write-Output "The action prompt enumerates these behaviors; each needs at least one test."
    exit 1
}
exit 0
