# catches: a shipped skill that lists the four verbs but OMITS the load-bearing feedback-drain teaching (F2) -
#          the honesty crux. There is NO 'charter poll' CLI verb, so the skill MUST teach the raw HTTP drain:
#          GET /api/poll for queued annotations and GET /api/answers for :::question answers, on the loopback
#          server. Both tokens are distinctive and un-inventable, so a skill that skips the drain mechanic (or
#          reduces the workflow to a bare space-separated verb list) fails here. A covers-key-behaviors
#          lower-bound scoped to the one skill directory this task owns; both tokens are named in this task's
#          action.prompt.md (#157-safe).
$files = @(Get-ChildItem -Path "skills/charter" -Recurse -File -Filter *.md -ErrorAction SilentlyContinue)
if ($files.Count -lt 1) {
    Write-Output "skills/charter/ contains no .md files to check - the skill was not authored."
    exit 1
}
$all = ($files | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"
$required = [ordered]@{
    'annotation drain endpoint (GET /api/poll)'  = '/api/poll'
    'answer drain endpoint (GET /api/answers)'   = '/api/answers'
}
$missing = @()
foreach ($k in $required.Keys) {
    if ($all -notmatch [regex]::Escape($required[$k])) { $missing += $k }
}
if ($missing.Count -gt 0) {
    Write-Output ("skills/charter/ does not teach the feedback drain: missing " + ($missing -join '; ') + ". There is no 'charter poll' CLI verb - the skill MUST show draining annotations via GET /api/poll and question answers via GET /api/answers on the loopback server.")
    exit 1
}
exit 0
