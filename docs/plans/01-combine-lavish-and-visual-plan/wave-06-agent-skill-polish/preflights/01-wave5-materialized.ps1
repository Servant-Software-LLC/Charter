# catches: wave 6 starting before wave 5's export + handoff materialized on the branch. Wave 6 ships the
#          `charter` usage SKILL and makes the README + CLI banner TRUTHFUL, all of which describe the real
#          verb surface `charter render` / `charter review` / `charter export` / `charter handoff`. If wave 5
#          (ArtifactExporter + HandoffMarkdown + their CLI verbs) has NOT landed, the skill/README would
#          document commands that do not exist. POSITIVE + monotone-safe (assert-present, the #181 green-start
#          at the wave boundary): confirm the wave-5 surface is present AND the wave 1-5 suite is green BEFORE
#          this wave's DAG spends a turn. Wave 6 authors NO new tests (its deliverables are a shipped skill +
#          doc/banner truthfulness), so the WHOLE suite is safe to run here - there are no about-to-be-authored
#          red tests to exclude (contrast the mid-TDD waves, which --filter-scope their green-start).
$mustExist = @(
    @{ Path = 'src/Charter.Core/ArtifactExporter.cs'; Marker = 'public\s+static\s+string\s+Export'; What = 'the wave-5 exporter that `charter export` is wired to (the skill export playbook cites it)' },
    @{ Path = 'src/Charter.Core/HandoffMarkdown.cs';   Marker = 'public\s+static\s+string\s+Emit';   What = 'the wave-5 handoff emitter that `charter handoff` is wired to (the skill handoff playbook cites it)' },
    @{ Path = 'src/Charter.Cli/Program.cs';            Marker = 'BuildExportRoot';                    What = 'the `charter export` verb the skill + README document' },
    @{ Path = 'src/Charter.Cli/Program.cs';            Marker = 'BuildHandoffRoot';                   What = 'the `charter handoff` verb the skill + README document' }
)
foreach ($m in $mustExist) {
    if (-not (Test-Path $m.Path)) {
        Write-Output "wave-6 entry gate: $($m.Path) is absent - wave 5 has not materialized on the branch ($($m.What))."
        exit 1
    }
    if ((Get-Content -Raw $m.Path) -notmatch $m.Marker) {
        Write-Output "wave-6 entry gate: $($m.Path) does not carry /$($m.Marker)/ - $($m.What) is missing."
        exit 1
    }
}
$test = dotnet test Charter.sln -c Debug --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output "----- FAILURE DETAIL -----"
    ($test -split "`n" | Select-String -Pattern "Failed|error|Exception|Assert" -SimpleMatch:$false) | ForEach-Object { Write-Output $_.Line }
    Write-Output "wave-6 entry gate: the wave 1-5 test suite is not green on the starting HEAD - fix the pre-existing breakage before wave 6 builds the shipped skill + polish on top of it."
    exit 1
}
exit 0
