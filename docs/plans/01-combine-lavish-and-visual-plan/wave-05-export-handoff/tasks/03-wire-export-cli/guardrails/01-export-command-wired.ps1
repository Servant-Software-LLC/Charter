# catches: a Charter.Cli that names ArtifactExporter only in a comment/using but never INVOKES it (the
#          export command inert). Structural (#76): require a dotted ArtifactExporter.Export( CALL.
#          SINGLE ANCHORED PATTERN, not two independent regexes (#302-review WEAK finding): the earlier
#          two-check form (bare 'ArtifactExporter' present ANYWHERE + bare '.Export(' present ANYWHERE,
#          unrelated to each other) is satisfied by e.g. "// TODO ArtifactExporter" plus an UNRELATED
#          "diagnosticsExporter.Export(...)" call elsewhere in the file - neither check alone proves the
#          real symbol was actually invoked. Anchoring both halves into ONE pattern closes that gap.
#          Grep scoped to the one file this task owns (Program.cs).
$prog = Get-Content -Raw src/Charter.Cli/Program.cs
if ($prog -notmatch 'ArtifactExporter\.Export\s*\(') {
    Write-Output "src/Charter.Cli/Program.cs never invokes ArtifactExporter.Export(...) as a single anchored call - a bare mention of the type name and/or an unrelated .Export( call elsewhere does not satisfy this."
    exit 1
}
exit 0
