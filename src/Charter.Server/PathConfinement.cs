namespace Charter.Server;

/// <summary>
/// The load-bearing confinement primitive: resolve a request path against a served root and return the
/// full on-disk path ONLY when it stays inside that root, rejecting <c>..</c> traversal and absolute-path
/// escapes. This is transport-independent, so it can be proven directly by a unit test rather than only
/// over HTTP.
/// </summary>
/// <remarks>
/// STUB (TDD red). The next task implements the real logic; every behavioral member throws
/// <see cref="NotImplementedException"/> for now so the authored tests compile and fail.
/// </remarks>
public static class PathConfinement
{
    /// <summary>
    /// Resolve <paramref name="requestPath"/> under <paramref name="root"/>. Returns the full path when it
    /// stays inside <paramref name="root"/>; returns <c>null</c> when the path escapes via <c>..</c>
    /// traversal or is an absolute path outside the root.
    /// </summary>
    public static string? Resolve(string root, string requestPath)
        => throw new NotImplementedException();
}
