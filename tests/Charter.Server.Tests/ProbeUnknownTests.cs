using System.Net;
using System.Net.Sockets;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Charter #217 — a probe that could not complete must say so, not report absence.
///
/// <para>
/// The defect these pin: <c>ProbeAsync</c> returned a nullable session, so <i>"nothing is listening"</i> and
/// <i>"I could not tell"</i> were the same value. Under load a probe against a LIVE review server timed out,
/// <c>charter poll</c> reported exit 3 (<c>NoSession</c>) — <i>the server is not running</i> — and the caller
/// deleted the descriptor that proved otherwise, so the wrong answer latched.
/// </para>
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","ProbeUnknown")].
/// </summary>
[Trait("Category", "ProbeUnknown")]
public class ProbeUnknownTests
{
    /// <summary>
    /// A listener that COMPLETES THE TCP HANDSHAKE and then never writes a response — the shape of a live
    /// server too busy to answer within the probe's deadline. Deliberately a raw <see cref="TcpListener"/>
    /// rather than an <see cref="HttpListener"/> that delays: the point is that the connection succeeds, so
    /// the client cannot fall back on <c>HttpRequestException</c> and must reach its timeout.
    /// </summary>
    private static (TcpListener Listener, Uri Address) StartSilentListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        _ = Task.Run(async () =>
        {
            var held = new List<TcpClient>();
            try
            {
                while (true)
                {
                    // Accept and HOLD. Never respond, never close — closing would surface as a transport
                    // error, which is a different (and correctly Absent) case.
                    held.Add(await listener.AcceptTcpClientAsync().ConfigureAwait(false));
                }
            }
            catch (Exception)
            {
                foreach (var c in held)
                {
                    try { c.Dispose(); } catch (Exception) { /* best effort */ }
                }
            }
        });

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return (listener, new Uri($"http://127.0.0.1:{port}/"));
    }

    [Fact]
    public async Task Probe_AcceptedButUnanswered_IsUnknown_NotAbsent()
    {
        var (listener, address) = StartSilentListener();
        try
        {
            using var client = new ReviewClient(address, "anykey00000000000000");

            // Generous outer budget: the deadline under test is ReviewClient's own ProbeTimeout, and binding
            // this assertion to the caller's token would test the token, not the probe.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var probe = await client.ProbeAsync(expectedSourcePath: null, cts.Token);

            Assert.Equal(ProbeOutcome.Unknown, probe.Outcome);

            // The distinction that decides whether a descriptor gets deleted. `!IsLive` is true for BOTH
            // non-live outcomes and is the spelling that caused #217; a caller must read IsAbsent.
            Assert.False(probe.IsLive);
            Assert.False(probe.IsAbsent);
            Assert.Null(probe.Session);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Probe_CallerCancellation_IsUnknown_NotAbsent()
    {
        var (listener, address) = StartSilentListener();
        try
        {
            using var client = new ReviewClient(address, "anykey00000000000000");

            // The caller gave up first. Still nothing learned about whether a session is there.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var probe = await client.ProbeAsync(expectedSourcePath: null, cts.Token);

            Assert.Equal(ProbeOutcome.Unknown, probe.Outcome);
            Assert.False(probe.IsAbsent);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Probe_NothingListening_IsAbsent_SoAPruneStaysSound()
    {
        // Reserve a port, then release it: a connection is refused outright. This is the one unambiguous
        // absence, and the fix must NOT have made pruning conservative to the point of never happening —
        // a descriptor for a genuinely dead server still has to be removable.
        var probeListener = new TcpListener(IPAddress.Loopback, 0);
        probeListener.Start();
        var port = ((IPEndPoint)probeListener.LocalEndpoint).Port;
        probeListener.Stop();

        using var client = new ReviewClient(new Uri($"http://127.0.0.1:{port}/"), "anykey00000000000000");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var probe = await client.ProbeAsync(expectedSourcePath: null, cts.Token);

        Assert.Equal(ProbeOutcome.Absent, probe.Outcome);
        Assert.True(probe.IsAbsent);
    }
}
