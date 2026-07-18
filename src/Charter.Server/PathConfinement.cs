namespace Charter.Server;

/// <summary>
/// The load-bearing confinement primitive: resolve a request path against a served root and return the
/// full on-disk path ONLY when it stays inside that root, rejecting <c>..</c> traversal and absolute-path
/// escapes. This is transport-independent, so it can be proven directly by a unit test rather than only
/// over HTTP.
/// </summary>
public static class PathConfinement
{
    /// <summary>
    /// Resolve <paramref name="requestPath"/> under <paramref name="root"/>. Returns the full path when it
    /// stays inside <paramref name="root"/>; returns <c>null</c> when the path escapes via <c>..</c>
    /// traversal or is an absolute path outside the root.
    /// </summary>
    /// <remarks>
    /// Both the root and the combined candidate are canonicalized with <see cref="Path.GetFullPath(string)"/>
    /// so <c>..</c> segments collapse before the containment check. <see cref="Path.Combine(string, string)"/>
    /// returns an absolute <paramref name="requestPath"/> verbatim, so an absolute escape canonicalizes to a
    /// path outside the root and is rejected the same way a relative traversal is.
    /// </remarks>
    public static string? Resolve(string root, string requestPath)
    {
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        requestPath ??= string.Empty;

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, requestPath));

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var normalizedRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (fullPath.Equals(normalizedRoot, comparison))
        {
            return fullPath;
        }

        var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootPrefix, comparison) ? fullPath : null;
    }
}
