namespace Charter.Server;

/// <summary>
/// A loopback HTTP server that serves one <see cref="ReviewSession"/>'s rendered, SDK-injected plan and
/// only honours requests presenting the session's <see cref="CapabilityKey"/>. Confinement is enforced by
/// <see cref="PathConfinement"/> so no request can escape the session's root.
/// </summary>
/// <remarks>
/// STUB (TDD red). The next task implements the real logic; every behavioral member throws
/// <see cref="NotImplementedException"/> for now so the authored tests compile and fail.
/// </remarks>
public sealed class ReviewServer : IReviewServer
{
    /// <summary>The loopback URI the server is listening on (host <c>127.0.0.1</c>, OS-chosen port).</summary>
    public Uri Address => throw new NotImplementedException();

    /// <summary>
    /// Start a loopback HTTP server for <paramref name="session"/>, serving its rendered + SDK-injected
    /// plan. Uses <paramref name="options"/> (or loopback-only, ephemeral-port defaults when null).
    /// </summary>
    public static ReviewServer Start(ReviewSession session, ReviewServerOptions? options = null)
        => throw new NotImplementedException();

    /// <summary>Stop the server and release the bound port.</summary>
    public void Dispose() => throw new NotImplementedException();
}
