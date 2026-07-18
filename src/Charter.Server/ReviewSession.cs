namespace Charter.Server;

/// <summary>
/// One review session: the source <c>.mdx</c> plan being reviewed, the confined root the server is allowed
/// to serve files from, and the per-session <see cref="CapabilityKey"/> that authorizes requests.
/// </summary>
public sealed class ReviewSession
{
    private ReviewSession(string sourcePath, string root, CapabilityKey key)
    {
        SourcePath = sourcePath;
        Root = root;
        Key = key;
    }

    /// <summary>The source Charter plan (<c>.mdx</c>) under review.</summary>
    public string SourcePath { get; }

    /// <summary>The confined root directory the server may serve files from — nothing outside it.</summary>
    public string Root { get; }

    /// <summary>The per-session capability key a request must present to be served.</summary>
    public CapabilityKey Key { get; }

    /// <summary>
    /// Create a review session bound to <paramref name="sourcePath"/>: confine the root to that plan's
    /// directory and mint a fresh <see cref="CapabilityKey"/>.
    /// </summary>
    public static ReviewSession Create(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);

        var fullSource = Path.GetFullPath(sourcePath);
        var root = Path.GetDirectoryName(fullSource);
        if (string.IsNullOrEmpty(root))
        {
            root = Directory.GetCurrentDirectory();
        }

        return new ReviewSession(fullSource, root, CapabilityKey.Create());
    }
}
