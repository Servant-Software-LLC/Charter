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

    /// <summary>
    /// Charter #184 — the same shell, over a plan whose ONLY diagram is nested inside a <c>::::note</c>.
    /// There is deliberately no top-level diagram: with one present the runtime is inlined for that block and
    /// the nested one draws by coincidence, which is what made the defect read as intermittent.
    /// </summary>
    private const string NestedDiagramPlan =
        "# Nested Diagram Plan\n\n" +
        "Prose, and no top-level diagram anywhere in this plan.\n\n" +
        "::::note\n" +
        "A callout that explains itself with a picture.\n\n" +
        ":::diagram\ngraph TD\nIngress-->Auth\n:::\n" +
        "::::\n";

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

    /// <summary>
    /// Charter #184 — the served page inlines the Mermaid runtime for a diagram that is NOT a direct child of
    /// the document. <c>hasDiagram</c> was raised by the same top-level-only walk as the anchor pass, so this
    /// page carried the diagram's SOURCE TEXT and no runtime at all. Deterministic and on every OS: the
    /// browser suite proves the block becomes a picture, this proves the bytes that make it one are served.
    /// </summary>
    [Fact]
    public async Task ServedPage_InlinesTheMermaidRuntime_ForADiagramNestedInsideACallout()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-nested-" + System.Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, NestedDiagramPlan);
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

            // The block has always rendered; what was missing is the runtime that turns it into a picture —
            // the vendored library's BYTES (a bootstrap alone would leave it blank offline) and the run call.
            Assert.Contains("<pre class=\"mermaid\"", body, System.StringComparison.Ordinal);
            Assert.Contains("__esbuild_esm_mermaid_nm", body, System.StringComparison.Ordinal);
            Assert.Contains("mermaid.run(", body, System.StringComparison.Ordinal);
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
