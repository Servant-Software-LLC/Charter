# catches: a Charter.Cli that names HandoffMarkdown only in a comment/using but never INVOKES it (the
#          handoff command inert). Structural (#76): require a dotted HandoffMarkdown.Emit( CALL.
#          SINGLE ANCHORED PATTERN, not two independent regexes (#302-review WEAK finding): the earlier
#          two-check form (bare 'HandoffMarkdown' present ANYWHERE + bare '.Emit(' present ANYWHERE,
#          unrelated to each other) is satisfied by e.g. a comment mentioning the type plus an UNRELATED
#          '.Emit(' call elsewhere in the file. Anchoring both halves into ONE pattern closes that gap.
#          Grep scoped to the one file this task owns (Program.cs).
$prog = Get-Content -Raw src/Charter.Cli/Program.cs
if ($prog -notmatch 'HandoffMarkdown\.Emit\s*\(') {
    Write-Output "src/Charter.Cli/Program.cs never invokes HandoffMarkdown.Emit(...) as a single anchored call - a bare mention of the type name and/or an unrelated .Emit( call elsewhere does not satisfy this."
    exit 1
}
exit 0
