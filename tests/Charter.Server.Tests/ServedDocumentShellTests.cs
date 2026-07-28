using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Deterministic (no-browser) guard that the REAL served review page — the exact bytes
/// <see cref="ReviewServer"/> writes, SDK injected — is a complete, styled document (Charter #38) whose Mermaid
/// runtime is set up to render inline SVG under the served-page CSP (#37). Runs on every OS; it is the cheap
/// counterpart to the headless-browser acceptance test in Charter.Browser.Tests, catching the fragment /
/// broken-Mermaid symptoms even where a browser is unavailable.
/// </summary>
[Trait("Category", "ReviewServer")]
public class ServedDocumentShellTests
{
    private const string Plan =
        "# Served Shell Plan\n\n" +
        "Prose.\n\n" +
        ":::note\nA note.\n:::\n\n" +
        ":::diagram\ngraph TD\nA-->B\n:::\n";

    [Fact]
    public async Task ServedPage_IsCompleteStyledDocument_WithNonIframeMermaid_AndNoTemplateLeak()
    {
        var planPath = Path.Combine(Path.GetTempPath(), "charter-shell-" + System.Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, Plan);
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var keyedUri = new UriBuilder(server.Address) { Query = "key=" + session.Key.Value }.Uri;
            using var response = await client.GetAsync(keyedUri);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();

            // #38 — a complete, styled document, not a bare fragment.
            Assert.StartsWith("<!doctype html>", body);
            Assert.Contains("<html", body);
            Assert.Contains("<head>", body);
            Assert.Contains("</head>", body);
            Assert.Contains("<body>", body);
            Assert.Contains("</body>", body);
            Assert.Contains("<style>", body);

            // The serve-time SDK is still injected (inside the completed document now, before </body>).
            Assert.Contains("data-charter-sdk", body);

            // #37 — the Mermaid init pins a non-iframe securityLevel.
            Assert.Contains("securityLevel: 'antiscript'", body);
            Assert.DoesNotContain("securityLevel: 'sandbox'", body);

            // #37 — no `${…}` template literal leaks OUTSIDE the library <script>.
            var withoutScripts = Regex.Replace(body, "<script[^>]*>.*?</script>", string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            Assert.DoesNotContain("${", withoutScripts);

            // #51 — the OTHER half of "pan/zoom is review-time only": the SERVED page really does carry it.
            // Charter.Core.Tests.DiagramPanZoomArtifactTests pins that the exported artifact does not, and a
            // pair of assertions that could both hold with the feature simply absent would prove nothing.
            Assert.Contains("charter-zoom-bar", body, System.StringComparison.Ordinal);
            Assert.Contains("diagram-zoom-reset", body, System.StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(planPath))
            {
                File.Delete(planPath);
            }
        }
    }
}
