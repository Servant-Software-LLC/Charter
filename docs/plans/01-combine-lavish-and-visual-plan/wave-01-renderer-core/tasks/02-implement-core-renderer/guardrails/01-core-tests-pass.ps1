# catches: an implementation that does not actually make the authored CoreRenderer tests pass (a
#          stub still throwing, or a subset failing). Re-emits failure detail to the stdout tail (#179).
$log = dotnet test tests/Charter.Core.Tests/Charter.Core.Tests.csproj -c Debug --filter "Category=CoreRenderer" --nologo 2>&1 | Out-String
Write-Output $log
if ($LASTEXITCODE -ne 0) {
    Write-Output "----- FAILURE DETAIL -----"
    ($log -split "`n" | Select-String -Pattern "Failed|error|Exception|Assert" -SimpleMatch:$false) | ForEach-Object { Write-Output $_.Line }
    Write-Output "CoreRenderer tests did NOT all pass - implement the renderer so every Category=CoreRenderer test is green."
    exit 1
}
exit 0
