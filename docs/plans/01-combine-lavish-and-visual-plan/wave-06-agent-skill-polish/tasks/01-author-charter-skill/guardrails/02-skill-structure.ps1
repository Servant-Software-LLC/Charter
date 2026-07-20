# catches: a SKILL.md that exists but is missing its load-bearing structure - the name/description
#          frontmatter (how a harness decides to load it), the when-to-use section, the block catalog naming
#          the :::question elicitation block, and the references/ pointer (the lean-SKILL-with-references
#          convention). Structural presence check scoped to the one SKILL.md this task owns (mirrors the
#          wave-3 sdk-structure guardrail). The verb-citation teeth are the sibling 03 guardrail.
$skill = "skills/charter/SKILL.md"
$content = Get-Content -Raw $skill
$required = [ordered]@{
    'frontmatter name field'        = '(?im)^\s*name:\s*\S'
    'frontmatter description field' = '(?im)^\s*description:\s*\S'
    'when-to-use section'           = '(?i)when to use'
    'block catalog section'         = '(?i)block catalog'
    'question elicitation block'    = ':::question'
    'references/ pointer'           = '(?i)references/'
}
$missing = @()
foreach ($k in $required.Keys) {
    if ($content -notmatch $required[$k]) { $missing += "$k (/$($required[$k])/)" }
}
if ($missing.Count -gt 0) {
    Write-Output ("skills/charter/SKILL.md is missing required structure: " + ($missing -join '; ') + ".")
    exit 1
}
# The frontmatter must actually be a leading YAML block (--- ... ---), not just the words appearing in prose.
if ($content -notmatch '(?s)\A\s*---\s.*?\n---') {
    Write-Output "skills/charter/SKILL.md does not open with a YAML frontmatter block (--- ... ---) carrying name/description - a skill needs real frontmatter, not the words in prose."
    exit 1
}
exit 0
