namespace Charter.Core;

/// <summary>
/// Loads Charter's bundled stylesheet (<c>assets/charter.css</c> — embedded into this assembly at build time,
/// see <c>Charter.Core.csproj</c>) and exposes it as raw CSS text the shared document shell
/// (<see cref="CharterDocument"/>) inlines into a single <c>&lt;style&gt;</c> element. Reading from the embedded
/// resource (rather than off disk) keeps the CSS shipping INSIDE the rendered/exported HTML so the artifact
/// stays portable and offline (invariant 1); it is NEVER an external <c>&lt;link&gt;</c>. Mirrors
/// <see cref="MermaidResource"/>.
/// </summary>
internal static class CharterStyles
{
    // Must match the <LogicalName> on the <EmbeddedResource> in Charter.Core.csproj.
    private const string ResourceName = "Charter.Core.charter.css";

    // Read once and cached: the embedded stylesheet never changes over a process's lifetime.
    private static readonly Lazy<string> LazyCss = new(ReadCss);

    /// <summary>
    /// The bundled stylesheet CSS, exactly as vendored — no <c>&lt;style&gt;</c> wrapper. The shell
    /// (<see cref="CharterDocument.Wrap"/>) wraps and inlines it, one style source of truth shared by
    /// <c>render</c>, <c>review</c>, and <c>export</c>.
    /// </summary>
    public static string Css => LazyCss.Value;

    private static string ReadCss()
    {
        var assembly = typeof(CharterStyles).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded stylesheet resource '{ResourceName}' was not found. Ensure Charter.Core.csproj embeds " +
                "assets/charter.css with that LogicalName.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
