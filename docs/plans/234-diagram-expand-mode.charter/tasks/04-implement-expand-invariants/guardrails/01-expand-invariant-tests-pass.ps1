# catches: an expand implementation that breaks something the reviewer already had - zoom, pan, the
#          pinned zoom bar, per-node annotation, the expanded view surviving a render(), or focus being
#          stolen by that render. The --filter names this pair's own per-pair Feature trait, never
#          Category=BrowserAcceptance alone (#455). Re-emits the assertion/exception lines at the END so
#          they reach the retry-feedback tail (#179).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# SAME filter string as this pair's inverse half (03-author-tests/02-tests-fail-on-current-code.ps1).
$filter = 'Category=BrowserAcceptance&Feature=DiagramExpandInvariants'

$out = dotnet test tests/Charter.Browser.Tests/Charter.Browser.Tests.csproj --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second: a test host that never ran exits NON-zero with no summary.
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
    Write-Output "An expand-mode invariant is broken - something the reviewer already had stopped working while a diagram is expanded (see failure details above)."
    exit 1
}

# ZERO-MATCH GUARD: key on the EXECUTED count (Passed+Failed). "Total:" would also count [Skip]ped
# tests, and these are SkippableFact tests that skip when no browser is installed - so a Total:-keyed
# guard would certify a fully-skipped run as green.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test was skipped because no Playwright browser is installed. Install the browser (pwsh tests/Charter.Browser.Tests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium) rather than weakening this check."
    exit 1
}

exit 0
