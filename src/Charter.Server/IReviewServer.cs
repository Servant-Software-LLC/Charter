namespace Charter.Server;

/// <summary>
/// A running local review server. Disposing it stops the server and frees the bound port.
/// </summary>
public interface IReviewServer : IDisposable
{
    /// <summary>The loopback URI the server is listening on (host <c>127.0.0.1</c>, OS-chosen port).</summary>
    Uri Address { get; }
}
