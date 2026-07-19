# catches: a DiffBlock test file that does not COMPILE - garbage exits dotnet test non-zero identically to a
#          real runtime red, so without a build gate the red signal is gameable (#155). The DiffBlock tests
#          reference only already-materialized types (BlockDocument, Block.StableId, CharterRenderer,
#          SourceMap, BlockKind.Diff from task 01) - so the test project MUST build; the red is a RUNTIME
#          failure (:::diff still renders as Note), proven by guardrail 02.
dotnet build tests/Charter.Core.Tests/Charter.Core.Tests.csproj -c Debug --nologo -v q 2>&1 | Out-String | Write-Output
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Charter.Core.Tests does not build with the DiffBlock tests present - the test file is not type-correct (it must compile against the existing renderer surface + BlockKind.Diff)."
    exit 1
}
exit 0
