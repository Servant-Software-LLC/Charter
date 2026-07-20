# catches: wave 5 starting before wave 4's rich/interactive blocks materialized on the branch - the
#          export path re-renders through CharterRenderer (invariant 1: the SDK-free, offline-inlined
#          artifact wave 4 completed) and the handoff path converts EVERY wave-4 block kind (:::diagram,
#          :::comparison, :::diff, :::question) to plain markdown, so it needs the real wave-4 renderer +
#          schema + answer-loop surface, not a guess at its shape. POSITIVE + monotone-safe (assert-present,
#          the #181 green-start archetype at the wave boundary): confirm the wave 1-4 renderer + block
#          catalog + annotation/answer-loop + SDK surface is present AND the wave 1-4 test suite is green
#          BEFORE this wave's DAG spends a turn. --filter-scoped to the materialized wave 1-4 categories, so
#          it NEVER runs the about-to-be-authored wave-5 red tests (ArtifactExporter / HandoffMarkdown) -
#          those do not exist on the branch at wave-5 entry.
$mustExist = @(
    @{ Path = 'src/Charter.Core/CharterRenderer.cs'; Marker = 'WriteQuestion';                    What = 'the wave-4 :::question form renderer the handoff path must convert to plain prose' },
    @{ Path = 'src/Charter.Core/QuestionSpec.cs';     Marker = 'public\s+static\s+QuestionSpec\s+Parse'; What = 'the wave-4 question schema the handoff path parses to resolve/flag answers' },
    @{ Path = 'src/Charter.Core/MermaidResource.cs';  Marker = 'internal\s+static\s+class\s+MermaidResource'; What = 'the wave-4 vendored Mermaid runtime the export path re-inlines unchanged' },
    @{ Path = 'src/Charter.Server/AnswerStore.cs';    Marker = 'public\s+sealed\s+class\s+AnswerStore'; What = 'the wave-4 answer store proving the question/answer loop landed' },
    @{ Path = 'sdk/charter-annotate.js';              Marker = 'data-question-id';                What = 'the wave-4 SDK question-submit extension' }
)
foreach ($m in $mustExist) {
    if (-not (Test-Path $m.Path)) {
        Write-Output "wave-5 entry gate: $($m.Path) is absent - wave 4 has not materialized on the branch ($($m.What))."
        exit 1
    }
    if ((Get-Content -Raw $m.Path) -notmatch $m.Marker) {
        Write-Output "wave-5 entry gate: $($m.Path) does not carry /$($m.Marker)/ - $($m.What) is missing."
        exit 1
    }
}
$test = dotnet test Charter.sln -c Debug --filter "Category=CoreRenderer|Category=DiagramBlock|Category=ComparisonBlock|Category=DiffBlock|Category=QuestionSchema|Category=QuestionForm|Category=AnnotationApi|Category=AnnotationStore|Category=AnswerApi|Category=ReviewServer" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output "----- FAILURE DETAIL -----"
    ($test -split "`n" | Select-String -Pattern "Failed|error|Exception|Assert" -SimpleMatch:$false) | ForEach-Object { Write-Output $_.Line }
    Write-Output "wave-5 entry gate: the wave 1-4 test suite is not green on the starting HEAD - fix the pre-existing breakage before wave 5 builds on the renderer + rich-block + answer loop."
    exit 1
}
exit 0
