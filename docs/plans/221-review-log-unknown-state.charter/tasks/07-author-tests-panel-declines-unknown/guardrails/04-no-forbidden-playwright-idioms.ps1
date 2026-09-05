# catches: the two Playwright idioms this task's prompt forbids in prose and nothing else enforced
#          (#221). Both are real, measured traps in THIS project:
#          - WaitForFunctionAsync: the served page's CSP is script-src 'unsafe-inline' with NO
#            'unsafe-eval' (ReviewServer.ServedPageCsp), so its polling predicate THROWS the moment it
#            genuinely has to wait. It only appears to work when the condition is already true on the
#            first check. The perverse part, and why prose was not enough: a throwing wait makes the test
#            FAIL, which is exactly what task 07's red census wants - so the census passes for the WRONG
#            REASON (a CSP throw, not a vanished panel) and the defect surfaces at task 08 as an
#            unfixable green-check, one task too late.
#          - WaitForEventAsync: asks "has this EVER happened", so a second call in one test returns
#            instantly. WaitForEventCountAsync is the correct form and is deliberately NOT banned here.
# Both are structural facts about the test file with no runtime proxy - a test cannot assert which wait
#          helper another test used - so this is not a demotable source-shape check (#468).
# Measured baseline (#478): the file does not exist on the starting tree; the precondition fires.
# Probe C (#470): the task's action.prompt.md DOES name both banned tokens - in prose, to forbid them,
#          never as required vocabulary - and this scan runs over comment- and literal-stripped source,
#          so neither the prompt nor a comment in the test file can trip it.
# Two-sided sample pair committed beside this file (#302/#468):
#          ../samples/04-no-forbidden-playwright-idioms.valid.cs   -> exit 0  (uses WaitForEventCountAsync)
#          ../samples/04-no-forbidden-playwright-idioms.invalid.cs -> exit 1
$ErrorActionPreference = 'Continue'

# DUAL-MODE, and this is a CONTRACT, not a convenience. `guardrails samples verify` (which the plan
# preflight runs before scheduling any task) invokes this script with the sample file BOTH as the env var
# GR_SUBJECT and as $args[0], with cwd = the workspace. A guardrail honouring neither only ever scans its
# hardcoded target, so BOTH halves of its sample pair read the same file, both exit identically, and the
# pair proves nothing. GR_SUBJECT is checked FIRST because it is the canonical half (Guardrails #559);
# $args[0] is kept as the fallback so whichever one a future verifier drops, this still works.
$path = if (-not [string]::IsNullOrWhiteSpace($env:GR_SUBJECT)) {
    $env:GR_SUBJECT
} elseif ($args.Count -ge 1 -and -not [string]::IsNullOrWhiteSpace($args[0])) {
    $args[0]
} else {
    'tests/Charter.Browser.Tests/ReviewLogUnknownPanelTests.cs'
}

if (-not (Test-Path -LiteralPath $path)) {
    Write-Output "PRECONDITION: $path does not exist - this task's whole deliverable is missing. Every clause below would report a phantom gap, so nothing else is checked."
    exit 1
}

$raw = Get-Content -LiteralPath $path -Raw

# Strip comments AND string literals before scanning (#470/#97): a token inside a comment or quoted in a
# message is a MENTION, not a USE, and would satisfy - or falsely trip - a naive scan over raw text.
$code = [regex]::Replace($raw, '(?s)/\*.*?\*/', '')
$code = [regex]::Replace($code, '(?m)//.*$', '')
$code = [regex]::Replace($code, '"(?:[^"\\]|\\.)*"', '""')

$failures = @()

# Anchored on the DOTTED CALL, not the bare name (#76): a mention in a name, a comment or a string is
# not a use. `\.WaitForEventAsync\s*\(` cannot match `.WaitForEventCountAsync(` - the literal differs at
# the character after "WaitForEvent" - which is what keeps the correct helper legal.
if ($code -cmatch '\.WaitForFunctionAsync\s*\(') {
    $failures += "the test calls .WaitForFunctionAsync(...). The served page's CSP has no 'unsafe-eval', so its polling predicate throws the moment it genuinely has to wait - it only appears to work when the condition is already true on the first check. Use an event-count wait (WaitForEventCountAsync) or a Playwright locator assertion instead."
}

if ($code -cmatch '\.WaitForEventAsync\s*\(') {
    $failures += "the test calls .WaitForEventAsync(...), which asks 'has this EVER happened' - so a second call in the same test returns instantly and asserts nothing. Use WaitForEventCountAsync, which is NOT banned by this check."
}

if ($failures.Count -gt 0) {
    Write-Output "=== Forbidden Playwright idioms ($($failures.Count)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output "No forbidden Playwright wait idioms in $path."
exit 0
