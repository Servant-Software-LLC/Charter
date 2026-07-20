# catches: a skill task that produced nothing, an empty/placeholder SKILL.md, or a references/ dir that only
#          NOMINALLY satisfies "has a .md" (a 0-byte references/empty.md - the hollow-shell pass the review
#          flagged, F1). Require SKILL.md AND each of the THREE NAMED playbooks the prompt instructs
#          (authoring-plans.md, review-loop.md, handoff.md), each over a per-file substance floor, so the
#          lean-SKILL-with-references convention has real depth, not a stub. Presence + size check scoped to
#          the one skill directory this task owns.
$skill = "skills/charter/SKILL.md"
if (-not (Test-Path $skill)) {
    Write-Output "skills/charter/SKILL.md does not exist - the bundled charter usage skill was not authored."
    exit 1
}
$skillSize = (Get-Item $skill).Length
if ($skillSize -lt 400) {
    Write-Output "skills/charter/SKILL.md is only $skillSize bytes - too small to be a real skill (expected a lean-but-substantive SKILL.md)."
    exit 1
}
$refDir = "skills/charter/references"
if (-not (Test-Path $refDir -PathType Container)) {
    Write-Output "skills/charter/references/ does not exist - the skill must keep depth in references/ (lean-SKILL-with-references convention)."
    exit 1
}
# The three named playbooks the prompt instructs, each with a real substance floor (not a 0-byte shell).
$required = @('authoring-plans.md', 'review-loop.md', 'handoff.md')
foreach ($name in $required) {
    $path = Join-Path $refDir $name
    if (-not (Test-Path $path)) {
        Write-Output "skills/charter/references/$name is missing - author the three named playbooks: authoring-plans.md, review-loop.md, handoff.md."
        exit 1
    }
    $size = (Get-Item $path).Length
    if ($size -lt 300) {
        Write-Output "skills/charter/references/$name is only $size bytes - too small to be a real playbook (>= 300 bytes expected); it must carry the author/review/handoff depth, not be a stub."
        exit 1
    }
}
exit 0
