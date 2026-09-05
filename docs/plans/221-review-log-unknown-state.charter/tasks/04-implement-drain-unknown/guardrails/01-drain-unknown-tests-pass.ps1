# catches: a drain that reports a confident exit 2 - 'a queue was found and it was EMPTY' - when the review log could not be read, telling an agent the reviewer said nothing. The --filter names this pair's OWN selector, never a plan-wide
#          one - a broad filter asserts the state of every test in the plan, so the task cannot go green
#          until a task that DEPENDS on it has run (a deadlock validate and graph --check cannot see, #455).
#          Re-emits the failure BLOCK at the END so it reaches the retry-feedback tail (#179).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# SAME filter string as this pair's inverse half - copied verbatim so the two cannot drift apart.
$filter = 'Category=ReviewLogDrainUnknown&FullyQualifiedName~ReviewLogDrainUnknownTests'

# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block, leaving
# only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
# -c Release matches the build gates, so the configuration that is compile-checked is
# the one the tests actually run in - and each task pays ONE build, not two (#8).
$out = dotnet test -c Release tests/Charter.Cli.Tests/Charter.Cli.Tests.csproj --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary, so
# checking the exit code first reports its real error instead of blaming the filter.
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
    Write-Output "The drain does not translate Unknown into exit 4, or it broke exit 2 for the genuinely-empty case it was always right about (see failure details above)."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter matching nothing, or a
# malformed one, also exits 0. Key on the EXECUTED count (Passed+Failed); "Total:" would also count
# [Skip]ped tests.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
 if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter matched no tests or is malformed. Check it against the class ReviewLogDrainUnknownTests."
    exit 1
}

exit 0
