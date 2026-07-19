# catches: an answer-submission change that REGRESSES the wave-3 annotation contract - e.g. it altered the
#          /api/poll response shape or the /api/{key}/prompts route to add answers, breaking the existing
#          Category=AnnotationApi tests (the #193 shared-contract-break the dedicated /answers route exists to
#          avoid). Runs the wave-3 annotation + review-server tests and requires them still green. Re-emits
#          the failing lines at the END for the harness retry tail (#179).
$test = dotnet test tests/Charter.Server.Tests/Charter.Server.Tests.csproj -c Debug --filter "Category=AnnotationApi|Category=ReviewServer" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    ($test -split "`n" | Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Expected:|Actual:' -SimpleMatch:$false) |
        ForEach-Object { $_.Line } | Select-Object -First 40 | ForEach-Object { Write-Output $_ }
    Write-Output "The wave-3 annotation/review-server tests regressed - the answers route must be ADDITIVE (dedicated /api/answers), not a change to the existing /prompts + /poll contract. See details above."
    exit 1
}
exit 0
