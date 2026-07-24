using System.Net;

namespace Charter.Server;

/// <summary>
/// Configuration for a <see cref="ReviewServer"/>. Plain data with safe defaults — this is not a
/// behavioral stub: it deliberately binds loopback-only on an OS-chosen ephemeral port and does not open a
/// browser unless asked.
/// </summary>
public sealed class ReviewServerOptions
{
    /// <summary>
    /// The address to bind. Defaults to <see cref="IPAddress.Loopback"/> so the review server is reachable
    /// only from the local machine.
    /// </summary>
    public IPAddress BindAddress { get; set; } = IPAddress.Loopback;

    /// <summary>The TCP port to bind. <c>0</c> (the default) lets the OS choose an ephemeral port.</summary>
    public int Port { get; set; }

    /// <summary>Whether to open the system browser at the served capability URL on start.</summary>
    public bool OpenBrowser { get; set; }

    /// <summary>
    /// The directory the server writes its durable review sidecar into (§1.6). When set, the server persists
    /// the queued annotations/answers to a server-owned file under this directory on every change and
    /// rehydrates from it on start, so a <c>charter review</c> crash before drain loses nothing. When
    /// <c>null</c> (the default), durability is off and the queues are purely in-memory — the pre-sidecar
    /// behaviour, kept so unit tests that do not exercise durability neither persist nor touch the state dir.
    /// Production (<c>charter review</c>) sets this to <see cref="StateDirectory.Sidecars"/>.
    /// </summary>
    public string? SidecarDirectory { get; set; }
}
