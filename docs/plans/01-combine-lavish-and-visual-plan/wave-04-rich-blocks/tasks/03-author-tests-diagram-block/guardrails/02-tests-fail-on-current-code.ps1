# catches: tautological DiagramBlock tests that PASS against the current renderer (which still classifies
#          :::diagram to Note) - e.g. a test that asserts nothing the renderer does not already satisfy. With
#          the build green (guardrail 01), a non-zero test exit here means the tests RAN and FAILED at runtime
#          because :::diagram is not yet a Diagram (renders as <div class="note">, no mermaid runtime) = a
#          real TDD red. A zero exit means the tests are tautological.
$test = dotnet test tests/Charter.Core.Tests/Charter.Core.Tests.csproj -c Debug --filter "Category=DiagramBlock" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -eq 0) {
    Write-Output "The Category=DiagramBlock tests PASSED (or matched none) against the current renderer - not a real TDD red. Classification to BlockKind.Diagram, the <pre class=\"mermaid\"> markup, the inlined offline runtime, and the source-map round-trip must FAIL until task 04 implements them."
    exit 1
}
exit 0
