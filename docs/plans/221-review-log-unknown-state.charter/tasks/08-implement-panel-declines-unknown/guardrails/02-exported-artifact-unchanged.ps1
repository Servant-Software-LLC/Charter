# catches: an SDK change that leaks out of the serve-time layer into the portable artifact. The annotation SDK is injected at SERVE time only (invariant 1) and the exported artifact must stay byte-identical; DiagramPanZoomArtifactTests enforces that boundary and covers this change unchanged. The --filter names this pair's OWN selector, never a plan-wide
#          one - a broad filter asserts the state of every test in the plan, so the task cannot go green
#          until a task that DEPENDS on it has run (a deadlock validate and graph --check cannot see, #455).
#          Re-emits the failure BLOCK at the END so it reaches the retry-feedback tail (#179).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# SAME filter string as this pair's inverse half - copied verbatim so the two cannot drift apart.
$filter = 'FullyQualifiedName~DiagramPanZoomArtifactTests'

# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block, leaving
# only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Charter.Core.Tests/Charter.Core.Tests.csproj --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary, so
# checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    # BLOCK capture, never a line allowlist (#608). An allowlist re-emits only the lines it enumerates, so
    # it DROPS a DoesNotContain failure's String:/Found: payload and every stack frame - measured. Take the
    # CONTIGUOUS block from the first failure marker onward, bounded to fit the ~60-line feedback tail.
    $lines = ($out | Out-String) -split '\r?\n'
    # Anchor on the first DETAIL line, NOT the first [FAIL]. MEASURED during review: xUnit prints its
    # whole [FAIL] NAME list before any detail block, so a [FAIL]-anchored window fills with names - a
    # 60-failure regression gave 56 name lines and ZERO Error Message: blocks, which is precisely the
    # #179 starvation the re-emit exists to prevent. Fall back to [FAIL] only when there is no detail
    # block at all (a crashed host prints names and nothing else).
    $first = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match 'Error Message:') { $first = $i; break }
    }
    if ($first -lt 0) {
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '\[FAIL\]') { $first = $i; break }
        }
    }
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($first -ge 0) {
        $last = [Math]::Min($first + 55, $lines.Count - 1)
        $lines[$first..$last] | ForEach-Object { Write-Output $_ }
    }
    else { Write-Output "(no failure block found - inspect the full log above)" }
    Write-Output "The exported artifact CHANGED. This fix must live entirely in sdk/charter-annotate.js - revert any renderer, exporter or charter.css edit."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter matching nothing, or a
# malformed one, also exits 0. Key on the EXECUTED count (Passed+Failed); "Total:" would also count
# [Skip]ped tests.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
 if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter matched no tests or is malformed. Check that DiagramPanZoomArtifactTests still exists in tests/Charter.Core.Tests."
    exit 1
}

exit 0
