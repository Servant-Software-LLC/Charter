# catches: wave 2 starting before wave 1's renderer materialized on the branch - the review server
#          builds on Charter.Core.CharterRenderer. POSITIVE + monotone-safe (assert-present): confirm the
#          upstream API is present AND wave-1's existing tests are green (#181 green-start at the wave
#          boundary) before this wave's DAG spends a turn. --filter-scoped to the CoreRenderer tests, so
#          it never runs the about-to-be-authored ReviewServer red tests.
$renderer = "src/Charter.Core/CharterRenderer.cs"
if (-not (Test-Path $renderer)) {
    Write-Output "wave-2 entry gate: $renderer is absent - wave 1 (renderer core) has not materialized on the branch."
    exit 1
}
$src = Get-Content -Raw $renderer
if ($src -notmatch 'public\s+static\s+string\s+Render\s*\(') {
    Write-Output "wave-2 entry gate: CharterRenderer.Render(string) is not present - the wave-1 renderer API the review server depends on is missing."
    exit 1
}
$test = dotnet test tests/Charter.Core.Tests/Charter.Core.Tests.csproj -c Debug --filter "Category=CoreRenderer" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output "----- FAILURE DETAIL -----"
    ($test -split "`n" | Select-String -Pattern "Failed|error|Exception|Assert" -SimpleMatch:$false) | ForEach-Object { Write-Output $_.Line }
    Write-Output "wave-2 entry gate: wave-1 CoreRenderer tests are not green on the starting HEAD - fix the pre-existing breakage before wave 2 builds on the renderer."
    exit 1
}
exit 0
