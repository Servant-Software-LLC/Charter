# catches: a wave-1 merged HEAD that does not build, or whose CoreRenderer tests are not green - the
#          wave's terminal postcondition. LOCAL (no scope sidecar): wave 1 is a single linear chain
#          (01 -> 02 -> 03), one leaf, no fan-in union, so GR2028 integration re-run is not required.
$build = dotnet build Charter.sln -c Debug 2>&1 | Out-String
Write-Output $build
if ($LASTEXITCODE -ne 0) { Write-Output "wave-1 exit gate: the solution does not build on the merged HEAD."; exit 1 }
$test = dotnet test tests/Charter.Core.Tests/Charter.Core.Tests.csproj -c Debug --filter "Category=CoreRenderer" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) { Write-Output "wave-1 exit gate: the CoreRenderer tests are not green on the merged HEAD."; exit 1 }
exit 0
