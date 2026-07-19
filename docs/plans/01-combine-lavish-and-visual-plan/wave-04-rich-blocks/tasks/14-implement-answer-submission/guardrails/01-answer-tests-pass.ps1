# catches: an answer-submission implementation that does not satisfy the AnswerApi tests - the round-trip
#          (POST /api/{key}/answers -> GET /api/answers with the questionId + values), target preservation,
#          the CSRF rejection, or the key gate. The round-trip test drives the REAL ReviewServer.Start, so it
#          also proves the route is WIRED into the server (not dead code). Re-emits the failing assertion/
#          exception lines at the END for the harness retry tail (#179).
$test = dotnet test tests/Charter.Server.Tests/Charter.Server.Tests.csproj -c Debug --filter "Category=AnswerApi" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    ($test -split "`n" | Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Expected:|Actual:' -SimpleMatch:$false) |
        ForEach-Object { $_.Line } | Select-Object -First 40 | ForEach-Object { Write-Output $_ }
    Write-Output "AnswerApi tests are failing - the answer HTTP route does not satisfy its tests (round-trip / target / CSRF / key gate). See details above."
    exit 1
}
exit 0
