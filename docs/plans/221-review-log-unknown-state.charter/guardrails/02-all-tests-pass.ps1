# catches: a regression anywhere in the solution caused by this plan - the whole suite on the merged
#          plan-branch HEAD. The per-task checks are all FILTERED to their own pair, deliberately, so
#          this is the only place a break in an unrelated area is caught. That matters more than usual
#          here: ReviewLogStore.Read is read by the drain, the bridge, the served view and the CLI, so
#          adding an outcome to it can reach consumers no per-task filter selects.
# LOCAL (no scope key, #165): a whole-suite run is a TERMINAL POSTCONDITION. At an intermediate union a
#          merged tree legitimately holds intentionally-red TDD tests, so an integration-scoped whole
#          suite would red-halt a correct run.
# Read the baseline first: <plan>/preflights/01-baseline-suite-green.ps1 proved the suite was green
#          BEFORE the plan ran, so a red here is attributable to the plan rather than inherited.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# NO -v q on the test command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below - which would defeat #179 by the flag alone.
$out = dotnet test Charter.sln -c Release --nologo 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

if ($testExit -ne 0) {
    # BLOCK capture, never a line allowlist (#608) - an allowlist drops the String:/Found: payload and
    # every stack frame. Contiguous block from the first failure marker, bounded to the feedback tail.
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
    Write-Output "The full suite is failing on the merged plan-branch HEAD - see the failure details above."
    exit 1
}

# Unfiltered, so a zero-executed run means the test host never started rather than a bad filter.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed across the whole solution - the test host did not run. This guardrail certified nothing."
    exit 1
}

exit 0
