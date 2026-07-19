# catches: a QuestionSpec implementation that does not satisfy the QuestionSchema tests - a valid body must
#          parse to a well-formed QuestionSpec and the known-bad body must be REJECTED by validation.
#          Filtered to THIS task's Category. Re-emits the failing assertion/exception lines at the END for the
#          harness retry tail (#179).
$test = dotnet test tests/Charter.Core.Tests/Charter.Core.Tests.csproj -c Debug --filter "Category=QuestionSchema" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    ($test -split "`n" | Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Expected:|Actual:' -SimpleMatch:$false) |
        ForEach-Object { $_.Line } | Select-Object -First 40 | ForEach-Object { Write-Output $_ }
    Write-Output "QuestionSchema tests are failing - QuestionSpec parse/validate does not satisfy its tests (valid parse + known-bad reject). See details above."
    exit 1
}
exit 0
