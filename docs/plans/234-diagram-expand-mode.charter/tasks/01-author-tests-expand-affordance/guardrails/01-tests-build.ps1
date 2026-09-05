# catches: a test file that does not COMPILE. Without this, the inverse red check below cannot tell a
#          genuinely-failing test from a syntax error - both exit non-zero - so garbage would certify as
#          TDD red, and the implementation task (whose writeScope EXCLUDES this file) could never fix it.
#          Cheapest-first: this runs before anything that starts a browser.
# Measured baseline (#478): the project builds clean on the starting tree, so this exits 0 BEFORE the
#          task and 0 after. It is a compile gate, not an adequacy floor - the red proof is guardrail 02.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# -v q is CORRECT on a build (it strips restore/banner chatter and leaves the compiler errors).
# It is NOT carried onto any dotnet test command in this plan - there it would delete the failure detail.
$out = dotnet build tests/Charter.Browser.Tests/Charter.Browser.Tests.csproj -c Release --nologo -v q 2>&1
$buildExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

if ($buildExit -ne 0) {
    Write-Output ""
    Write-Output "Charter.Browser.Tests does not compile - DiagramExpandAffordanceTests.cs has a syntax or reference error. The tests must COMPILE and FAIL; not compiling is a mistake to fix, not a TDD red."
    exit 1
}

exit 0
