# catches: a union that left git conflict markers in the review-log sources, or that dropped a
#          colliding sibling's hunk from them. After task 02 the DAG FORKS - the drain branch
#          (04: ReviewLogDrain.cs, PollCommand.cs) and the bridge branch (06: ReviewLogBridge.cs,
#          ReviewLogView.cs, then 08: sdk/charter-annotate.js) both build on the SAME changed
#          ReviewLogRead, so their union is the first tree where the two halves meet.
# scope:"integration" (see the sidecar) - UNION-SAFE, so the contribution check is written as a
#          CONDITIONAL: gate on the contribution being present, THEN verify it is real. It passes
#          trivially at a union where the contributing task has not run yet, which is what makes it
#          correct to re-run at every union rather than only at the end.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$tracked = @(
    'src/Charter.Server/ReviewLogStore.cs',
    'src/Charter.Server/ReviewLogPaths.cs',
    'src/Charter.Server/ReviewLogDrain.cs',
    'src/Charter.Server/ReviewLogBridge.cs',
    'src/Charter.Server/ReviewLogView.cs',
    'src/Charter.Cli/PollCommand.cs',
    'sdk/charter-annotate.js'
)

$failures = @()

foreach ($rel in $tracked) {
    $path = Join-Path $ws $rel
    if (-not (Test-Path -LiteralPath $path)) {
        # Every file above is tracked on master, so absence is a real problem rather than a
        # not-yet-produced artifact.
        $failures += "$rel is missing from the union - it is a tracked file, so this is not a not-yet-produced artifact."
        continue
    }
    $content = Get-Content -LiteralPath $path -Raw
    # Line-anchored ours/theirs markers only. A bare '=======' is deliberately NOT checked: it
    # false-fires on an ASCII banner or a Markdown setext rule and would red-halt a correct run.
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        $failures += "$rel contains git conflict markers - the union did not cleanly integrate."
    }
}

# CONDITIONAL contribution check on the ONE file the fork is built over. ReviewLogStore.cs carries ZERO
# occurrences of the word Unknown on master (verified at breakdown time), so this gate is false until
# task 02 lands and the check passes trivially before then. PollCommand.cs is deliberately NOT a subject
# here - it already carries the word six times for the pre-existing exit-4 path, so the gate could not
# discriminate a landed contribution from the status quo.
$storePath = Join-Path $ws 'src/Charter.Server/ReviewLogStore.cs'
if (Test-Path -LiteralPath $storePath) {
    $store = Get-Content -LiteralPath $storePath -Raw
    if ($store -match 'Unknown') {
        # Present as a real construct, not only inside a comment or a string. Strip block and line
        # comments first, then require the token to survive.
        # Strip block comments, then STRING LITERALS, then line comments - same order rule as the trait
        # guardrail, and for a second reason here: without the literal strip, an
        # `throw new InvalidOperationException("Unknown outcome")` surviving a merge that DROPPED the
        # enum member reads as a landed contribution. Measured as a false green during review.
        $code = [regex]::Replace($store, '(?s)/\*.*?\*/', '')
        $code = [regex]::Replace($code, '"(?:[^"\\]|\\.)*"', '""')
        $code = [regex]::Replace($code, '(?m)//.*$', '')
        if ($code -notmatch 'Unknown') {
            $failures += "'Unknown' survives only inside comments in src/Charter.Server/ReviewLogStore.cs - the third outcome's construct was dropped by the union."
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== Review-log union problems ($($failures.Count)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

exit 0
