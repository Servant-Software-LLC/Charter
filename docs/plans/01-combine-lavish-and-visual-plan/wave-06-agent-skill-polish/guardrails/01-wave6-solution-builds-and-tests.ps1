# catches: a wave-6 merged HEAD that does not build, or whose full test suite is not green - the wave's
#          terminal postcondition and (wave 6 being the FINAL wave) the whole-plan terminal soundness
#          boundary. This is the gate that proves the shipped-skill / README / CLI-banner polish did NOT
#          regress the 100-test baseline (Charter.Core.Tests 75 + Charter.Server.Tests 25). LOCAL (no scope
#          sidecar): a whole-solution build + whole-suite test is a terminal postcondition run ONCE on the
#          merged HEAD, not a per-union invariant - marking it scope:"integration" would re-run it at the
#          three-leaf fan-in union where a sibling has NOT merged yet and false-RED a correct partial merge
#          (#125/#165). The scope:"integration" GR2028 re-run for this fan-in wave is the union-clean check
#          (02-union-clean), kept separate so it - not this whole-suite check - runs at the union.
$build = dotnet build Charter.sln -c Debug 2>&1 | Out-String
Write-Output $build
if ($LASTEXITCODE -ne 0) {
    Write-Output "wave-6 exit gate: the solution does not build on the merged HEAD."
    exit 1
}
$test = dotnet test Charter.sln -c Debug --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output "----- FAILURE DETAIL -----"
    ($test -split "`n" | Select-String -Pattern "Failed|error|Exception|Assert" -SimpleMatch:$false) | ForEach-Object { Write-Output $_.Line }
    Write-Output "wave-6 exit gate: the full test suite is not green on the merged HEAD - the shipped skill / README / banner polish must not regress the 100-test baseline."
    exit 1
}
exit 0
