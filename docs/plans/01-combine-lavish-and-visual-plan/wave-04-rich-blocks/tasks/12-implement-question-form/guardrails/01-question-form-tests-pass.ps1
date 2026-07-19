# catches: a :::question renderer that does not satisfy the QuestionForm golden tests - classification, the
#          native <form> with mode-matched inputs, the block-level stable id, or the question-id correlation.
#          Filtered to THIS task's Category so a pre-existing renderer golden is not swept in (#193). Re-emits
#          the failing assertion/exception lines at the END for the harness retry tail (#179).
$test = dotnet test tests/Charter.Core.Tests/Charter.Core.Tests.csproj -c Debug --filter "Category=QuestionForm" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    ($test -split "`n" | Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Expected:|Actual:' -SimpleMatch:$false) |
        ForEach-Object { $_.Line } | Select-Object -First 40 | ForEach-Object { Write-Output $_ }
    Write-Output "QuestionForm tests are failing - the :::question classifier/renderer does not satisfy its tests (native <form>, mode-matched inputs, question-id correlation). See details above."
    exit 1
}
exit 0
