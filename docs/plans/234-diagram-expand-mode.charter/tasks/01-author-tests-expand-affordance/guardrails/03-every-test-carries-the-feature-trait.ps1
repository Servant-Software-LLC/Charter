# catches: a test method authored WITHOUT [Trait("Feature", "DiagramExpandAffordance")]. Every file in
#          tests/Charter.Browser.Tests is a partial of ONE class carrying Category=BrowserAcceptance, so
#          that per-pair trait is the ONLY selector that can discriminate this pair's tests. A method
#          missing it is invisible to both halves of the pair: the red census reports it as "no test
#          named X ran" (true, but misleading - the method EXISTS, it just is not selected) and the
#          implementation task's forward check silently never asserts it. This is a STRUCTURAL fact about
#          the file, not a runtime property, so no test can carry it - the census's own selector is what
#          it is protecting, which is why it cannot be demoted to a test (#468).
# Measured baseline (#478): DiagramExpandAffordanceTests.cs does not exist on the starting tree, so the
#          required token appears 0 times before this task - the expected count for a required-present
#          clause. The precondition below reports the absent file rather than counting zero traits.
# Two-sided sample pair committed beside this file (#302/#468):
#          ../samples/03-every-test-carries-the-feature-trait.valid.cs   -> exit 0
#          ../samples/03-every-test-carries-the-feature-trait.invalid.cs -> exit 1
$ErrorActionPreference = 'Continue'

# DUAL-MODE, and this is a CONTRACT, not a convenience. `guardrails samples verify` (which the plan
# preflight runs before scheduling any task) invokes this script with the sample file as $args[0] and
# cwd = the workspace; it sets no GUARDRAILS_* variables. A guardrail that ignores $args[0] and only
# ever scans its hardcoded target reads the SAME file for both halves of its pair - so both exit
# identically and the pair proves nothing. Measured: this script did exactly that, and the harness
# caught it with "the guardrail may not be reading the sample at all".
$path = if ($args.Count -ge 1 -and -not [string]::IsNullOrWhiteSpace($args[0])) {
    $args[0]                                                        # sample-verification mode
} else {
    'tests/Charter.Browser.Tests/DiagramExpandAffordanceTests.cs'   # normal run
}

if (-not (Test-Path -LiteralPath $path)) {
    Write-Output "PRECONDITION: $path does not exist - this task's whole deliverable is missing. Every clause below would report a phantom gap, so nothing else is checked."
    exit 1
}

$raw = Get-Content -LiteralPath $path -Raw

# Strip comments AND string literals before scanning (#470/#97): a [Trait(...)] mentioned in a comment or
# quoted inside a message is a MENTION, not a USE, and would satisfy a naive scan over raw text.
$code = [regex]::Replace($raw, '(?s)/\*.*?\*/', '')
$code = [regex]::Replace($code, '(?m)//.*$', '')
$code = [regex]::Replace($code, '"(?:[^"\\]|\\.)*"', '""')

# Count the fact-attributes and the per-pair traits. This project spells its browser tests
# [SkippableFact] (they skip cleanly when no browser is installed), so that is the anchor.
$facts  = [regex]::Matches($code, '\[\s*SkippableFact')
$traits = [regex]::Matches($raw,  '\[\s*Trait\s*\(\s*"Feature"\s*,\s*"DiagramExpandAffordance"\s*\)\s*\]')

$failures = @()

if ($facts.Count -lt 1) {
    $failures += "no [SkippableFact] test methods found in $path - the file exists but encodes no behaviour. The browser suite spells its tests [SkippableFact], not [Fact]."
}

if ($traits.Count -lt $facts.Count) {
    $failures += "$($facts.Count) [SkippableFact] method(s) but only $($traits.Count) carry [Trait(`"Feature`", `"DiagramExpandAffordance`")]. Every test method in this file needs that trait: Category=BrowserAcceptance sits on the shared partial class and selects all browser tests, so the Feature trait is the only selector that can pick out this pair. A method without it is invisible to this pair's guardrails."
}

if ($failures.Count -gt 0) {
    Write-Output "=== Feature-trait coverage gaps ($($failures.Count)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output "$($facts.Count) test method(s), all carrying the DiagramExpandAffordance Feature trait."
exit 0
