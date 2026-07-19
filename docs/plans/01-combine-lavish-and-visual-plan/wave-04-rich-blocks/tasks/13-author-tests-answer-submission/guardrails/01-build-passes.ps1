# catches: an AnswerApi test file that does not COMPILE - garbage exits dotnet test non-zero identically to
#          a real runtime red, so without a build gate the red is gameable (#155). The AnswerApi tests
#          reference only already-materialized types (ReviewServer, ReviewSession, HttpClient, JsonDocument) -
#          so the test project MUST build; the red is a RUNTIME failure (unrouted /api/answers), proven by
#          guardrail 02.
dotnet build tests/Charter.Server.Tests/Charter.Server.Tests.csproj -c Debug --nologo -v q 2>&1 | Out-String | Write-Output
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Charter.Server.Tests does not build with the AnswerApi tests present - the test file is not type-correct (it must compile against the existing ReviewServer + HttpClient surface)."
    exit 1
}
exit 0
