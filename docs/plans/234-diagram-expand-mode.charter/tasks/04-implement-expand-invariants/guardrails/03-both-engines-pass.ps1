# catches: an expand implementation that works on Chromium and breaks on WebKit. CI runs the browser
#          suite on BOTH engines, and WebKit is where this repo's focus-across-rebuild failures (#221)
#          actually surface - which is precisely the area this feature touches. The default leg above
#          is Chromium; this re-runs BOTH feature traits under WebKit, so a Chromium-only green cannot
#          stand in for "it works".
#          Placed on the LAST implementation task, filtered to both pairs, so one run covers the whole
#          feature rather than each pair paying for a second engine.
# PREREQUISITE: the WebKit browser must be installed for Playwright. It is NOT installed by this
#          guardrail - installing a browser is a side effect a verification check must not have, and a
#          silent auto-install would hide a misconfigured runner. The failure message says how.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:CHARTER_BROWSER = 'webkit'

# Both pairs in one run. Parenthesised alternation with a BARE '|' - never '\|', which VSTest reads as
# an escape and rejects with "Invalid Condition", running zero tests and exiting 0 (a silent green).
$filter = 'Category=BrowserAcceptance&(Feature=DiagramExpandAffordance|Feature=DiagramExpandInvariants)'

$out = dotnet test tests/Charter.Browser.Tests/Charter.Browser.Tests.csproj --filter $filter --nologo 2>&1
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
    Write-Output "Expand mode fails under WebKit. Chromium green is not the gate - CI runs both engines, and WebKit is where this repo's focus-across-rebuild failures surface."
    exit 1
}

# ZERO-MATCH GUARD - the likeliest real cause here is a MISSING WEBKIT BROWSER, which makes every
# SkippableFact skip cleanly and exit 0. Keying on Total: would count those skips and certify nothing.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed under WebKit - this guardrail certified nothing. Most likely the WebKit browser is not installed, so every test skipped. Install it and re-run:"
    Write-Output "    pwsh tests/Charter.Browser.Tests/bin/Release/net10.0/playwright.ps1 install --with-deps webkit"
    Write-Output "If WebKit IS installed, the --filter '$filter' matched nothing - check both Feature trait spellings."
    exit 1
}

exit 0
