# catches: an AnnotationStore implementation that does not satisfy the authored store tests - including
#          the concurrency race (a lock/single-writer that still loses or duplicates annotations under
#          concurrent Enqueue + Drain). Re-emits the failing assertion/exception lines at the END so they
#          reach the harness retry-feedback tail (#179).
$test = dotnet test tests/Charter.Server.Tests/Charter.Server.Tests.csproj -c Debug --filter "Category=AnnotationStore" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    ($test -split "`n" | Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Expected:|Actual:' -SimpleMatch:$false) |
        ForEach-Object { $_.Line } | Select-Object -First 40 | ForEach-Object { Write-Output $_ }
    Write-Output "AnnotationStore tests are failing - the store implementation does not satisfy its tests (see details above)."
    exit 1
}
exit 0
