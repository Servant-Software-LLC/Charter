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
}
