using System.Text.RegularExpressions;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Deterministic (no-browser) guards for the shared document shell (Charter #38) and the Mermaid-under-CSP
/// fix (#37). These run on every OS and catch the symptoms the C#-string golden tests were blind to:
/// <list type="bullet">
///   <item><description>the renderer emits a COMPLETE, STYLED document (doctype/html/head/body + inline
///     stylesheet), not a bare fragment;</description></item>
///   <item><description>the inlined Mermaid init selects a NON-iframe securityLevel, so the diagram renders as
///     inline SVG under the strict CSP instead of the sandboxed-iframe/data: path;</description></item>
///   <item><description>no <c>${…}</c> template literal leaks into the rendered markup OUTSIDE the
///     <c>&lt;script&gt;</c> that legitimately carries the vendored library.</description></item>
/// </list>
/// </summary>
[Trait("Category", "DocumentShell")]
public class DocumentShellTests
{
    private const string DiagramMarkdown = ":::diagram\ngraph TD\nA-->B\n:::";

    [Fact]
    public void Render_EmitsCompleteStyledDocument()
    {
        var html = CharterRenderer.Render("# Title\n\nProse.");

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<html", html);
        Assert.Contains("<head>", html);
        Assert.Contains("</head>", html);
        Assert.Contains("<body>", html);
        Assert.Contains("</body>", html);
        Assert.Contains("</html>", html);

        // The bundled stylesheet is inlined (never an external <link>) — proven by distinctive callout selectors
        // that only Charter's stylesheet defines.
        Assert.Contains("<style>", html);
        Assert.Contains(".note", html);
        Assert.Contains(".warn", html);
        Assert.DoesNotContain("<link", html);
    }

    [Fact]
    public void Render_MermaidInit_SelectsNonIframeSecurityLevel()
    {
        var html = CharterRenderer.Render(DiagramMarkdown);

        // The init must pin a non-iframe securityLevel so Mermaid renders inline SVG under `default-src 'none'`
        // (the sandboxed-iframe/data: path is CSP-blocked). 'antiscript' also strips <script> from labels.
        Assert.Contains("securityLevel: 'antiscript'", html);
        Assert.DoesNotContain("securityLevel: 'sandbox'", html);
    }

    [Fact]
    public void Render_NoTemplateLiteralLeaksOutsideScripts()
    {
        var html = CharterRenderer.Render(DiagramMarkdown);

        // The vendored library legitimately contains `${…}` template literals — but ONLY inside its <script>.
        // Strip every <script>…</script> region; nothing that remains (the actual page markup) may carry a `${`,
        // which would signal the library tore out of its script and leaked into the document (#37).
        var withoutScripts = Regex.Replace(html, "<script[^>]*>.*?</script>", string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Assert.DoesNotContain("${", withoutScripts);
    }
    /// <summary>
    /// The reserve is the SDK's, never the renderer's. `render` and `export` share charter.css and must keep
    /// emitting a centred, full-width document — an artifact someone opens without a review server has no
    /// panel to make room for, and a 340px gutter in a shared deliverable would be inexplicable.
    /// </summary>
    [Fact]
    public void TheExportedArtifactReservesNothing()
    {
        var html = CharterRenderer.Render("# Plan\n\nA paragraph.\n");

        Assert.DoesNotContain("charter-reserved", html, StringComparison.Ordinal);
        Assert.DoesNotContain("padding-right: 340px", html, StringComparison.Ordinal);
    }

}
