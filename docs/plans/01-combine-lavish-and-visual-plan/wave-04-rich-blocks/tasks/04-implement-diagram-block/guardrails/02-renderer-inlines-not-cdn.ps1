# catches: a diagram renderer that links Mermaid from a CDN (<script src="https://…mermaid…">) instead of
#          inlining the vendored offline runtime - a green DiagramBlock suite could still ship a
#          network-dependent artifact if a test missed it, breaking the portable-artifact invariant (1).
#          Structural guard scoped to the one renderer file this task owns: forbid a CDN script src to
#          mermaid, and require the renderer to reference the vendored MermaidResource loader.
$renderer = "src/Charter.Core/CharterRenderer.cs"
$code = Get-Content -Raw $renderer
if ($code -match '(?i)<script[^>]*src\s*=\s*["'']https?://[^"'']*mermaid') {
    Write-Output "$renderer emits a CDN <script src> to Mermaid - the vendored offline runtime must be INLINED, not linked over the network (portable-artifact invariant 1)."
    exit 1
}
if ($code -notmatch 'MermaidResource') {
    Write-Output "$renderer does not reference MermaidResource - the diagram renderer must inline the vendored Mermaid library from the embedded resource, not a hard-coded/remote source."
    exit 1
}
exit 0
