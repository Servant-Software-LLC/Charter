# BASELINE (#181), plan-level: is the suite ALREADY green before this plan touches anything?
# catches: a pre-existing red that the terminal <plan>/guardrails/02-all-tests-pass.ps1 would otherwise
#          blame on this plan - AFTER all eight tasks have burned their spend. That misattribution is
#          expensive and late, which is exactly the case the worth-it gate says to pay one suite run for.
#          It also protects the two brownfield projects this plan modifies (Charter.Server.Tests, twice:
#          tasks 01/02 and 05/06) whose per-task guardrails are all filtered to their own pair by
#          design and so can see nothing outside it.
# The exclusion filter is a NO-OP today - none of the four selectors below exists until this plan's
#          author-tests tasks create them - and it is written out anyway so this check keeps its meaning
#          if it is ever re-run against a partially-executed tree. It is NOT the filter for any
#          task-level guardrail (#455); each pair names its own class.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$filter = 'Category!=ReviewLogUnknownState&Category!=ReviewLogDrainUnknown&Category!=BridgeUnknown&Feature!=ReviewLogUnknownPanel'

# NO -v q on a test command: it suppresses the Error Message/Expected/Actual/Stack Trace block (#179).
$out = dotnet test Charter.sln -c Release --nologo --filter $filter 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

if ($testExit -ne 0) {
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
    Write-Output "The suite is ALREADY RED before this plan has changed anything. Fix that first: every red the terminal gate reports at the end of this run would otherwise be attributed to the plan."
    exit 1
}

$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed across the solution - the test host did not run, so no baseline was established. This is NOT a green baseline."
    exit 1
}

Write-Output "Baseline established: $ran tests executed and the suite is green before the plan runs."
exit 0
