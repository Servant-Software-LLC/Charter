# catches: a QuestionSchema test file (or the QuestionSpec stub) that does not COMPILE - garbage exits
#          dotnet test non-zero identically to a real red, so without a build gate the red is gameable (#155).
#          With the minimal QuestionSpec stub this task writes, the test project MUST build; the red is the
#          throwing NotImplementedException stub, proven by guardrail 02.
dotnet build tests/Charter.Core.Tests/Charter.Core.Tests.csproj -c Debug --nologo -v q 2>&1 | Out-String | Write-Output
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Charter.Core.Tests does not build with the QuestionSchema tests + QuestionSpec stub present - the test file or the stub is not type-correct."
    exit 1
}
exit 0
