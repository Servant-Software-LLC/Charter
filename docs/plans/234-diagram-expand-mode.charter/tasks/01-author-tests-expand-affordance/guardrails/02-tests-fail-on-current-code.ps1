# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), an assertion
#          over a value the test itself constructed, anything that never drives the real control). It
#          PASSES against the current SDK and hides behind its genuinely-failing siblings, so a
#          suite-level non-zero exit would certify the whole file honest and the covers-* token floor
#          would certify it covered (#375). One entry per enumerated behaviour, each observed Failed in
#          the runner's OWN TRX - never merely discovered by name, which a hollow body satisfies.
# DECLARED EXEMPTIONS: none. Every behaviour below drives an expand control that does not exist yet, so
#          a CORRECT test for each one is red on the current tree. If a row here ever becomes green
#          before the feature lands, that row is asserting something already true - fix the test, do not
#          add an exemption.
# NOTE for this repo: these are Playwright SkippableFact tests. When no browser is installed they record
#          outcome 'NotExecuted', which this census reports as unbound - correct, because a skipped run
#          proves nothing. The remedy is to install the browser, never to weaken a test.
$ErrorActionPreference = 'Continue'
# The census reads the TRX (schema tokens, NOT localized) so the guard does not depend on this - kept so
# the logged summary is readable and the pair stays copy-pasteable with its forward half.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# The class term cannot discriminate in this project: every file in tests/Charter.Browser.Tests is a
# partial of ONE class (ReviewLoopBrowserTests) carrying Category=BrowserAcceptance, so
# FullyQualifiedName~<anything-else> matches ZERO and Category alone selects all 86 browser tests (the
# #455 plan-wide-trait defect). The per-pair Feature trait is this pair's discriminating selector.
$filter = 'Category=BrowserAcceptance&Feature=DiagramExpandAffordance'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
$manifest = [ordered]@{
    'the zoom bar carries an expand control with an accessible name' = 'An_oversized_diagram_offers_an_expand_control'
    'activating it expands that diagram to fill the viewport'        = 'Expanding_a_diagram_fills_the_viewport'
    'activating it again restores the original box'                  = 'Expanding_then_collapsing_restores_the_original_box'
    'Escape collapses the expanded view'                             = 'Escape_collapses_the_expanded_diagram'
    'the expand control is operable from the keyboard'               = 'The_expand_control_is_reachable_by_keyboard'
    'the zoom hint names expand while the diagram is too wide'       = 'The_zoom_hint_names_expand_when_the_diagram_is_too_wide'
    'expanding hides no existing chrome (no display:none)'           = 'Expanding_hides_no_existing_chrome'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX

$out = dotnet test tests/Charter.Browser.Tests/Charter.Browser.Tests.csproj --filter $filter --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (test host failed to
# start, wrong project path, or a malformed --filter, which exits 0 SILENTLY). Diagnose THAT. Falling
# through would print "every behaviour unbound" - a confident wrong message aimed at the one artifact a
# retry agent is allowed to edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
# The Where-Object is NOT decoration: with zero tests executed the TRX has NO <Results> element, the
# navigation yields $null, and @($null).Count is 1 - so the bare @(...) form would make the guard below
# evaluate 1 -lt 1 and NEVER FIRE.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing (is the Feature trait, value DiagramExpandAffordance, on every test method?), the filter is malformed, or every match is [Skip]ped out of execution. This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# ACCUMULATE: one distinguishable message per unbound behaviour, so ONE attempt learns every gap.
$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $name = $manifest[$behaviour]
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, missing the Feature trait, or not selected by the filter)"
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on the CURRENT tree, not Failed. A test that does not fail while the expanded view cannot be entered never drives the real control, so it asserts a tautology and certifies nothing. ('NotExecuted' means it was skipped - install the browser rather than weakening the test.)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== Behaviours not bound to a genuinely-failing test ($($failures.Count)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output "All $($manifest.Count) enumerated behaviours are bound to a test observed Failed on the current tree."
exit 0
