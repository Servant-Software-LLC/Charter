using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #51 — pan/zoom is a REVIEW-TIME affordance, and this is the deterministic half of that claim:
/// the renderer's output and the exported offline artifact carry none of it. The affordance is built by
/// <c>sdk/charter-annotate.js</c>, which is injected only when <c>charter review</c> serves the page
/// (invariant 1), so a saved <c>:::diagram</c> must still be the plain
/// <c>&lt;pre class="mermaid" id="…"&gt;</c> the exporter has always written — no zoom chrome, no tab stop,
/// no runtime classes, and no inline sizing.
///
/// <para>The reason this is worth a C#-string test at all, when the browser suite is the real guard: the
/// browser suite SKIPS where Chromium is unavailable, and invariant 1 is the one property that must hold on
/// every OS on every run. The behaviour — that an oversized diagram actually zooms and pans, that a drag is
/// not an annotation, and that the overlay stays aligned — is asserted in
/// <c>Charter.Browser.Tests.ReviewLoopBrowserTests</c>, where it is visible.</para>
/// </summary>
[Trait("Category", "DiagramPanZoom")]
public class DiagramPanZoomArtifactTests
{
    private const string DiagramMarkdown =
        ":::diagram\n" +
        "graph LR\n" +
        "Ingress[Public ingress gateway] --> Auth[Authentication service]\n" +
        "Auth --> Store[Session store]\n" +
        ":::";

    /// <summary>Every marker the SDK's pan/zoom chrome is built from — none may reach a saved file.</summary>
    private static readonly string[] ZoomChrome =
    {
        "charter-zoom",
        "charter-zoomable",
        "charter-zoomed",
        "charter-panning",
        "diagram-zoom"
    };

    [Fact]
    public void Render_Diagram_IsThePlainMermaidPreWithNoPanZoomChrome()
    {
        var block = BlockDocument.Parse(DiagramMarkdown).Blocks[0];
        var body = CharterRenderer.RenderBody(DiagramMarkdown);

        Assert.Equal(BlockKind.Diagram, block.Kind);
        Assert.Contains($"<pre class=\"mermaid\" id=\"{block.Id}\">", body, StringComparison.Ordinal);

        // The pan/zoom tab stop, role and label are added by the SDK at RUNTIME and removed on dispose();
        // the renderer emits none of them. Asserted against the block's own opening TAG rather than the
        // rendered body, which also carries the 3.5 MB vendored Mermaid library — that blob contains the
        // literal "tabindex", so a whole-body scan for it measures the third-party bytes, not Charter's.
        var tag = DiagramPreTag(body);
        Assert.DoesNotContain("tabindex", tag, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role=", tag, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-", tag, StringComparison.Ordinal);
        AssertNoZoomChrome(body);
    }

    [Fact]
    public void Render_FullDocument_CarriesNoPanZoomChromeThroughTheSharedShell()
    {
        // The full shell inlines the bundled stylesheet, so this also pins that charter.css was not the
        // place the affordance was implemented — the exported artifact shares that stylesheet byte for byte.
        AssertNoZoomChrome(CharterRenderer.Render(DiagramMarkdown));
    }

    [Fact]
    public void Export_OfflineArtifact_StillRendersTheDiagramAndShipsNoPanZoomChrome()
    {
        var block = BlockDocument.Parse(DiagramMarkdown).Blocks[0];

        var exported = ArtifactExporter.Export(DiagramMarkdown, Path.GetTempPath());

        // Still a real diagram: the block, the vendored Mermaid runtime, and the init that renders it.
        Assert.Contains($"<pre class=\"mermaid\" id=\"{block.Id}\">", exported, StringComparison.Ordinal);
        Assert.Contains("mermaid.run()", exported, StringComparison.Ordinal);

        // …and none of the review-time affordance, nor the SDK that would provide it.
        Assert.DoesNotContain("data-charter-sdk", exported, StringComparison.Ordinal);
        AssertNoZoomChrome(exported);
    }

    private static void AssertNoZoomChrome(string html)
    {
        foreach (var marker in ZoomChrome)
        {
            Assert.DoesNotContain(marker, html, StringComparison.Ordinal);
        }
    }

    /// <summary>The <c>&lt;pre class="mermaid" …&gt;</c> opening tag, attributes included.</summary>
    private static string DiagramPreTag(string body)
    {
        var match = System.Text.RegularExpressions.Regex.Match(body, "<pre class=\"mermaid\"[^>]*>");
        Assert.True(match.Success, "the renderer emitted no pre.mermaid block");
        return match.Value;
    }
}
