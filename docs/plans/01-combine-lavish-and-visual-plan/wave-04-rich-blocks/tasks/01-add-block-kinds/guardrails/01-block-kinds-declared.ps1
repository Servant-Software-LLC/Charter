# catches: the enum addition claimed but a member missing (or satisfied only by a comment). Structural
#          check scoped to the one file the task owns: each of the four new BlockKind members must be
#          declared as a real enum member (NAME then a comma or the enum's closing brace), not merely named
#          in a doc comment (the comment-blind trap - a bare token grep passes on `/// <see cref="Diagram"/>`).
$file = "src/Charter.Core/BlockModel.cs"
$raw = Get-Content -Raw $file
# Strip // line comments and /* */ block comments so a member named only in a doc/summary comment cannot
# satisfy the check (structural-vs-keyword, comment-blind).
$code = [regex]::Replace($raw, '/\*[\s\S]*?\*/', ' ')
$code = [regex]::Replace($code, '//[^\r\n]*', ' ')
$missing = @()
foreach ($member in @('Diagram', 'Comparison', 'Question', 'Diff')) {
    # An enum member is NAME in a value position - followed by `=` (an explicit value, e.g. `Diagram = 10,`),
    # `,`, or the closing `}` (the last member may omit the comma). Word-boundaries rather than line-anchored so
    # it holds for one-member-per-line (the file's style) AND an inline `{ A, B, C }` enum; comment-stripping
    # above rules out a doc-comment `<see cref>` match.
    if ($code -notmatch "\b$member\b\s*(=|,|\})") {
        $missing += $member
    }
}
if ($missing.Count -gt 0) {
    Write-Output ("$file is missing BlockKind enum member(s): " + ($missing -join ', ') + " (declared as a real enum member, not only in a comment).")
    exit 1
}
exit 0
