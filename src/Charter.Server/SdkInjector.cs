namespace Charter.Server;

/// <summary>
/// Injects the serve-time Charter annotation SDK <c>&lt;script&gt;</c> into a rendered plan's HTML just
/// before it is served, so the static artifact stays SDK-free on disk and only the served copy is wired
/// for the review loop.
/// </summary>
public static class SdkInjector
{
    private const string ClosingBody = "</body>";

    /// <summary>
    /// Return <paramref name="html"/> with <paramref name="sdkScript"/> injected — before <c>&lt;/body&gt;</c>
    /// when that tag is present, otherwise appended — WITHOUT mutating the input. Strings are immutable, so
    /// this always produces a new string and leaves the caller's <paramref name="html"/> untouched. The
    /// injected script is the serve-time SDK and MUST carry the stable marker attribute
    /// <c>data-charter-sdk</c> so a test (or the browser) can find it deterministically.
    /// </summary>
    public static string Inject(string html, string sdkScript)
    {
        html ??= string.Empty;
        sdkScript ??= string.Empty;

        var closingBodyIndex = html.LastIndexOf(ClosingBody, StringComparison.OrdinalIgnoreCase);
        if (closingBodyIndex < 0)
        {
            return html + sdkScript;
        }

        return string.Concat(html.AsSpan(0, closingBodyIndex), sdkScript, html.AsSpan(closingBodyIndex));
    }
}
