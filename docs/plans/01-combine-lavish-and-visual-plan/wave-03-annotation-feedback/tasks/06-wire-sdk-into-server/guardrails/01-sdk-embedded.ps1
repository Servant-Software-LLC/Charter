# catches: the SDK left on disk but never embedded into the server assembly - a single-file binary would
#          then ship WITHOUT the SDK and serve nothing real to inject. Assert Charter.Server.csproj embeds
#          charter-annotate.js as an <EmbeddedResource>. Scoped to the one project file this task owns.
$csproj = "src/Charter.Server/Charter.Server.csproj"
if (-not (Test-Path $csproj)) {
    Write-Output "$csproj not found - cannot verify the SDK is embedded."
    exit 1
}
if ((Get-Content -Raw $csproj) -notmatch '<EmbeddedResource[^>]*charter-annotate\.js') {
    Write-Output "$csproj does not embed charter-annotate.js as an <EmbeddedResource> - the real SDK will be absent from the single-file binary at runtime."
    exit 1
}
exit 0
