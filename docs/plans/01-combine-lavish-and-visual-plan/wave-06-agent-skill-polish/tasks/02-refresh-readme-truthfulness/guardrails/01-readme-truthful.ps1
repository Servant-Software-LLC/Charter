# catches: a README left describing Charter as unbuilt - not just the two headline stale claims, but the
#          SURVIVING future-framing the narrow blacklist missed (F3): a kept "## How it will work (roadmap)"
#          heading with the four verbs pasted INSIDE that future-tense section still reads as unbuilt. NEGATIVE
#          (#176) fail-on-present for every stale marker + POSITIVE require-present for all four real verbs AND
#          a present-tense "## Usage" heading, so the verbs must live in a usage section, not a roadmap.
#          Scoped to the one file this task owns (README.md); the verbs are proven REAL by the wave entry gate.
$readme = "README.md"
$content = Get-Content -Raw $readme

# NEGATIVE: every stale / future-framing marker must be gone (case-insensitive; -match is case-insensitive).
$stale = @(
    'early scaffold',
    'Coming with the first release',
    'How it will work',
    '(roadmap)',
    'lands next',
    'next milestones',
    'you will run'
)
foreach ($s in $stale) {
    if ($content -match [regex]::Escape($s)) {
        Write-Output "README.md still contains the stale / future-framing marker '$s' - the renderer, review server, annotation loop, export, and handoff are all implemented now; rewrite the status/usage in the present tense (no roadmap framing around the shipped commands)."
        exit 1
    }
}

# POSITIVE: a present-tense Usage section heading.
if ($content -notmatch '(?im)^\s{0,3}#{1,6}\s+Usage\b') {
    Write-Output "README.md has no '## Usage' section heading - the real verbs must live in a present-tense Usage section, not a future-tense roadmap."
    exit 1
}

# POSITIVE: the usage story must document each real verb.
$requiredVerbs = @('charter render', 'charter review', 'charter export', 'charter handoff')
$missing = @()
foreach ($v in $requiredVerbs) {
    if ($content -notmatch [regex]::Escape($v)) { $missing += $v }
}
if ($missing.Count -gt 0) {
    Write-Output ("README.md does not document the real verb(s): " + ($missing -join ', ') + " - the Usage section must show the commands that now exist.")
    exit 1
}
exit 0
