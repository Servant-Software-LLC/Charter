# catches: a project that builds alone but breaks the solution after every plan task has merged - a
#          cross-project compilation error the per-task single-project builds cannot see.
# LOCAL (no scope key, #165): a whole-solution build is a TERMINAL POSTCONDITION, not a union-safe
#          invariant. Marked scope:"integration" it would re-run at every intermediate union, where a
#          merged tree can legitimately hold test files referencing a not-yet-implemented type - and
#          the harness would roll back a correct wave. This runs ONCE, on the merged plan-branch HEAD.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# -v q is correct on a build: it strips restore/banner chatter and leaves the compiler errors.
$out = dotnet build Charter.sln -c Release --nologo -v q 2>&1
$buildExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

if ($buildExit -ne 0) {
    Write-Output ""
    Write-Output "Charter.sln does not build on the merged plan-branch HEAD - a cross-project compilation error that no single-project build caught."
    exit 1
}

exit 0
