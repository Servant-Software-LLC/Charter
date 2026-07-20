namespace Charter.Core;

/// <summary>
/// Exports Charter markdown to a TRULY offline, self-contained HTML artifact: it renders via
/// <see cref="CharterRenderer.Render(string)"/> and then (a) inlines every LOCAL image/media asset the plan
/// references (resolved under <paramref name="planDirectory"/>) as a <c>data:</c> URI, and (b) scrubs any
/// remaining local filesystem path from the shipped file. Unlike the review server, <c>export</c> NEVER
/// emits the serve-time annotation SDK (the <c>data-charter-sdk</c> marker is added only by
/// <c>Charter.Server.SdkInjector</c> at serve time) — the exported file is portable and SDK-free.
/// </summary>
/// <remarks>
/// This is the minimal stub. Task <c>02-implement-artifact-exporter</c> fills in the real inlining,
/// redaction, size-cap, and path-confinement behavior; the failing tests in
/// <c>ArtifactExporterTests</c> specify that contract.
/// </remarks>
public static class ArtifactExporter
{
    /// <summary>
    /// Render and export <paramref name="markdown"/> to a self-contained HTML artifact, inlining local
    /// assets found under <paramref name="planDirectory"/>.
    /// </summary>
    /// <param name="markdown">The Charter plan markdown to render and export.</param>
    /// <param name="planDirectory">
    /// The plan's directory — the confinement root for resolving and reading local asset references. Asset
    /// reads never escape this directory.
    /// </param>
    /// <param name="maxAssetBytes">
    /// The per-asset inlining size cap (default 10 MiB, 10,485,760 bytes). A single asset larger than this is
    /// omitted rather than inlined.
    /// </param>
    /// <param name="maxTotalAssetBytes">
    /// The cumulative cap across every asset inlined into one export (default 50 MiB, 52,428,800 bytes).
    /// Assets are considered in document order; once the running total of already-inlined bytes would exceed
    /// this, every subsequent local asset is omitted even if individually under <paramref name="maxAssetBytes"/>.
    /// </param>
    public static string Export(
        string markdown,
        string planDirectory,
        long maxAssetBytes = 10_485_760,
        long maxTotalAssetBytes = 52_428_800)
        => throw new NotImplementedException();
}
