# catches: a HOLLOW test - named for the behaviour, body a tautology that never drives the subject. It
#          PASSES on the current tree and hides behind its genuinely-failing siblings, so a suite-level
#          non-zero exit would certify the whole file honest (#375). One entry per enumerated behaviour,
#          each observed Failed in the runner's OWN TRX - never merely discovered by name.
# DECLARED EXEMPTIONS: none. The drain does not translate Unknown yet, so every row is red on the current tree. If a row here goes green before the
#          implementation lands, that test is not driving the subject: fix the test, never add an exemption.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$filter = 'Category=ReviewLogDrainUnknown&FullyQualifiedName~ReviewLogDrainUnknownTests'

$manifest = [ordered]@{
    'an unknown review log exits 4, not 2'                           = 'An_unknown_review_log_exits_four_not_two'
    'a genuinely empty review log still exits 2'                     = 'A_genuinely_empty_review_log_still_exits_two'
    'the unknown exit says unknown, not nothing-queued'              = 'The_unknown_exit_says_unknown_not_nothing_queued'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX

$out = dotnet test tests/Charter.Cli.Tests/Charter.Cli.Tests.csproj --filter $filter --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (host failed to start,
# wrong project path, or a malformed --filter, which exits 0 SILENTLY). Diagnose THAT: falling through
# would print "every behaviour unbound", a confident wrong message aimed at the one artifact a retry agent
# is allowed to edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing. The
# Where-Object is load-bearing: with zero tests executed the TRX has NO <Results> element, the navigation
# yields $null, and @($null).Count is 1 - so the bare @(...) form makes this guard evaluate 1 -lt 1 and
# NEVER FIRE.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing (is the Category trait ReviewLogDrainUnknown on the class?), the filter is malformed, or every match is skipped. This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# ACCUMULATE: one distinguishable message per unbound behaviour, so ONE attempt learns every gap.
$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $name = $manifest[$behaviour]
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not. The (\(|$) tail admits a
    # [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, missing or misspelled trait, or not selected by the filter)"
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on the CURRENT tree, not Failed. A test that does not fail before the implementation lands never drives the subject, so it asserts a tautology and certifies nothing. ('NotExecuted' means it was skipped.)"
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
