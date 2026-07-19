# catches: wave 4 starting before wave 3's annotation loop materialized on the branch - the rich/interactive
#          blocks EXTEND Charter.Core (CharterRenderer + BlockKind + SourceMap) and tie into the wave-3
#          annotation server + embedded SDK. POSITIVE + monotone-safe (assert-present, #181 green-start at the
#          wave boundary): confirm the wave 1-3 renderer + annotation-API + SDK surface is present AND the
#          wave 1-3 test suite is green BEFORE this wave's DAG spends a turn. --filter-scoped to the
#          materialized wave 1-3 categories, so it NEVER runs the about-to-be-authored wave-4 red tests
#          (DiagramBlock / ComparisonBlock / DiffBlock / QuestionSchema / QuestionForm / AnswerApi) - those do
#          not exist on the branch at wave-4 entry.
$mustExist = @(
    @{ Path = 'src/Charter.Core/BlockModel.cs';        Marker = 'internal\s+static\s+class\s+CharterMarkdown'; What = 'the wave-1 CharterMarkdown.Describe classifier the new block kinds extend' },
    @{ Path = 'src/Charter.Core/CharterRenderer.cs';   Marker = 'public\s+static\s+string\s+Render\s*\(';       What = 'the wave-1 renderer the new blocks emit through' },
    @{ Path = 'src/Charter.Core/SourceMap.cs';         Marker = 'public\s+static\s+SourceMap\s+Build\s*\(';     What = 'the wave-1 source-map the per-sub-element anchors extend' },
    @{ Path = 'src/Charter.Server/ReviewServer.cs';    Marker = 'prompts';                                      What = 'the wave-3 annotation server the answer-submission route extends' },
    @{ Path = 'src/Charter.Server/AnnotationApi.cs';   Marker = 'IsAllowedOrigin';                              What = 'the wave-3 CSRF/same-origin gate the answer route reuses' },
    @{ Path = 'sdk/charter-annotate.js';               Marker = 'CharterAnnotate';                              What = 'the wave-3 embedded SDK the question-form submit extends' }
)
foreach ($m in $mustExist) {
    if (-not (Test-Path $m.Path)) {
        Write-Output "wave-4 entry gate: $($m.Path) is absent - wave 3 has not materialized on the branch ($($m.What))."
        exit 1
    }
    if ((Get-Content -Raw $m.Path) -notmatch $m.Marker) {
        Write-Output "wave-4 entry gate: $($m.Path) does not carry /$($m.Marker)/ - $($m.What) is missing."
        exit 1
    }
}
$test = dotnet test Charter.sln -c Debug --filter "Category=CoreRenderer|Category=AnnotationApi|Category=AnnotationStore|Category=ReviewServer" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output "----- FAILURE DETAIL -----"
    ($test -split "`n" | Select-String -Pattern "Failed|error|Exception|Assert" -SimpleMatch:$false) | ForEach-Object { Write-Output $_.Line }
    Write-Output "wave-4 entry gate: the wave 1-3 test suite is not green on the starting HEAD - fix the pre-existing breakage before wave 4 builds on the renderer + annotation loop."
    exit 1
}
exit 0
