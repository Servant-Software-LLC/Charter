# catches: a test method authored WITHOUT [Trait("Feature", "ReviewLogUnknownPanel")] - and, worse, the
#          trait written ONCE on the partial CLASS declaration instead of per method. Every file in
#          tests/Charter.Browser.Tests is a partial of ONE class carrying Category=BrowserAcceptance, so
#          that per-pair trait is the ONLY selector that can discriminate this pair. A C# attribute on
#          any partial declaration applies to the WHOLE TYPE, so a class-level Feature trait silently
#          widens `Category=BrowserAcceptance&Feature=ReviewLogUnknownPanel` to every browser test in the
#          project - and everything stays GREEN while task 08's tests-pass becomes a project-wide gate
#          (#455). A method merely MISSING the trait is the loud half: the red census reports "no test
#          named X ran", true but misleading, since the method exists and simply is not selected.
#          This is a STRUCTURAL fact about the file, not a runtime property, so no test can carry it -
#          the census's own selector is what it protects, which is why it cannot be demoted (#468).
# Measured baseline (#478): ReviewLogUnknownPanelTests.cs does not exist on the starting tree, so the
#          required token appears 0 times before this task. The precondition reports the absent file
#          rather than counting zero traits, so this is RED on arrival for an honest reason.
# Two-sided sample pair committed beside this file (#302/#468):
#          ../samples/03-every-test-carries-the-feature-trait.valid.cs   -> exit 0
#          ../samples/03-every-test-carries-the-feature-trait.invalid.cs -> exit 1
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
# ORDER IS LOAD-BEARING, and the obvious order is wrong. Stripping `//` FIRST truncates any string
# literal containing one - `"http://example"` - which orphans its opening quote; the literal regex then
# runs on to the NEXT quote in the file and swallows whatever lies between, [SkippableFact] attributes
# included. MEASURED during review: a file with 4 test methods counted as 3, and with one more swallowed
# attribute it counts as 2 and PASSES a file that is genuinely missing traits. Literals first.
$code = [regex]::Replace($raw, '(?s)/\*.*?\*/', '')
$code = [regex]::Replace($code, '"(?:[^"\\]|\\.)*"', '""')
$code = [regex]::Replace($code, '(?m)//.*$', '')

# This project spells its browser tests [SkippableFact] (they skip cleanly with no browser installed),
# so that is the anchor - counting [Fact] here would find nothing and pass vacuously.
$facts = [regex]::Matches($code, '\[\s*SkippableFact')

# Counted on RAW, deliberately: the trait's own argument is a string literal, which the strip above
# blanks. Requiring it on $code would make this clause unsatisfiable - the #470 mirror-image dead-end.
# A SECOND matcher runs over the stripped text purely so its match Indexes are comparable with the class
# declaration's index below; it matches the attribute's SHAPE, whose argument the strip has emptied.
$traits    = [regex]::Matches($raw,  '\[\s*Trait\s*\(\s*"Feature"\s*,\s*"ReviewLogUnknownPanel"\s*\)\s*\]')
$traitsPos = [regex]::Matches($code, '\[\s*Trait\s*\(\s*""\s*,\s*""\s*\)\s*\]')

$failures = @()

if ($facts.Count -lt 1) {
    $failures += "no [SkippableFact] test methods found in $path - the file exists but encodes no behaviour. The browser suite spells its tests [SkippableFact], not [Fact]."
}

# COUNTS ALONE ARE NOT ENOUGH, and the gap is the one case this guardrail uniquely exists for. A MISSING
# trait is already caught by the red census ("no test named X ran"). The case only this check can see is
# the trait on the CLASS - and an agent can satisfy a pure count comparison by writing it in BOTH places,
# leaving the filter just as widened. Measured during review: class + every method -> counts equal -> the
# old form exited 0. So locate the class declaration and reject any Feature trait ABOVE it, where a
# class-level attribute necessarily sits.
$classDecl = [regex]::Match($code, '(?m)^\s*(?:public|internal)?\s*(?:sealed\s+)?partial\s+class\s+ReviewLoopBrowserTests')
if ($classDecl.Success) {
    $aboveClass = @($traitsPos | Where-Object { $_.Index -lt $classDecl.Index })
    if ($aboveClass.Count -gt 0) {
        $failures += "a [Trait(`"Feature`", `"ReviewLogUnknownPanel`")] appears ABOVE the class declaration, so it is a CLASS-level attribute. Every file here is a partial of ONE type, and a C# attribute on any partial declaration applies to the WHOLE type - so this silently widens Category=BrowserAcceptance&Feature=ReviewLogUnknownPanel from this pair's tests to every browser test in the project (measured: 99 instead of 7). Put the trait on each METHOD and nowhere else."
    }
}

if ($facts.Count -ge 1 -and $traits.Count -lt $facts.Count) {
    $failures += "$($facts.Count) [SkippableFact] method(s) but only $($traits.Count) carry [Trait(`"Feature`", `"ReviewLogUnknownPanel`")]. Put the trait on EVERY METHOD, never once on the class: Category=BrowserAcceptance sits on the shared partial class and selects all browser tests, so the Feature trait is the only selector that picks out this pair - and a class-level attribute applies to the whole partial type, widening the filter to every browser test while looking correct."
}

if ($failures.Count -gt 0) {
    Write-Output "=== Feature-trait coverage gaps ($($failures.Count)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output "$($facts.Count) [SkippableFact] method(s), all carrying the ReviewLogUnknownPanel Feature trait."
exit 0
