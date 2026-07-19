# catches: a diagram renderer that reaches Mermaid over the network (a mermaid URL in the source) instead of
#          inlining the vendored offline runtime - a green DiagramBlock suite could still ship a
#          network-dependent artifact if a test missed it, breaking the portable-artifact invariant (1).
#          Structural guard scoped to the one renderer file this task owns. QUOTE-AGNOSTIC + comment-stripped:
#          a C#-escaped/verbatim link (sb.Append("<script src=\"https://cdn…/mermaid.min.js\">")) has no bare
#          quote around the URL, so a quote-specific regex misses it. We strip comments, then FAIL on ANY
#          mermaid URL regardless of the quoting around it, and still require the vendored MermaidResource.
$renderer = "src/Charter.Core/CharterRenderer.cs"
$raw = Get-Content -Raw $renderer
# Strip C# comments so a commented-out URL cannot false-FAIL. Line comments use a negative lookbehind for ':'
# so the '//' inside 'https://' is NOT mistaken for a comment marker (which would eat the URL and go blind).
$code = [regex]::Replace($raw, '/\*[\s\S]*?\*/', ' ')
$code = [regex]::Replace($code, '(?<!:)//[^\r\n]*', ' ')
# Quote-agnostic: a mermaid URL must never appear in the renderer source, however it is quoted/escaped. The
# char class stops the URL at a quote (' or "), a backslash (the \" escape), whitespace, or '>' so an
# escaped-quote CDN link still matches up to 'mermaid'.
if ($code -match 'https?://[^"''\\\s>]*mermaid') {
    Write-Output "$renderer contains a Mermaid URL - the vendored offline runtime must be INLINED, not fetched over the network, however the link is quoted/escaped (portable-artifact invariant 1)."
    exit 1
}
if ($code -notmatch 'MermaidResource') {
    Write-Output "$renderer does not reference MermaidResource - the diagram renderer must inline the vendored Mermaid library from the embedded resource, not a hard-coded/remote source."
    exit 1
}
exit 0
