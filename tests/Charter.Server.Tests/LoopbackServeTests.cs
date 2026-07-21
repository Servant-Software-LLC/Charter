using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Loopback serve integration test: start a real <see cref="ReviewServer"/> on default (loopback,
/// ephemeral-port) options and drive it over the wire. It must (a) serve the rendered plan + injected SDK
/// marker to a request carrying the session capability key, on a <c>127.0.0.1</c> address; (b) reject a
/// request WITHOUT the key; and (c) reject a raw <c>..</c>-traversal request line (defense-in-depth — the
/// authoritative confinement proof is <see cref="PathConfinementTests"/>).
/// </summary>
[Trait("Category", "ReviewServer")]
public class LoopbackServeTests
{
    private const string Marker = "data-charter-sdk";
    private const string PlanHeadingText = "Charter Review Loopback Plan";

    [Fact]
    public async Task Server_ServesRenderedPlanWithSdk_ToKeyedRequest_AndRejectsUnauthorizedAndTraversal()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            var options = new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 };

            // Ephemeral port (Port = 0); the OS-chosen port is read back from Address. Disposed via `using`.
            using var server = ReviewServer.Start(session, options);

            // Address is a loopback (127.0.0.1) URI on the OS-chosen ephemeral port.
            Assert.Equal("127.0.0.1", server.Address.Host);
            Assert.True(server.Address.Port > 0, "an ephemeral port should have been chosen and reported");

            using var client = new HttpClient();

            // (a) A GET carrying the session's capability key returns 200 with the rendered plan + SDK marker.
            var keyedUri = new UriBuilder(server.Address) { Query = "key=" + session.Key.Value }.Uri;
            using var keyedResponse = await client.GetAsync(keyedUri);
            Assert.Equal(HttpStatusCode.OK, keyedResponse.StatusCode);
            var body = await keyedResponse.Content.ReadAsStringAsync();
            Assert.Contains(PlanHeadingText, body); // the rendered plan content is served
            Assert.Contains(Marker, body);          // with the serve-time SDK injected

            // (b) A GET WITHOUT the capability key is rejected (non-200).
            using var unkeyedResponse = await client.GetAsync(server.Address);
            Assert.NotEqual(HttpStatusCode.OK, unkeyedResponse.StatusCode);

            // (c) A raw ../ traversal request line is rejected (non-200). Defense-in-depth only.
            var traversalStatus = await SendRawTraversalStatusAsync(server.Address);
            Assert.NotEqual(200, traversalStatus);
        }
        finally
        {
            if (File.Exists(planPath))
            {
                File.Delete(planPath);
            }
        }
    }

    [Fact]
    public async Task Server_ReReadsSourceEachRequest_ServesEditedContent()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();
            var keyedUri = new UriBuilder(server.Address) { Query = "key=" + session.Key.Value }.Uri;

            // First GET serves the ORIGINAL heading (rendered from the source on disk).
            using (var first = await client.GetAsync(keyedUri))
            {
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                Assert.Contains(PlanHeadingText, await first.Content.ReadAsStringAsync());
            }

            // Edit the source file on disk — the live-reload contract is that ServeStatic re-reads + re-renders
            // from source on EVERY request, so no server restart is needed to see the change.
            const string editedHeading = "Charter Live Reload Edited Heading";
            File.WriteAllText(planPath, "# " + editedHeading + "\n\nAn edited paragraph inside the plan.\n");

            // A second GET serves the NEW content, and the original heading is gone.
            using (var second = await client.GetAsync(keyedUri))
            {
                Assert.Equal(HttpStatusCode.OK, second.StatusCode);
                var body = await second.Content.ReadAsStringAsync();
                Assert.Contains(editedHeading, body);
                Assert.DoesNotContain(PlanHeadingText, body);
            }
        }
        finally
        {
            if (File.Exists(planPath))
            {
                File.Delete(planPath);
            }
        }
    }

    [Fact]
    public async Task Server_ServedPage_CarriesSecurityHeaders()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var keyedUri = new UriBuilder(server.Address) { Query = "key=" + session.Key.Value }.Uri;
            using var response = await client.GetAsync(keyedUri);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Content-Security-Policy: same-origin XHR still allowed (connect-src 'self'), but images are
            // confined to 'self' + data: — no arbitrary remote host — and there is no wildcard anywhere.
            var csp = HeaderValue(response, "Content-Security-Policy");
            Assert.NotNull(csp);
            Assert.Contains("connect-src 'self'", csp);
            Assert.Contains("img-src 'self' data:", csp);
            Assert.DoesNotContain("*", csp);              // no wildcard source permitting an arbitrary host
            Assert.DoesNotContain("http", csp);           // and no explicit remote scheme

            // Referrer-Policy: no-referrer — critical, since the capability key rides the ?key= URL and must
            // never leak to a remote via the Referer header. X-Content-Type-Options: nosniff pins the MIME.
            Assert.Equal("no-referrer", HeaderValue(response, "Referrer-Policy"));
            Assert.Equal("nosniff", HeaderValue(response, "X-Content-Type-Options"));
        }
        finally
        {
            if (File.Exists(planPath))
            {
                File.Delete(planPath);
            }
        }
    }

    /// <summary>
    /// The first value of response header <paramref name="name"/> — checking both the response and content
    /// header collections, since <see cref="HttpClient"/> partitions headers between them — or null if absent.
    /// </summary>
    private static string? HeaderValue(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var fromResponse))
        {
            return System.Linq.Enumerable.FirstOrDefault(fromResponse);
        }

        return response.Content.Headers.TryGetValues(name, out var fromContent)
            ? System.Linq.Enumerable.FirstOrDefault(fromContent)
            : null;
    }

    private static string WriteTempPlan()
    {
        var path = Path.Combine(Path.GetTempPath(), "charter-plan-" + Guid.NewGuid().ToString("N") + ".mdx");
        File.WriteAllText(path, "# " + PlanHeadingText + "\n\nA paragraph inside the plan under review.\n");
        return path;
    }

    /// <summary>
    /// Send a <c>..</c>-traversal as a RAW request line and return the HTTP status code from the response.
    /// <see cref="HttpClient"/> / <see cref="Uri"/> normalize <c>../</c> away per RFC 3986 dot-segment
    /// rules BEFORE anything is sent, so a traversal built as a normal request URI never reaches the server
    /// and proves nothing. Writing the raw line over a <see cref="TcpClient"/> actually transmits the
    /// escaping path; under <c>HttpListener</c> the server stack refuses it (non-200) — defense-in-depth.
    /// </summary>
    private static async Task<int> SendRawTraversalStatusAsync(Uri address)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(address.Host, address.Port);
        var stream = tcp.GetStream();

        var raw = "GET /../../secret.txt HTTP/1.1\r\n" +
                  "Host: " + address.Host + ":" + address.Port + "\r\n" +
                  "Connection: close\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(raw);
        await stream.WriteAsync(bytes, 0, bytes.Length);

        using var reader = new StreamReader(stream, Encoding.ASCII);
        var statusLine = await reader.ReadLineAsync() ?? string.Empty;

        // Status line shape: "HTTP/1.1 <code> <reason>".
        var parts = statusLine.Split(' ');
        return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : 0;
    }
}
