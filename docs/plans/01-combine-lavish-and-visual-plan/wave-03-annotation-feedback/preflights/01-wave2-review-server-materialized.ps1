# catches: wave 3 starting before wave 2's review server materialized on the branch - the annotation
#          feedback loop extends Charter.Server.ReviewServer + SdkInjector. POSITIVE + monotone-safe
#          (assert-present): confirm the wave-2 server API surface is present AND wave-2's ReviewServer
#          tests are green (#181 green-start at the wave boundary) before this wave's DAG spends a turn.
#          --filter-scoped to Category=ReviewServer, so it never runs the about-to-be-authored wave-3
#          Annotation* red tests (they do not exist on the branch at wave-3 entry).
$server = "src/Charter.Server/ReviewServer.cs"
if (-not (Test-Path $server)) {
    Write-Output "wave-3 entry gate: $server is absent - wave 2 (review server) has not materialized on the branch."
    exit 1
}
if ((Get-Content -Raw $server) -notmatch 'public\s+static\s+ReviewServer\s+Start\s*\(') {
    Write-Output "wave-3 entry gate: ReviewServer.Start(...) is not present - the wave-2 server API the annotation loop extends is missing."
    exit 1
}
$injector = "src/Charter.Server/SdkInjector.cs"
if (-not (Test-Path $injector)) {
    Write-Output "wave-3 entry gate: $injector is absent - the serve-time SDK injection point the annotation SDK replaces is missing."
    exit 1
}
if ((Get-Content -Raw $injector) -notmatch 'public\s+static\s+string\s+Inject\s*\(') {
    Write-Output "wave-3 entry gate: SdkInjector.Inject(...) is not present - the wave-2 injection mechanism is missing."
    exit 1
}
$test = dotnet test tests/Charter.Server.Tests/Charter.Server.Tests.csproj -c Debug --filter "Category=ReviewServer" --nologo 2>&1 | Out-String
Write-Output $test
if ($LASTEXITCODE -ne 0) {
    Write-Output "----- FAILURE DETAIL -----"
    ($test -split "`n" | Select-String -Pattern "Failed|error|Exception|Assert" -SimpleMatch:$false) | ForEach-Object { Write-Output $_.Line }
    Write-Output "wave-3 entry gate: wave-2 ReviewServer tests are not green on the starting HEAD - fix the pre-existing breakage before wave 3 builds on the review server."
    exit 1
}
exit 0
