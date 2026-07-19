# catches: tautological DiffBlock tests that PASS against the current renderer (which classifies :::diff to
#          Note with no per-line sub-anchors). With the build green (guardrail 01), a non-zero test exit means
#          the tests RAN and FAILED because classification, per-line sub-anchors, and their source-map
#          round-trip are absent = a real TDD red. A zero exit means they are tautological.
$test = dotnet test tests/Charter.Core.Tests/Charter.Core.Tests.csproj -c Debug --filter "Category=DiffBlock" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -eq 0) {
    Write-Output "The Category=DiffBlock tests PASSED (or matched none) against the current renderer - not a real TDD red. Classification to BlockKind.Diff, per-line sub-anchors, and their source-map round-trip must FAIL until task 08 implements them."
    exit 1
}
exit 0
