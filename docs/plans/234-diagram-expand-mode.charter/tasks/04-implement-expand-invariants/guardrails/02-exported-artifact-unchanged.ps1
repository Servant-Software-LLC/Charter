# catches: expand mode leaking OUT of the serve-time SDK and into the portable artifact - a
#          .charter-expand* rule added to assets/charter.css, a change to CharterRenderer, or anything
#          that makes the exported HTML differ. Repeated from the sibling implementation task because
#          THIS task edits the same file again: the boundary can be broken by either edit, and a check
#          that only ran on the earlier one would leave the later edit unguarded.
#          Behavioural (the artifact's bytes), carried by a test rather than a source regex over
#          charter.css - a regex would only prove a token is absent, not that the output is unchanged.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$filter = 'FullyQualifiedName~DiagramPanZoomArtifactTests'

$out = dotnet test tests/Charter.Core.Tests/Charter.Core.Tests.csproj --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

if ($testExit -ne 0) {
    # BLOCK capture, never a line allowlist (#608). An allowlist of patterns re-emits only the lines it
    # enumerates, so it DROPS a DoesNotContain failure's String:/Found: payload and every stack frame -
    # measured. Take the CONTIGUOUS block from the first failure marker onward instead, bounded so it
    # still fits the harness's ~60-line feedback tail.
    $lines = ($out | Out-String) -split '\r?\n'
    $first = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '\[FAIL\]|Error Message:') { $first = $i; break }
    }
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($first -ge 0) {
        $last = [Math]::Min($first + 55, $lines.Count - 1)
        $lines[$first..$last] | ForEach-Object { Write-Output $_ }
    }
    else { Write-Output "(no failure block found - inspect the full log above)" }
    Write-Output "The exported artifact CHANGED. Expand mode must live entirely in sdk/charter-annotate.js: move any style out of assets/charter.css and revert any renderer/exporter edit."
    exit 1
}

$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests or is malformed. Check that DiagramPanZoomArtifactTests still exists in tests/Charter.Core.Tests."
    exit 1
}

exit 0
