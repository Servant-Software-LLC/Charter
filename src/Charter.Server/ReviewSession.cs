namespace Charter.Server;

/// <summary>
/// One review session: the source <c>.mdx</c> plan being reviewed, the confined root the server is allowed
/// to serve files from, and the per-session <see cref="CapabilityKey"/> that authorizes requests.
/// </summary>
/// <remarks>
/// STUB (TDD red). The next task implements the real logic; every behavioral member throws
/// <see cref="NotImplementedException"/> for now so the authored tests compile and fail.
/// </remarks>
public sealed class ReviewSession
{
    /// <summary>The source Charter plan (<c>.mdx</c>) under review.</summary>
    public string SourcePath => throw new NotImplementedException();

    /// <summary>The confined root directory the server may serve files from — nothing outside it.</summary>
    public string Root => throw new NotImplementedException();

    /// <summary>The per-session capability key a request must present to be served.</summary>
    public CapabilityKey Key => throw new NotImplementedException();

    /// <summary>
    /// Create a review session bound to <paramref name="sourcePath"/>: confine the root to that plan's
    /// directory and mint a fresh <see cref="CapabilityKey"/>.
    /// </summary>
    public static ReviewSession Create(string sourcePath) => throw new NotImplementedException();
}
