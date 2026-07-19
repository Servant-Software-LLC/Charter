# catches: tautological ComparisonBlock tests that PASS against the current renderer (which classifies
#          :::comparison to Note with no per-row sub-anchors). With the build green (guardrail 01), a non-zero
#          test exit means the tests RAN and FAILED because classification, per-row sub-anchors, and the
#          sub-anchor source-map round-trip are absent = a real TDD red. A zero exit means they are tautological.
$test = dotnet test tests/Charter.Core.Tests/Charter.Core.Tests.csproj -c Debug --filter "Category=ComparisonBlock" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -eq 0) {
    Write-Output "The Category=ComparisonBlock tests PASSED (or matched none) against the current renderer - not a real TDD red. Classification to BlockKind.Comparison, per-row sub-anchors, and their source-map round-trip must FAIL until task 06 implements them."
    exit 1
}
exit 0
