# catches: Mermaid referenced but not actually vendored offline - a missing/truncated library, a missing
#          csproj embed, or (the portability-breaking failure) a CDN <script src> instead of an embedded
#          copy. Proves the runtime ships INSIDE the artifact (invariant 1) via deterministic static checks
#          (no JS toolchain exists - the SVG actually rendering in a browser is surfaced, not checked here).
$asset = "src/Charter.Core/assets/mermaid.min.js"
if (-not (Test-Path $asset)) {
    Write-Output "$asset does not exist - the Mermaid runtime was not vendored offline (a rendered :::diagram would not open standalone)."
    exit 1
}
# A real minified Mermaid build is hundreds of KB; a stub/placeholder is a red flag.
$size = (Get-Item $asset).Length
if ($size -lt 51200) {
    Write-Output "$asset is only $size bytes - too small to be the real Mermaid library (looks like a stub/truncated file, not the vendored runtime)."
    exit 1
}
$csproj = "src/Charter.Core/Charter.Core.csproj"
if ((Get-Content -Raw $csproj) -notmatch '<EmbeddedResource[^>]*mermaid\.min\.js|<LogicalName>\s*Charter\.Core\.mermaid\.min\.js') {
    Write-Output "$csproj does not embed assets/mermaid.min.js as an EmbeddedResource with LogicalName Charter.Core.mermaid.min.js - it would be absent at runtime."
    exit 1
}
$loader = "src/Charter.Core/MermaidResource.cs"
if (-not (Test-Path $loader)) {
    Write-Output "$loader does not exist - no loader reads the embedded Mermaid library back for the renderer to inline."
    exit 1
}
if ((Get-Content -Raw $loader) -notmatch 'GetManifestResourceStream|Charter\.Core\.mermaid\.min\.js') {
    Write-Output "$loader does not read the embedded Charter.Core.mermaid.min.js manifest resource."
    exit 1
}
# Portability guard: the vendored asset itself must not be a CDN redirect/shim.
if ((Get-Content -Raw $asset) -match '(?i)src\s*=\s*["'']https?://.*mermaid') {
    Write-Output "$asset contains a CDN <script src> to mermaid - it must be the embedded library, not a network link (breaks the portable-artifact invariant)."
    exit 1
}
exit 0
