# catches: an expand implementation whose behaviour deviates from the tests THIS task pair owns. The
#          --filter names this pair's own per-pair Feature trait, never Category=BrowserAcceptance alone
#          - a trait-only filter would assert the state of all 86 browser tests, so this task could not
#          go green until tasks that depend on it had run (a deadlock validate/graph --check cannot see,
#          #455). Re-emits the assertion/exception lines at the END so they reach the retry tail (#179).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the zero-match guard reads is LOCALIZED

# SAME filter string as this pair's inverse half (01-author-tests/02-tests-fail-on-current-code.ps1) -
# copied verbatim so the two halves can never drift apart. The class term is unusable here: every file
# in tests/Charter.Browser.Tests is a partial of ONE class (ReviewLoopBrowserTests), so
# FullyQualifiedName~<anything-else> matches ZERO.
$filter = 'Category=BrowserAcceptance&Feature=DiagramExpandAffordance'

# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Charter.Browser.Tests/Charter.Browser.Tests.csproj --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary, so
# checking the exit code first reports its real error instead of blaming the filter.
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
    Write-Output "The DiagramExpandAffordance tests are failing - the expand affordance is not implemented to spec (see failure details above)."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter matching nothing, or a
# malformed one, also exits 0. Key on the EXECUTED count (Passed+Failed); "Total:" would also count
# [Skip]ped tests, and these are SkippableFact tests that skip when no browser is installed - so a
# Total:-keyed guard would certify a fully-skipped run as green.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test was skipped because no Playwright browser is installed. Install the browser (pwsh tests/Charter.Browser.Tests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium) rather than weakening this check."
    exit 1
}

exit 0
