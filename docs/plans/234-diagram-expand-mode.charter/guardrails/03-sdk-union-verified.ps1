# catches: a union that left git conflict markers in the annotation SDK, or that dropped a colliding
#          sibling's hunk from it. sdk/charter-annotate.js is the one file two tasks in this plan write
#          (02-implement-expand-affordance and 04-implement-expand-invariants), and it is embedded as a
#          resource into Charter.Server - so a corrupt union ships a torn script to every served page.
# scope:"integration" (see the sidecar) - UNION-SAFE, so it is written as a CONDITIONAL: gate on the
#          contribution being present, THEN verify it is real. It therefore passes trivially at a union
#          where a contributing task has not run yet, which is what makes it correct to re-run at every
#          union rather than only at the end.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
$path = Join-Path $ws 'sdk/charter-annotate.js'

if (-not (Test-Path -LiteralPath $path)) {
    # The SDK is a tracked file, so this is a real problem rather than a not-yet-produced artifact.
    Write-Output "sdk/charter-annotate.js is missing from the union - the annotation SDK is a tracked file, so this is not a not-yet-produced artifact."
    exit 1
}

$content  = Get-Content -LiteralPath $path -Raw
$failures = @()

# Line-anchored ours/theirs markers only. A bare '=======' is deliberately NOT checked: it false-fires
# on an ASCII banner or a Markdown setext rule and would red-halt a correct run.
if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
    $failures += "sdk/charter-annotate.js contains git conflict markers - the union did not cleanly integrate."
}

# CONDITIONAL contribution check: only assert the expand chrome is real once it is present at all.
# Before 02-implement-expand-affordance has merged this gate is false and the check passes trivially.
if ($content -match 'charter-expand') {
    # Present as a real construct, not only inside a comment or a string. Strip block and line comments
    # first, then require the token to survive.
    $code = [regex]::Replace($content, '(?s)/\*.*?\*/', '')
    $code = [regex]::Replace($code, '(?m)//.*$', '')
    if ($code -notmatch 'charter-expand') {
        $failures += "'charter-expand' survives only inside comments in sdk/charter-annotate.js - the expand chrome's construct was dropped by the union."
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== SDK union problems ($($failures.Count)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

exit 0
