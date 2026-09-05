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
    'tests/Charter.Browser.Tests/ReviewLogNotLoadedTests.cs'
}

if (-not (Test-Path -LiteralPath $path)) {
    Write-Output "PRECONDITION: $path does not exist - this task's whole deliverable is missing. Every clause below would report a phantom gap, so nothing else is checked."
    exit 1
}

$raw = Get-Content -LiteralPath $path -Raw

# Strip comments AND string literals before scanning (#470/#97): a token inside a comment or quoted in a
# message is a MENTION, not a USE, and would satisfy - or falsely trip - a naive scan over raw text.
# ORDER IS LOAD-BEARING, and the obvious order is wrong. Stripping `//` FIRST truncates any string
# literal containing one - `"http://example"` - which orphans its opening quote; the literal regex then
# runs on to the NEXT quote in the file and swallows whatever lies between, [SkippableFact] attributes
# included. MEASURED during review: a file with 4 test methods counted as 3, and with one more swallowed
# attribute it counts as 2 and PASSES a file that is genuinely missing traits. Literals first.
$code = [regex]::Replace($raw, '(?s)/\*.*?\*/', '')
$code = [regex]::Replace($code, '"(?:[^"\\]|\\.)*"', '""')
$code = [regex]::Replace($code, '(?m)//.*$', '')

$failures = @()

# ANCHORED ON THE CALL, NOT ON A DOT - and this is the correction that makes the check real. The first
# version banned `\.WaitForEventAsync\s*\(`, a DOTTED call. MEASURED: that shape occurs ZERO times in
# this repository, while the idiom that carries the trap occurs 146 times UNDOTTED - because
# WaitForEventAsync is not the Playwright API here, it is a private static helper on this very partial
# class (ReviewLoopBrowserTests.cs:4694), called bare. The ban could not fire, and its own sample pair
# certified it because the invalid sample was written to match the regex instead of to match how this
# codebase writes the call. The lookbehind admits neither `WaitForEventCountAsync(` (the correct helper,
# whose literal differs) nor a longer identifier ending in the banned name.
if ($code -cmatch '(?<![A-Za-z])WaitForFunctionAsync\s*\(') {
    $failures += "the test calls WaitForFunctionAsync(...). The served page's CSP has no 'unsafe-eval', so its polling predicate throws the moment it genuinely has to wait - it only appears to work when the condition is already true on the first check. Use an event-count wait (WaitForEventCountAsync) or a Playwright locator assertion instead."
}

if ($code -cmatch '(?<![A-Za-z])WaitForEventAsync\s*\(') {
    $failures += "the test calls WaitForEventAsync(...), which asks 'has this EVER happened' - so a second call in the same test returns instantly and asserts nothing. Use WaitForEventCountAsync, which is NOT banned by this check."
}

# FINDING 7 - the C#<->JS seam nothing else pins. Task 06 names the outcome field on a PascalCase C#
# record; hydrateLog() reads its camelCase JSON name off the wire. Task 05's tests assert the C# object
# and task 08's assert the SDK, so a browser test that FABRICATES the bytes between them is red before
# the fix and green after it while being completely blind to the two halves having agreed on a name.
# HoldFirstQueueReadAsync's whole point is that the REAL server answers; `Response = response` is the
# legal fulfil form. A literal `Body =` on a route handler is the fabrication.
if ($code -cmatch 'FulfillAsync' -and $code -cmatch '(?m)Body\s*=') {
    $failures += "the test fulfils a route with a literal Body = ... . That fabricates the server's response, so the test cannot see whether the server and the SDK agree on the JSON property name - it would pass against a server that never emits the field at all. Hold the real response instead (see HoldFirstQueueReadAsync in StaleQueueReadTests.cs) and fulfil with Response = response."
}

if ($failures.Count -gt 0) {
    Write-Output "=== Forbidden Playwright idioms ($($failures.Count)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output "No forbidden Playwright wait idioms in $path."
exit 0
