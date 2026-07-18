# catches: authored ReviewServer tests that PASS against the NotImplementedException stubs (a
#          tautological, not-real TDD red). With the build green (guardrail 01), a non-zero test
#          exit here means the tests RAN and FAILED against the stubs = a real red.
$test = dotnet test tests/Charter.Server.Tests/Charter.Server.Tests.csproj -c Debug --filter "Category=ReviewServer" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -eq 0) {
    Write-Output "The Category=ReviewServer tests PASSED (or matched none) against the stubs - they are not a real TDD red. They must COMPILE and FAIL against the NotImplementedException stubs."
    exit 1
}
exit 0
