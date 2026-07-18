namespace Charter.Server;

/// <summary>
/// Injects the serve-time Charter annotation SDK <c>&lt;script&gt;</c> into a rendered plan's HTML just
/// before it is served, so the static artifact stays SDK-free on disk and only the served copy is wired
/// for the review loop.
/// </summary>
/// <remarks>
/// STUB (TDD red). The next task implements the real logic; every behavioral member throws
/// <see cref="NotImplementedException"/> for now so the authored tests compile and fail.
/// </remarks>
public static class SdkInjector
{
    /// <summary>
    /// Return <paramref name="html"/> with <paramref name="sdkScript"/> injected — before <c>&lt;/body&gt;</c>
    /// when that tag is present, otherwise appended — WITHOUT mutating the input. The injected script is the
    /// serve-time SDK and MUST carry the stable marker attribute <c>data-charter-sdk</c> so a test (or the
    /// browser) can find it deterministically.
    /// </summary>
    public static string Inject(string html, string sdkScript)
        => throw new NotImplementedException();
}
