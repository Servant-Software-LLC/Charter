# catches: a test file that does not COMPILE. Without this, the red check below cannot tell a genuinely
#          failing test from a syntax error - both exit non-zero - so garbage would certify as TDD red,
#          and the implementation task (whose writeScope EXCLUDES this file) could never fix it.
# Measured baseline (#478): the project builds clean on the starting tree, so this exits 0 both before and
#          after the task. It is a compile gate, not an adequacy floor - the red proof is guardrail 02.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# -v q is CORRECT on a build (it strips restore/banner chatter and leaves the compiler errors). It is NOT
# carried onto any dotnet test command in this plan, where it would delete the failure detail (#179).
$out = dotnet build tests/Charter.Browser.Tests/Charter.Browser.Tests.csproj -c Release --nologo -v q 2>&1
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
    Write-Output "tests/Charter.Browser.Tests/Charter.Browser.Tests.csproj does not compile - ReviewLogUnknownPanelTests.cs has a syntax or reference error ($($csErrors.Count) CS error(s)). The tests must COMPILE and FAIL; not compiling is a mistake to fix, not a TDD red."
    exit 1
}

exit 0
