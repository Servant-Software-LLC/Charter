# catches: a banner edit (this task's writeScope is all of src/Charter.Cli/) that ALSO mangles the verb
#          DISPATCH - e.g. renames a verb string so `charter export` silently stops routing (F6). The build +
#          the Charter.Core/Server suites do NOT cover the CLI dispatch (there is no Charter.Cli.Tests), so a
#          subtle change like retyping a dispatch literal passes them; this is the structural backing (#221)
#          for the prompt's prose-only "do not touch the verb dispatch" prohibition. Assert each of the four
#          dispatch guards survives verbatim. Cheap static grep (runs before the live smoke), scoped to the
#          one file the dispatch lives in.
$prog = Get-Content -Raw "src/Charter.Cli/Program.cs"
$dispatch = @('args[0] == "render"', 'args[0] == "review"', 'args[0] == "export"', 'args[0] == "handoff"')
$missing = @()
foreach ($d in $dispatch) {
    if ($prog -notmatch [regex]::Escape($d)) { $missing += $d }
}
if ($missing.Count -gt 0) {
    Write-Output ("src/Charter.Cli/Program.cs is missing verb-dispatch guard(s): " + ($missing -join '; ') + " - the banner edit must not disturb the render/review/export/handoff dispatch.")
    exit 1
}
exit 0
