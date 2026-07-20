# catches: a shipped skill that teaches a WRONG command surface - either missing one of the four real verbs
#          (charter render/review/export/handoff, all verified against src/Charter.Cli/Program.cs by the wave
#          entry gate) or, worse, inventing a CLI verb that does NOT exist. The negative half is the #176
#          fail-on-present assertion, anchored to the `charter <verb>` form so ordinary prose ("a charter
#          plan") is never caught. The denylist (poll/serve/annotate/publish/drain/preview/comment/share) is a
#          raised-bar mitigation, not a full allowlist (infeasible, F5): feedback drains over the /api/poll +
#          /api/answers HTTP endpoints, hosted publish/share is out of v1, review is the real serve verb, and
#          comment-in-place is a concept - none is a verb. Residual risk on an unlisted invention is
#          acknowledged. Scoped to the one skill directory this task owns (SKILL.md + references/).
$files = @(Get-ChildItem -Path "skills/charter" -Recurse -File -Filter *.md -ErrorAction SilentlyContinue)
if ($files.Count -lt 1) {
    Write-Output "skills/charter/ contains no .md files to check - the skill was not authored."
    exit 1
}
$all = ($files | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"

# POSITIVE: every real verb the skill must teach.
$requiredVerbs = @('charter render', 'charter review', 'charter export', 'charter handoff')
$missing = @()
foreach ($v in $requiredVerbs) {
    if ($all -notmatch [regex]::Escape($v)) { $missing += $v }
}
if ($missing.Count -gt 0) {
    Write-Output ("skills/charter/ does not teach the real verb(s): " + ($missing -join ', ') + " - the author->review->handoff workflow must name each command that exists.")
    exit 1
}

# NEGATIVE (#176): a CLI verb that does NOT exist must be ABSENT. Anchored `charter <verb>` so "a charter plan"
# is never caught; the word boundary keeps "charter's comment-in-place" (no space after charter) from firing.
$invented = [regex]::Match($all, '(?i)\bcharter\s+(poll|serve|annotate|publish|drain|preview|comment|share)\b')
if ($invented.Success) {
    Write-Output "skills/charter/ references '$($invented.Value)', which is NOT a real Charter CLI verb - the real verbs are render/review/export/handoff; describe only commands that exist in src/Charter.Cli/Program.cs (feedback drains over the /api/poll + /api/answers HTTP endpoints, not a CLI verb)."
    exit 1
}
exit 0
