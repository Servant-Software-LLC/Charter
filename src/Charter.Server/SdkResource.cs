namespace Charter.Server;

/// <summary>
/// Loads the real serve-time annotation SDK (<c>sdk/charter-annotate.js</c>, embedded into this assembly at
/// build time — see <c>Charter.Server.csproj</c>) and wraps it in a marked <c>&lt;script data-charter-sdk&gt;</c>
/// element ready for <see cref="SdkInjector.Inject"/>. Reading from the embedded resource (rather than off
/// disk) is what lets the SDK ship inside the single-file binary and be present in the served copy at runtime.
/// </summary>
internal static class SdkResource
{
    // Must match the <LogicalName> on the <EmbeddedResource> in Charter.Server.csproj.
    private const string ResourceName = "Charter.Server.charter-annotate.js";

    // Read once at startup and cached: the embedded JS never changes over a process's lifetime.
    private static readonly Lazy<string> LazyScriptElement = new(BuildScriptElement);

    /// <summary>
    /// The real annotation SDK wrapped in a <c>&lt;script data-charter-sdk&gt;</c> element. Carries the stable
    /// <c>data-charter-sdk</c> marker (so tests and the browser find it) and the real <c>CharterAnnotate</c>
    /// namespace body. Suitable to pass straight to <see cref="SdkInjector.Inject"/>.
    /// </summary>
    public static string ScriptElement => LazyScriptElement.Value;

    private static string BuildScriptElement()
    {
        var assembly = typeof(SdkResource).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded SDK resource '{ResourceName}' was not found. Ensure Charter.Server.csproj embeds " +
                "sdk/charter-annotate.js with that LogicalName.");
        using var reader = new StreamReader(stream);
        var js = reader.ReadToEnd();

        // Wrap the raw SDK in the marked script element. The marker attribute is load-bearing (invariant:
        // tests and the browser locate the SDK by data-charter-sdk); the body is the real SDK, not a placeholder.
        return "<script data-charter-sdk>\n" + js + "\n</script>";
    }
}
