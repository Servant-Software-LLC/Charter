# catches: an ArtifactExporter implementation that reaches for Charter.Server (e.g. to call SdkInjector
#          directly, or "just this once" add a project reference) instead of staying pure Charter.Core -
#          a green test suite could still miss a code path that only leaks the SDK marker under an
#          untested input. This is an ARCHITECTURAL proof, independent of and stronger than any single
#          test: if Charter.Core has NO project reference to Charter.Server, ArtifactExporter cannot
#          possibly pull in SdkInjector/SdkResource by construction, not merely by convention.
#          ELEMENT-SCOPED, not a bare substring match (#302 lesson from this task's own author-time
#          smoke test): Charter.Core.csproj legitimately CONTAINS the plain-text string "Charter.Server"
#          in an XML COMMENT (documenting how Charter.Server embeds its own SDK resource) - a bare
#          '-match "Charter\.Server"' false-FAILS on that comment. Require an actual
#          <ProjectReference ... Charter.Server ...> element instead.
$csproj = "src/Charter.Core/Charter.Core.csproj"
if (-not (Test-Path $csproj)) {
    Write-Output "$csproj not found."
    exit 1
}
$content = Get-Content -Raw -Path $csproj
if ($content -match '<ProjectReference[^>]*Charter\.Server') {
    Write-Output "$csproj has a <ProjectReference> to Charter.Server - Charter.Core (and ArtifactExporter within it) must stay independent of the review server / SDK-injection layer (invariant 6: narrow C#<->JS boundary; invariant 1: SDK injected only at serve time, never by export)."
    exit 1
}
exit 0
