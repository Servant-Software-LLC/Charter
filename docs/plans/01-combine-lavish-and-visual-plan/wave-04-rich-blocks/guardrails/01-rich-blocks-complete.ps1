# catches: a wave-4 merged HEAD that does not build, or whose full test suite is not green - the wave's
#          terminal postcondition and (wave 4 being the last AUTHORED wave for now) the whole-plan terminal
#          soundness boundary. LOCAL (no scope sidecar): a whole-solution build + whole-suite test is a
#          terminal postcondition run ONCE on the merged HEAD, not a per-union invariant - marking it
#          scope:"integration" would re-run it at the internal fan-in unions (04->06->08->12, 09->10,
#          11->12, 13->14) where a downstream renderer/answer task has NOT merged yet and false-RED a
#          correct partial merge (#125/#165). A genuine whole-suite invocation satisfies GR2028 for this
#          multi-leaf/fan-in wave.
$build = dotnet build Charter.sln -c Debug 2>&1 | Out-String
Write-Output $build
if ($LASTEXITCODE -ne 0) {
    Write-Output "wave-4 exit gate: the solution does not build on the merged HEAD."
    exit 1
}
$test = dotnet test Charter.sln -c Debug --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output "----- FAILURE DETAIL -----"
    ($test -split "`n" | Select-String -Pattern "Failed|error|Exception|Assert" -SimpleMatch:$false) | ForEach-Object { Write-Output $_.Line }
    Write-Output "wave-4 exit gate: the full test suite is not green on the merged HEAD."
    exit 1
}
exit 0
