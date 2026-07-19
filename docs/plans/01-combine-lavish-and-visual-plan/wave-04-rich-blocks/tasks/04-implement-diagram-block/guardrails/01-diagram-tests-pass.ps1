# catches: a :::diagram implementation that does not satisfy the DiagramBlock golden tests - classification
#          to BlockKind.Diagram, the <pre class="mermaid" id="{stable-id}"> markup with the content-derived
#          anchor, the inlined offline Mermaid runtime + theme-aware init, or the source-map round-trip.
#          Re-emits the failing assertion/exception lines at the END so they reach the harness retry tail (#179).
$test = dotnet test tests/Charter.Core.Tests/Charter.Core.Tests.csproj -c Debug --filter "Category=DiagramBlock" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    ($test -split "`n" | Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Expected:|Actual:' -SimpleMatch:$false) |
        ForEach-Object { $_.Line } | Select-Object -First 40 | ForEach-Object { Write-Output $_ }
    Write-Output "DiagramBlock tests are failing - the :::diagram classifier/renderer does not satisfy its tests. See details above."
    exit 1
}
exit 0
