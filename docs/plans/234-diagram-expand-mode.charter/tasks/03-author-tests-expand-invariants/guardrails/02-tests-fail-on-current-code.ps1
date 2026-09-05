# catches: a HOLLOW invariant test - named for the behaviour, body a tautology that never opens the
#          expanded view. It PASSES on the current tree and hides behind its genuinely-failing siblings,
#          so a suite-level non-zero exit would certify the whole file honest (#375). One entry per
#          enumerated behaviour, each observed Failed in the runner's OWN TRX.
# WHY EVERY ROW IS EXPECTED RED HERE, with no declared exemption: this task's tests run against a tree
#          where no expand control exists (the sibling implementation task has not merged), so every
#          test fails at the point it tries to open the expanded view. That is the right red - none of
#          these invariants can hold for a state the page cannot enter. If a row here goes green before
#          the feature lands, that test is not driving the expanded view: fix the test, do not add an
#          exemption. A file full of exemptions would be a forward census wearing the red one's name.
# NOTE for this repo: these are Playwright SkippableFact tests. With no browser installed they record
#          'NotExecuted', reported below as unbound - correct, because a skipped run proves nothing.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Per-pair Feature trait - the only discriminating selector in this project (every browser test file is
# a partial of ONE class carrying Category=BrowserAcceptance). Note the value differs from the sibling
# pair's DiagramExpandAffordance.
$filter = 'Category=BrowserAcceptance&Feature=DiagramExpandInvariants'

$manifest = [ordered]@{
    'zoom still works while expanded'                             = 'Zoom_still_works_while_the_diagram_is_expanded'
    'pan still works while expanded'                              = 'Pan_still_works_while_the_diagram_is_expanded'
    'the zoom bar stays pinned while panning expanded'            = 'The_zoom_bar_stays_pinned_while_panning_expanded'
    'annotating a node while expanded posts THAT node''s anchor'  = 'Annotating_a_node_while_expanded_posts_that_nodes_anchor'
    'the expanded view survives a teammate-record render()'       = 'The_expanded_view_survives_a_render_from_a_teammate_record'
    'that render does not steal focus (anti-steal control)'       = 'A_render_while_expanded_does_not_steal_focus'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

$out = dotnet test tests/Charter.Browser.Tests/Charter.Browser.Tests.csproj --filter $filter --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# The Where-Object is load-bearing: with zero tests executed the TRX has NO <Results> element, the
# dotted navigation yields $null, and @($null).Count is 1 - so the bare @(...) form would make this
# guard evaluate 1 -lt 1 and never fire.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing (is the Feature trait, value DiagramExpandInvariants, on every test method, spelled exactly?), the filter is malformed, or every match is skipped. This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $name    = $manifest[$behaviour]
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, missing or misspelled Feature trait, or not selected by the filter)"
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on the CURRENT tree, not Failed. A test that genuinely drives the expanded view cannot pass until the invariants are implemented - this one is not driving it. ('NotExecuted' means it was skipped: install the browser rather than weakening the test.)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== Behaviours not bound to a genuinely-failing test ($($failures.Count)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output "All $($manifest.Count) enumerated invariants are bound to a test observed Failed on the current tree."
exit 0
