using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #184 — a <c>:::diagram</c> that is not a DIRECT CHILD of the document must still draw.
///
/// <para><c>RenderBody</c>'s <c>hasDiagram</c> was raised by the same top-level-only walk as the anchor pass
/// (<c>foreach (var node in document)</c>), so a diagram nested inside a <c>::::note</c> — or inside a list
/// item — never set it. The Mermaid runtime was therefore not inlined and the block rendered as its own
/// source text. #166 had already made that block ANNOTATABLE, so a reviewer could take a note on a diagram
/// that does not draw.</para>
///
/// <para><b>Every fixture here is deliberately free of a top-level diagram.</b> With one present the runtime
/// is inlined for THAT block and the nested one draws by coincidence — which is what made the defect read as
/// intermittent, and what would make a careless fixture pass without the fix.</para>
///
/// <para><b>Invariant 1 is not weakened; it is the point.</b> The EXPORTED artifact now carries the runtime
/// for a nested diagram, exactly as it always has for a top-level one — a saved plan whose diagram is a
/// picture in the review page and source text in the file was the bug. Nothing review-only is added: the
/// runtime is the same offline library and bootstrap <c>render</c> and <c>export</c> already share.</para>
///
/// <para><b>Invariant 2 is untouched, and asserted.</b> The flag is now raised by the container renderer
/// itself, at the moment it writes a <c>&lt;pre class="mermaid"&gt;</c>, so the anchor pass is not widened
/// and a nested diagram still renders with NO id of its own — which is settled behaviour (#166: a note on it
/// resolves outward to the enclosing block), not something a real rendering changes.</para>
///
/// <para><b>And #177 is not re-widened.</b> A forged <c>&lt;pre class="mermaid"&gt;</c> inside
/// <c>:::custom-html</c> is markup the author asked for verbatim; the renderer never calls
/// <c>WriteDiagram</c> for it, so it raises nothing — the last test here is that control.</para>
/// </summary>
[Trait("Category", "NestedDiagramRuntime")]
public class NestedDiagramRuntimeTests
{
    /// <summary>The distinctive interior marker of the vendored minified library — present iff its BYTES are
    /// inlined. A bootstrap without the library would leave a saved diagram blank with no network.</summary>
    private const string LibraryMarker = "__esbuild_esm_mermaid_nm";

    /// <summary>A <c>:::diagram</c> one level down, inside the four-colon callout that contains it. No
    /// top-level diagram anywhere, so nothing else can inline the runtime on its behalf.</summary>
    private const string DiagramInsideNote =
        "# A note that explains itself with a picture\n" +
        "\n" +
        "Prose before the callout, and the only other block in this plan.\n" +
        "\n" +
        "::::note\n" +
        "This design has one moving part, and here it is.\n" +
        "\n" +
        ":::diagram\n" +
        "graph TD; Ingress-->Auth;\n" +
        ":::\n" +
        "::::\n";

    /// <summary>The other nesting the issue names: a diagram indented under a list item.</summary>
    private const string DiagramInsideListItem =
        "# A step that draws what it means\n" +
        "\n" +
        "- a plain first step\n" +
        "- a step that draws what it means\n" +
        "\n" +
        "  :::diagram\n" +
        "  graph TD; Plan-->Build;\n" +
        "  :::\n";

    /// <summary>
    /// The #177 control: markup that merely LOOKS like a diagram, inside the one sanctioned raw-HTML escape
    /// hatch. The renderer emits it verbatim and never writes a diagram, so no runtime is inlined for it —
    /// and the bootstrap's containment filter would refuse to rewrite it even if one were.
    /// </summary>
    private const string ForgedDiagramInsideCustomHtml =
        "# A plan that documents Mermaid\n" +
        "\n" +
        ":::custom-html\n" +
        "<pre class=\"mermaid\" id=\"forged\">graph TD; Forged--&gt;Node;</pre>\n" +
        ":::\n";

    [Theory]
    [InlineData(DiagramInsideNote)]
    [InlineData(DiagramInsideListItem)]
    public void Render_NestedDiagram_InlinesTheMermaidRuntime(string plan)
    {
        var body = CharterRenderer.RenderBody(plan);

        // The block itself has always rendered; what was missing is the runtime that turns it into a picture.
        Assert.Contains("<pre class=\"mermaid\"", body, StringComparison.Ordinal);
        Assert.Contains(LibraryMarker, body, StringComparison.Ordinal);
        Assert.Contains("mermaid.initialize", body, StringComparison.Ordinal);
        Assert.Contains("mermaid.run(", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DiagramInsideNote)]
    [InlineData(DiagramInsideListItem)]
    public void Export_NestedDiagram_SavedArtifactCarriesTheRuntimeToo(string plan)
    {
        // Invariant 1 stated explicitly: the artifact GAINS the runtime for a nested diagram. That is the
        // fix, not a leak — the saved file must draw the same picture the served page does, offline.
        var artifact = ArtifactExporter.Export(plan, Path.GetTempPath());

        Assert.Contains("<pre class=\"mermaid\"", artifact, StringComparison.Ordinal);
        Assert.Contains(LibraryMarker, artifact, StringComparison.Ordinal);
        Assert.Contains("mermaid.run(", artifact, StringComparison.Ordinal);

        // Offline, as ever: inlined bytes, never a CDN fetch.
        Assert.DoesNotMatch("src=\"https?://[^\"]*mermaid", artifact);

        // …and still no review-only scaffolding rode in with it.
        Assert.DoesNotContain("data-charter-sdk", artifact, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NestedDiagram_StillCarriesNoAnchorOfItsOwn()
    {
        // Invariant 2: giving the nested block a real RENDERING must not give it a real ANCHOR. The reachable
        // anchor set is unchanged — exactly the top-level blocks — and the nested <pre> carries no id, so a
        // note on it still resolves outward to the enclosing :::note (#166).
        var body = CharterRenderer.RenderBody(DiagramInsideNote);

        var open = body.IndexOf("<pre class=\"mermaid\"", StringComparison.Ordinal);
        Assert.True(open >= 0, "the nested :::diagram must still render its mermaid element.");
        var tag = body[open..(body.IndexOf('>', open) + 1)];
        Assert.DoesNotContain("id=", tag, StringComparison.Ordinal);

        var topLevelIds = BlockDocument.Parse(DiagramInsideNote).Blocks.Select(block => block.Id).ToHashSet();
        Assert.True(
            SourceMap.Build(DiagramInsideNote).Anchors.SetEquals(topLevelIds),
            "the anchor set must still be exactly the top-level blocks — the nested diagram gains none.");
    }

    [Fact]
    public void Render_ForgedMermaidInsideCustomHtml_InlinesNothing()
    {
        // Charter #177's half, held from the other side: the runtime is inlined for the diagrams CHARTER
        // wrote, and a name an author can type is not one of them. Widening the flag to "any pre.mermaid in
        // the output" — the tempting shape — would fail exactly here.
        var body = CharterRenderer.RenderBody(ForgedDiagramInsideCustomHtml);

        Assert.Contains("<pre class=\"mermaid\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain(LibraryMarker, body, StringComparison.Ordinal);
        Assert.DoesNotContain("mermaid.initialize", body, StringComparison.Ordinal);
    }
}
