# catches: a wave-2 merged HEAD that does not build, or whose ReviewServer tests are not green - the
#          wave's terminal postcondition. LOCAL (no scope sidecar): wave 2 is a single linear chain
#          (01 -> 02 -> 03 -> 04), one leaf, no fan-in union, so a GR2028 integration re-run is not required.
$build = dotnet build Charter.sln -c Debug 2>&1 | Out-String
Write-Output $build
if ($LASTEXITCODE -ne 0) { Write-Output "wave-2 exit gate: the solution does not build on the merged HEAD."; exit 1 }
$test = dotnet test tests/Charter.Server.Tests/Charter.Server.Tests.csproj -c Debug --filter "Category=ReviewServer" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output "----- FAILURE DETAIL -----"
    ($test -split "`n" | Select-String -Pattern "Failed|error|Exception|Assert" -SimpleMatch:$false) | ForEach-Object { Write-Output $_.Line }
    Write-Output "wave-2 exit gate: the ReviewServer tests are not green on the merged HEAD."
    exit 1
}
exit 0
