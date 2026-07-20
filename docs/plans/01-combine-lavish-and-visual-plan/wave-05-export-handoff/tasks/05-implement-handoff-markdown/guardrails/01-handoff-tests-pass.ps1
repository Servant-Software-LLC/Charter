# catches: a HandoffMarkdown implementation that does not satisfy the HandoffMarkdownTests golden
#          behaviors - block pass-through, note/warn blockquote conversion, diagram/diff fence conversion,
#          question resolution/open-flagging, the global no-':::'-leak proof, or the self-parse
#          round-trip. Re-emits the failing assertion/exception lines at the END so they reach the harness
#          retry tail (#179).
$test = dotnet test tests/Charter.Core.Tests/Charter.Core.Tests.csproj -c Debug --filter "Category=HandoffMarkdown" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    ($test -split "`n" | Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Expected:|Actual:' -SimpleMatch:$false) |
        ForEach-Object { $_.Line } | Select-Object -First 40 | ForEach-Object { Write-Output $_ }
    Write-Output "HandoffMarkdown tests are failing - the handoff implementation does not satisfy its tests. See details above."
    exit 1
}
exit 0
