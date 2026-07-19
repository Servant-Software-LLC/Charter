# catches: an annotation-API implementation that does not satisfy the authored AnnotationApi tests - the
#          M3 acceptance round-trip (POST /api/{key}/prompts -> GET /api/poll returns the annotation with
#          the correct SourceMap.LineForAnchor source line), /api/sessions, the /events SSE stream, or the
#          CSRF/same-origin rejection. Re-emits the failing assertion/exception lines at the END so they
#          reach the harness retry-feedback tail (#179).
$test = dotnet test tests/Charter.Server.Tests/Charter.Server.Tests.csproj -c Debug --filter "Category=AnnotationApi" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    ($test -split "`n" | Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Expected:|Actual:' -SimpleMatch:$false) |
        ForEach-Object { $_.Line } | Select-Object -First 40 | ForEach-Object { Write-Output $_ }
    Write-Output "AnnotationApi tests are failing - the annotation HTTP API does not satisfy its tests (round-trip / anchor source-line / sessions / events / CSRF). See details above."
    exit 1
}
exit 0
