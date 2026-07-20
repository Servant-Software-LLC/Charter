using System.IO;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Security-hardening tests for the renderer and export (batch A1). Two concerns:
/// <list type="number">
///   <item><description>raw HTML in ordinary PLAN markdown is ESCAPED (rendered as visible text), never
///     passed through as live markup — the block catalog is the only sanctioned rich surface, and the one
///     deliberate raw-HTML escape hatch is <c>:::custom-html</c>;</description></item>
///   <item><description>the AI-authored surfaces inside catalog blocks — a <c>:::question</c> option and
///     title, a <c>:::diagram</c> source, a <c>:::diff</c> line — are HTML-escaped so <c>" &lt; &gt; &amp;</c>
///     cannot break out of the element they sit in;</description></item>
///   <item><description>the EXPORTED self-contained artifact carries a restrictive CSP that forbids any
///     remote/off-machine fetch while still allowing the inlined offline runtime.</description></item>
/// </list>
/// </summary>
[Trait("Category", "SecurityHardening")]
public class SecurityHardeningTests
{
    [Fact]
    public void Render_RawHtmlInPlanMarkdown_IsEscapedNotLive()
    {
        const string markdown =
            "<script>alert('xss')</script>\n\n" +
            "<img src=\"http://evil.example/track.png\" onerror=\"steal()\">";

        var html = CharterRenderer.Render(markdown);

        // The raw HTML is rendered as visible, inert text — not live markup that could run or phone home.
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("onerror=\"steal", html);
        Assert.DoesNotContain("<img src=\"http://evil", html);
    }

    [Fact]
    public void Render_CustomHtmlEscapeHatch_StillPassesRawHtmlThrough()
    {
        // :::custom-html is the ONE sanctioned raw-HTML surface: the author opted in, so its body stays live
        // even though bare prose HTML is now escaped.
        const string markdown =
            ":::custom-html\n" +
            "<div class=\"widget\"><span>live</span></div>\n" +
            ":::";

        var html = CharterRenderer.Render(markdown);

        Assert.Contains("<div class=\"widget\"><span>live</span></div>", html);
    }

    [Fact]
    public void Render_QuestionOptionWithSpecialChars_CannotBreakOutOfAttribute()
    {
        // An option value carrying a double-quote, angle brackets and a script tag must be escaped so it
        // cannot break out of value="..." into live markup.
        const string markdown =
            ":::question\n" +
            "{\"id\":\"q1\",\"title\":\"T\",\"mode\":\"single\",\"target\":\"human\",\"options\":[\"a\\\"><script>x</script>\"]}\n" +
            ":::";

        var html = CharterRenderer.Render(markdown);

        Assert.DoesNotContain("<script>x</script>", html);
        Assert.DoesNotContain("\"><script", html); // no attribute breakout
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Render_QuestionTitleWithSpecialChars_IsEscaped()
    {
        const string markdown =
            ":::question\n" +
            "{\"id\":\"q1\",\"title\":\"a<script>t</script>&\\\"z\",\"mode\":\"free-text\",\"target\":\"human\"}\n" +
            ":::";

        var html = CharterRenderer.Render(markdown);

        Assert.DoesNotContain("<script>t</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Render_DiagramSourceWithHtml_IsEscaped()
    {
        const string markdown =
            ":::diagram\n" +
            "graph TD; A[\"</pre><script>x</script>\"]-->B\n" +
            ":::";

        var html = CharterRenderer.Render(markdown);

        // The Mermaid source is escaped so it cannot break out of <pre class="mermaid">.
        Assert.DoesNotContain("</pre><script>x</script>", html);
        Assert.Contains("&lt;/pre&gt;", html);
    }

    [Fact]
    public void Render_DiffLineWithHtml_IsEscaped()
    {
        const string markdown =
            ":::diff\n" +
            "+<script>x</script>\n" +
            "-safe line\n" +
            ":::";

        var html = CharterRenderer.Render(markdown);

        Assert.DoesNotContain("<script>x</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Export_Artifact_CarriesRestrictiveCspInHead_ThatBlocksRemoteFetch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-csp-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string markdown = ":::diagram\ngraph TD; A-->B;\n:::";

            var html = ArtifactExporter.Export(markdown, dir);

            // The CSP meta lives inside the head.
            var headEnd = html.IndexOf("</head>", System.StringComparison.Ordinal);
            Assert.True(headEnd >= 0, "export must have a <head>");
            var head = html[..headEnd];
            Assert.Contains("Content-Security-Policy", head);

            // It forbids any remote/off-machine connection outright.
            Assert.Contains("default-src 'none'", html);
            Assert.Contains("connect-src 'none'", html);

            // ...while still allowing the inlined offline runtime: the vendored Mermaid library bytes ride
            // inside the artifact, and inline script is permitted so a saved :::diagram still renders.
            Assert.Contains("script-src 'unsafe-inline'", html);
            Assert.Contains("__esbuild_esm_mermaid_nm", html);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
