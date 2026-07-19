# catches: tautological QuestionForm tests that PASS against the current renderer (which classifies
#          :::question to Note - no <form>). With the build green (guardrail 01), a non-zero test exit means
#          the tests RAN and FAILED because classification and the native <form> rendering are absent = a real
#          TDD red. A zero exit means they are tautological.
$test = dotnet test tests/Charter.Core.Tests/Charter.Core.Tests.csproj -c Debug --filter "Category=QuestionForm" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -eq 0) {
    Write-Output "The Category=QuestionForm tests PASSED (or matched none) against the current renderer - not a real TDD red. Classification to BlockKind.Question and the native <form> rendering (mode-matched inputs + question-id correlation) must FAIL until task 12 implements them."
    exit 1
}
exit 0
