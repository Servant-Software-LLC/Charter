# catches: an implementation that does not actually make the authored ReviewServer tests pass (a stub
#          still throwing, a subset failing, or the loopback serve integration test broken). Re-emits
#          failure detail to the stdout tail (#179).
$log = dotnet test tests/Charter.Server.Tests/Charter.Server.Tests.csproj -c Debug --filter "Category=ReviewServer" --nologo 2>&1 | Out-String
Write-Output $log
if ($LASTEXITCODE -ne 0) {
    Write-Output "----- FAILURE DETAIL -----"
    ($log -split "`n" | Select-String -Pattern "Failed|error|Exception|Assert" -SimpleMatch:$false) | ForEach-Object { Write-Output $_.Line }
    Write-Output "ReviewServer tests did NOT all pass - implement Charter.Server so every Category=ReviewServer test is green."
    exit 1
}
exit 0
