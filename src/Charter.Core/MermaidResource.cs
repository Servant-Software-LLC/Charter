namespace Charter.Core;

/// <summary>
/// Loads the vendored Mermaid diagram runtime (<c>assets/mermaid.min.js</c>, Mermaid v11.16.0, MIT — embedded
/// into this assembly at build time, see <c>Charter.Core.csproj</c>) and exposes it as raw library text the
/// renderer can inline. Reading from the embedded resource (rather than off disk) is what lets Mermaid ship
/// INSIDE the rendered HTML so a saved <c>:::diagram</c> opens standalone with no network (load-bearing
/// invariant 1 — portable artifact); it is NEVER emitted as a CDN <c>&lt;script src&gt;</c>. Mirrors
/// <c>Charter.Server.SdkResource</c>.
/// </summary>
internal static class MermaidResource
{
    // Must match the <LogicalName> on the <EmbeddedResource> in Charter.Core.csproj.
    private const string ResourceName = "Charter.Core.mermaid.min.js";

    // Read once and cached: the embedded library never changes over a process's lifetime.
    private static readonly Lazy<string> LazyLibrary = new(ReadLibrary);

    /// <summary>
    /// The raw Mermaid library JavaScript (the real minified v11.16.0 build that exposes
    /// <c>globalThis.mermaid</c>), exactly as vendored — no <c>&lt;script&gt;</c> wrapper, no
    /// <c>&lt;pre class="mermaid"&gt;</c> markup, and no <c>mermaid.initialize</c>/<c>mermaid.run</c> init call.
    /// The consuming diagram block (<c>04-implement-diagram-block</c>) wraps and inlines this into the artifact.
    /// </summary>
    public static string Library => LazyLibrary.Value;

    private static string ReadLibrary()
    {
        var assembly = typeof(MermaidResource).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded Mermaid resource '{ResourceName}' was not found. Ensure Charter.Core.csproj embeds " +
                "assets/mermaid.min.js with that LogicalName.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
