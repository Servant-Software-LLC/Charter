# catches: a "hollow" implementation that special-cases the exact test-fixture strings (or a fixed
#          literal answer) instead of really dispatching on the parsed block kind - a green test suite
#          could still hide a HandoffMarkdown.Emit that returns a hard-coded string. Structural: requires
#          a genuine SWITCH-EXPRESSION ARM over BlockKind (proving it dispatches over the real block
#          model, format single-sourced per invariant 3) and a genuine QuestionSpec.Parse( CALL (proving
#          question resolution parses the real schema). ANCHORED to real member access / a switch arm
#          (#302-review WEAK finding): the earlier bare 'BlockKind'/'QuestionSpec' name checks are
#          satisfied by EITHER appearing anywhere at all - a doc comment ("dispatches on BlockKind, uses
#          QuestionSpec") satisfies both with zero real dispatch logic behind it. The task 05 prompt
#          specifies a SWITCH EXPRESSION (`block.Kind switch { BlockKind.Note => ..., ... }`), not a
#          `case BlockKind.X:` statement form, so the arm pattern is `BlockKind\.\w+\s*=>`.
$path = "src/Charter.Core/HandoffMarkdown.cs"
if (-not (Test-Path $path)) {
    Write-Output "$path not found."
    exit 1
}
$content = Get-Content -Raw -Path $path
if ($content -notmatch 'BlockKind\.\w+\s*=>') {
    Write-Output "$path does not contain a real switch-expression arm over BlockKind (e.g. 'BlockKind.Note => ...') - a bare mention of the type name (a doc comment) does not prove real dispatch logic exists."
    exit 1
}
if ($content -notmatch 'QuestionSpec\.Parse\s*\(') {
    Write-Output "$path does not call QuestionSpec.Parse(...) - question resolution must parse the real schema, not string-match a fixture's literal JSON; a bare mention of the type name does not prove this."
    exit 1
}
exit 0
