# catches: tautological QuestionSchema tests that PASS against the throwing QuestionSpec stub - e.g. tests
#          that assert nothing about parse/validate. With the build green (guardrail 01), a non-zero test
#          exit here means the tests RAN and FAILED against the NotImplementedException stub = TDD red. A zero
#          exit means the tests are tautological (they do not exercise parse + the known-bad reject).
$test = dotnet test tests/Charter.Core.Tests/Charter.Core.Tests.csproj -c Debug --filter "Category=QuestionSchema" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -eq 0) {
    Write-Output "The Category=QuestionSchema tests PASSED (or matched none) against the throwing QuestionSpec stub - they are not a real TDD red. Valid parse AND the known-bad validation reject must FAIL until task 10 implements QuestionSpec."
    exit 1
}
exit 0
