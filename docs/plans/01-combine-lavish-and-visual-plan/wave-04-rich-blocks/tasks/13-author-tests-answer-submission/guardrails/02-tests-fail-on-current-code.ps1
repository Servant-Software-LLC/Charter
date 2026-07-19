# catches: tautological AnswerApi tests that PASS against the un-routed wave-3 server (e.g. a test that only
#          asserts the server starts). With the build green (guardrail 01), a non-zero test exit means the
#          tests RAN and FAILED at runtime because POST /api/{key}/answers + GET /api/answers are not routed
#          yet = a real red. A zero exit means the tests assert nothing the wave-3 server does not already do.
$test = dotnet test tests/Charter.Server.Tests/Charter.Server.Tests.csproj -c Debug --filter "Category=AnswerApi" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -eq 0) {
    Write-Output "The Category=AnswerApi tests PASSED (or matched none) against the un-routed server - not a real TDD red. The answer round-trip (POST /api/{key}/answers -> GET /api/answers), target routing, CSRF, and the key gate must FAIL until task 14 routes them."
    exit 1
}
exit 0
