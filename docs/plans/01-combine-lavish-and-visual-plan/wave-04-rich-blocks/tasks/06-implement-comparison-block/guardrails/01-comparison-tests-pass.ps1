# catches: a :::comparison implementation that does not satisfy the ComparisonBlock golden tests -
#          classification, the block-level stable id, per-row content-derived sub-anchors, or the per-row
#          source-map round-trip. Filtered to THIS task's Category so a pre-existing diagram golden is not
#          swept in (#193). Re-emits the failing assertion/exception lines at the END for the harness retry
#          tail (#179).
$test = dotnet test tests/Charter.Core.Tests/Charter.Core.Tests.csproj -c Debug --filter "Category=ComparisonBlock" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    ($test -split "`n" | Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Expected:|Actual:' -SimpleMatch:$false) |
        ForEach-Object { $_.Line } | Select-Object -First 40 | ForEach-Object { Write-Output $_ }
    Write-Output "ComparisonBlock tests are failing - the :::comparison classifier/renderer/source-map does not satisfy its tests. See details above."
    exit 1
}
exit 0
