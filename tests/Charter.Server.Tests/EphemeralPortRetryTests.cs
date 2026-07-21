using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Regression coverage for Charter #14: the ephemeral-port TOCTOU flake. <see cref="ReviewServer"/> reserves
/// an ephemeral port on a probe <see cref="TcpListener"/>, frees it, then binds an <see cref="HttpListener"/>
/// on it — so another process can grab the port in the gap and the bind throws
/// (<c>HttpListenerException</c> "conflicts with an existing registration" on Windows;
/// <c>AddressAlreadyInUse</c> elsewhere). The fix re-probes a fresh port and re-binds, bounded, ONLY on a
/// port-conflict exception. These tests drive that self-heal deterministically through the internal
/// <see cref="ReviewServer.StartCore"/> seam (a real occupied port + an injected port supplier) — no sleeps,
/// no timing dependence.
/// </summary>
[Trait("Category", "ReviewServer")]
public class EphemeralPortRetryTests
{
    [Fact]
    public async Task StartCore_WhenFirstEphemeralPortConflicts_SelfHealsToAFreshPort()
    {
        var planPath = WriteTempPlan();
        var (occupier, occupiedPort) = OccupyLoopbackPort();
        try
        {
            var session = ReviewSession.Create(planPath);

            // Supplier hands back the OCCUPIED port first (forcing the bind conflict + one retry), then real
            // fresh ephemeral ports. Without the retry, StartCore would throw on the very first attempt.
            var calls = 0;
            int Supplier()
            {
                calls++;
                return calls == 1 ? occupiedPort : ReserveLoopbackEphemeralPort();
            }

            using var server = ReviewServer.StartCore(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 }, Supplier);

            // The retry is load-bearing: the supplier was re-called, and the server landed on a DIFFERENT port.
            Assert.True(calls >= 2, "the first-attempt conflict should have driven at least one retry");
            Assert.NotEqual(occupiedPort, server.Address.Port);
            Assert.Equal("127.0.0.1", server.Address.Host);

            // And the self-healed bind is live: the keyed page actually serves 200.
            using var client = new HttpClient();
            var keyedUri = new UriBuilder(server.Address) { Query = "key=" + session.Key.Value }.Uri;
            using var response = await client.GetAsync(keyedUri);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            occupier.Close();
            DeleteIfExists(planPath);
        }
    }

    [Fact]
    public void StartCore_WhenPortConflictPersists_SurfacesAfterBoundedAttempts()
    {
        var planPath = WriteTempPlan();
        var (occupier, occupiedPort) = OccupyLoopbackPort();
        try
        {
            var session = ReviewSession.Create(planPath);

            // Supplier ALWAYS returns the occupied port, so every attempt conflicts. The retry is bounded, so
            // the conflict must SURFACE (not loop forever) after exactly EphemeralBindAttempts probes.
            var calls = 0;
            int Supplier()
            {
                calls++;
                return occupiedPort;
            }

            var thrown = Assert.ThrowsAny<Exception>(() => ReviewServer.StartCore(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 }, Supplier));

            Assert.True(
                ReviewServer.IsPortConflict(thrown),
                "a persistent conflict should surface as a port-conflict exception, not something swallowed");
            Assert.Equal(ReviewServer.EphemeralBindAttempts, calls);
        }
        finally
        {
            occupier.Close();
            DeleteIfExists(planPath);
        }
    }

    [Fact]
    public void StartCore_WhenSupplierRaisesNonPortFailure_SurfacesImmediatelyWithoutRetry()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);

            // A genuine, non-port failure raised while establishing the bind must NOT be retried away — it
            // surfaces on the first attempt (the supplier is called exactly once).
            var calls = 0;
            int Supplier()
            {
                calls++;
                throw new InvalidOperationException("genuine non-port failure");
            }

            Assert.Throws<InvalidOperationException>(() => ReviewServer.StartCore(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 }, Supplier));
            Assert.Equal(1, calls);
        }
        finally
        {
            DeleteIfExists(planPath);
        }
    }

    [Fact]
    public void IsPortConflict_RetriesOnlyPortConflicts_GenuineFailuresSurface()
    {
        // Port-in-use registration/socket collisions — these self-heal on a fresh ephemeral port.
        Assert.True(ReviewServer.IsPortConflict(new HttpListenerException(32)));    // ERROR_SHARING_VIOLATION
        Assert.True(ReviewServer.IsPortConflict(new HttpListenerException(183)));   // ERROR_ALREADY_EXISTS
        Assert.True(ReviewServer.IsPortConflict(new HttpListenerException(10048))); // WSAEADDRINUSE

        // Genuine, non-port failures — must surface immediately, never be retried away.
        Assert.False(ReviewServer.IsPortConflict(new HttpListenerException(5)));    // ERROR_ACCESS_DENIED
        Assert.False(ReviewServer.IsPortConflict(new InvalidOperationException()));
    }

    /// <summary>
    /// Bind a real <see cref="HttpListener"/> on a loopback ephemeral port so a SECOND bind on that same port
    /// deterministically conflicts. A tiny bounded setup retry keeps this helper from itself flaking on the
    /// very reserve-&gt;bind race the fix addresses (there is no concurrent contention in-test, so it settles
    /// on the first try in practice).
    /// </summary>
    private static (HttpListener Occupier, int Port) OccupyLoopbackPort()
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = ReserveLoopbackEphemeralPort();
            var occupier = new HttpListener();
            occupier.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                occupier.Start();
                return (occupier, port);
            }
            catch when (attempt < 10)
            {
                occupier.Close();
            }
        }
    }

    private static int ReserveLoopbackEphemeralPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static string WriteTempPlan()
    {
        var path = Path.Combine(Path.GetTempPath(), "charter-retry-" + Guid.NewGuid().ToString("N") + ".mdx");
        File.WriteAllText(path, "# Charter Ephemeral Port Retry Plan\n\nA paragraph inside the plan.\n");
        return path;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
