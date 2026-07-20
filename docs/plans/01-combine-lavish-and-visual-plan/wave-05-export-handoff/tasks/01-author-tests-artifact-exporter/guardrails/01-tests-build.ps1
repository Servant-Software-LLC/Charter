# catches: authored tests/stub that do NOT compile - a non-compiling project exits non-zero
#          identically to a real TDD red, so garbage would satisfy the fail-on-stubs check. The
#          build must be GREEN so guardrail 02's non-zero test exit is unambiguously a real red.
$build = dotnet build Charter.sln -c Debug 2>&1 | Out-String
Write-Output $build
if ($LASTEXITCODE -ne 0) {
    Write-Output "BUILD FAILED - the authored tests + stub do not compile. Fix them so the solution builds; a failing test is intended, a non-compiling project is not."
    exit 1
}
exit 0
