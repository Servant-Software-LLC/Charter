# catches: an author-tests task that writes only a trivial subset and skips the load-bearing handoff
#          behaviors, so the wave goes green having never encoded the diagram/diff fence conversion or the
#          open-question flag. Lower bound: a token present != the behavior fully asserted, but its
#          ABSENCE proves the behavior is unwritten. Scoped to this task's own test file.
#          Post-#302-review hardening: the PRE-hardening token set ('mermaid'/'diff' as BARE substrings,
#          no Note/Warn/Comparison/answered-question/round-trip tokens at all) was satisfied by a 2-test
#          file covering only Diagram + unanswered-Question - 10 of 13 enumerated behaviors were
#          unenforced, so an Emit whose switch handled only Diagram/Question (silently dropping
#          Prose/Heading/List/Table/Code/Note/Warn/Comparison - a broken, lossy handoff) passed 04/05/06
#          and the exit gate. The tokens below are FORCING: each is satisfied only by the specific test it
#          names, and 'diff' is anchored to the fence marker so it can't match inside ordinary English
#          ("different").
$path = 'tests/Charter.Core.Tests/HandoffMarkdownTests.cs'
if (-not (Test-Path $path)) {
    Write-Output "$path not found - the author-tests task produced no HandoffMarkdown test file."
    exit 1
}
$content = Get-Content -Raw -Path $path

$required = [ordered]@{
    'Category trait'          = '\[Trait\("Category",\s*"HandoffMarkdown"\)\]'
    'Emit call'                = 'HandoffMarkdown\.Emit'
    'diagram -> mermaid fence (anchored to the fence marker)' = '```mermaid'
    'diff -> diff fence (anchored - a bare "diff" matches "different")' = '```diff'
    'open-question flag'       = 'Open question'
    'Note -> labeled blockquote (test 2)' = '\*\*Note:\*\*'
    'Warn -> labeled blockquote (test 3)' = '\*\*Warning:\*\*'
    'Comparison scenario present (test 4)' = ':::comparison'
    'answered-question resolution, distinct from the open-question line (test 7)' = 'Answered:'
    'self-parse round-trip (test 10)' = 'BlockDocument\.Parse'
    'no annotation-loop artifacts (test 11)' = 'data-charter-sdk'
    'a real test attribute'    = '\[(Fact|Theory)\]'
}
$missing = @()
foreach ($k in $required.Keys) {
    if ($content -notmatch $required[$k]) { $missing += ("$k [/$($required[$k])/]") }
}
if ($missing.Count -gt 0) {
    Write-Output ("Required behavior token(s) absent from HandoffMarkdownTests.cs: " + ($missing -join '; '))
    Write-Output "The action prompt enumerates these behaviors; each needs at least one test."
    exit 1
}
exit 0
