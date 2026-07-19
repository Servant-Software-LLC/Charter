# catches: a wave-3 merged HEAD that does not build, or whose full test suite is not green - the wave's
#          terminal postcondition and (wave 3 being the last AUTHORED wave) the whole-plan terminal
#          soundness boundary. LOCAL (no scope sidecar): a whole-solution build + whole-suite test is a
#          terminal postcondition run ONCE on the merged HEAD, not a per-union invariant - marking it
#          scope:"integration" would re-run it at the internal 04+05 -> 06 fan-in union and false-RED a
#          correct partial merge (#125/#165). A genuine whole-suite invocation satisfies GR2028 for this
#          multi-leaf/fan-in wave.
$build = dotnet build Charter.sln -c Debug 2>&1 | Out-String
Write-Output $build
if ($LASTEXITCODE -ne 0) {
    Write-Output "wave-3 exit gate: the solution does not build on the merged HEAD."
    exit 1
}
$test = dotnet test Charter.sln -c Debug --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output "----- FAILURE DETAIL -----"
    ($test -split "`n" | Select-String -Pattern "Failed|error|Exception|Assert" -SimpleMatch:$false) | ForEach-Object { Write-Output $_.Line }
    Write-Output "wave-3 exit gate: the full test suite is not green on the merged HEAD."
    exit 1
}
exit 0
