# BASELINE (#181), plan-level, AREA-SCOPED: is Charter.Browser.Tests already green before this plan touches it?
# catches: a pre-existing red that the terminal <plan>/guardrails/02-all-tests-pass.ps1 would otherwise
#          blame on this plan - AFTER every task has burned its spend. This area is modified by tasks 07/08 and 09/10.
# ONE PREFLIGHT PER TOUCHED AREA, deliberately (#181c/#181e). The first version of this plan ran the
#          WHOLE SOLUTION here, which measured IDENTICAL in scope to the terminal gate - so it was not
#          "strictly narrower" in any sense, and it spent 3m38s on browser tests no early task can
#          affect before the DAG had even started.
# The exclusion filter is a NO-OP today - none of its selectors exists until this plan's author-tests
#          tasks create them - and is written out anyway so the check keeps its meaning if it is ever
#          re-run against a partially-executed tree. It is NOT the filter for any task-level guardrail
#          (#455); each pair names its own class.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$filter = 'Feature!=ReviewLogUnknownPanel&Feature!=ReviewLogNotLoaded'

# NO -v q on a test command: it suppresses the Error Message/Expected/Actual/Stack Trace block (#179).
$out = dotnet test tests/Charter.Browser.Tests/Charter.Browser.Tests.csproj -c Release --nologo --filter $filter 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

if ($testExit -ne 0) {
    # THREE-WAY, not two. Found by the second review pass: `dotnet test` BUILDS before it runs, so a
    # non-zero exit here is often NOT a test failure at all - and the behavioural message below is then a
    # confident wrong diagnosis aimed at the one file the retry agent is allowed to edit. Measured: the
    # plan-level preflight announced "the suite is ALREADY RED" over a run in which every project passed,
    # because a locked DLL failed the build. Separate the three cases before saying anything.
    $csErrors = @($out | Where-Object { $_ -match ': error CS' })
    $lockHits = @($out | Where-Object { $_ -match 'MSB302[67]' })

    if ($csErrors.Count -eq 0 -and $lockHits.Count -gt 0) {
        Write-Output ""
        Write-Output "ENVIRONMENT, NOT YOUR CODE: the run failed with ZERO compile errors and $($lockHits.Count) MSB3026/MSB3027 file-lock warning(s) - another process is holding the output DLLs (a lingering test host, or a `charter review` server for this plan). No test ran and no edit can fix it. Escalate with needsHuman (kind: blocked-work) and stop."
        exit 1
    }

    if ($csErrors.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Compile errors - this is NOT a test failure, no test ran ==="
        $csErrors | Select-Object -First 20 | ForEach-Object { Write-Output $_ }
        Write-Output ""
        Write-Output "The project did not COMPILE. Read the FILE PATHS above and compare each against your writeScope: this command compiles the whole project, including files this task may NOT write. If any failing file lies outside your scope, you cannot fix it - escalate with needsHuman (kind: blocked-work) naming the file and the missing symbol. Fix only what is inside your scope."
        exit 1
    }

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
    Write-Output "Charter.Browser.Tests is ALREADY RED before this plan has changed anything. Fix that first: every red the terminal gate reports at the end of this run would otherwise be attributed to the plan."
    exit 1
}

$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed in Charter.Browser.Tests - the test host did not run, so no baseline was established. This is NOT a green baseline."
    exit 1
}

Write-Output "Baseline established: $ran tests executed and Charter.Browser.Tests is green before the plan runs."
exit 0
