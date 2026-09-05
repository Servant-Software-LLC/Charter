# catches: a project that builds alone but breaks the solution after every plan task has merged - a
#          cross-project compilation error the per-task single-project builds cannot see. This plan
#          spreads across Charter.Server, Charter.Cli and the embedded SDK, and the signature that
#          task 02 changes on ReviewLogRead is consumed from all three.
# LOCAL (no scope key, #165): a whole-solution build is a TERMINAL POSTCONDITION, not a union-safe
#          invariant. Marked scope:"integration" it would re-run at every intermediate union, where a
#          merged tree can legitimately hold test files referencing a not-yet-implemented member - and
#          the harness would roll back a correct wave. This runs ONCE, on the merged plan-branch HEAD.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# -v q is correct on a build: it strips restore/banner chatter and leaves the compiler errors.
$out = dotnet build Charter.sln -c Release --nologo -v q 2>&1
$buildExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

if ($buildExit -ne 0) {
    # MEASURED on this machine during review: a lingering test host or review server holding the output
    # DLLs fails this build with ZERO compile errors and 44 MSB3026 warnings. The old message named the
    # TEST FILE as the cause - and the retry agent's writeScope IS that file, so every attempt would
    # rewrite correct code while the lock persisted, dead-ending at needs-human with a wrong diagnosis.
    # Separate the two before saying anything.
    $csErrors = @($out | Where-Object { $_ -match ': error CS' })
    $lockHits = @($out | Where-Object { $_ -match 'MSB302[67]' })
    Write-Output ""
    if ($csErrors.Count -eq 0 -and $lockHits.Count -gt 0) {
        Write-Output "ENVIRONMENT, NOT YOUR CODE: the build failed with ZERO compile errors and $($lockHits.Count) MSB3026/MSB3027 file-lock warning(s) - another process is holding the output DLLs. Do NOT edit any source file; nothing here is your fault and no edit can fix it. Escalate with needsHuman (kind: blocked-work) saying the build is blocked by a locked output DLL, and stop."
        exit 1
    }
    Write-Output "Charter.sln does not build on the merged plan-branch HEAD - a cross-project compilation error that no single-project build caught."
    exit 1
}

exit 0
