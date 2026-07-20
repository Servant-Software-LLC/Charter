using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Golden-HTML tests for the <c>:::diagram</c> block (TDD red, no stubs). These compile against the
/// existing renderer surface (<see cref="BlockDocument.Parse(string)"/>, <see cref="CharterRenderer.Render(string)"/>,
/// <see cref="SourceMap.Build(string)"/>, and the <see cref="BlockKind.Diagram"/> member added by
/// <c>01-add-block-kinds</c>) and FAIL at runtime: today a <c>:::diagram</c> container still classifies to
/// <see cref="BlockKind.Note"/> and renders as <c>&lt;div class="note"&gt;</c>. Task <c>04-implement-diagram-block</c>
/// makes them pass by classifying the container as a diagram, rendering a Mermaid element that carries the
/// block's content-derived stable id, and inlining the vendored offline Mermaid runtime.
/// </summary>
[Trait("Category","DiagramBlock")]
public class DiagramBlockTests
{
    /// <summary>
    /// A minimal Charter document whose only block is a <c>:::diagram</c> container wrapping a Mermaid graph.
    /// The diagram is the first (and only) block, so it must classify as <see cref="BlockKind.Diagram"/> and
    /// anchor at markdown line 1.
    /// </summary>
    private const string DiagramMarkdown =
        ":::diagram\n" +
        "graph TD\n" +
        "A-->B\n" +
        ":::";

    /// <summary>A diagram-free document, used to prove the Mermaid runtime is inlined only when needed.</summary>
    private const string ProseMarkdown = "Just a plain paragraph, no diagram anywhere.";

    [Fact]
    public void Parse_DiagramContainer_ClassifiesAsDiagram()
    {
        var block = BlockDocument.Parse(DiagramMarkdown).Blocks[0];

        // RED until task 04's classifier lands: a :::diagram container still classifies to Note today.
        Assert.Equal(BlockKind.Diagram, block.Kind);
    }

    [Fact]
    public void Render_DiagramContainer_EmitsMermaidElementWithStableId()
    {
        var block = BlockDocument.Parse(DiagramMarkdown).Blocks[0];
        var html = CharterRenderer.Render(DiagramMarkdown);

        // The diagram renders as a Mermaid element (a <pre class="mermaid"> block root), NOT a note callout.
        Assert.Contains("<pre", html);
        Assert.Contains("class=\"mermaid\"", html);
        Assert.DoesNotContain("class=\"note\"", html);

        // The Mermaid element carries the block's content-derived stable id — the diagram-node anchor the
        // SDK's diagram-node annotations bind to — asserted against block.Id exactly as RendererGoldenTests.
        // (Individual node identity within the graph is assigned client-side by Mermaid.)
        Assert.Contains($"id=\"{block.Id}\"", html);

        // The Mermaid source text must survive into the element so the client library can render it. The
        // arrow may be emitted raw or HTML-escaped; either survives to Mermaid, which reads textContent.
        Assert.Contains("graph TD", html);
        Assert.True(
            html.Contains("A-->B") || html.Contains("A--&gt;B"),
            "Mermaid arrow source (A-->B) must survive into the mermaid element, raw or HTML-escaped.");
    }

    [Fact]
    public void Render_DocumentWithDiagram_InlinesVendoredMermaidRuntimeWithThemeAwareInit()
    {
        var html = CharterRenderer.Render(DiagramMarkdown);

        // A theme-aware bootstrap: a mermaid.initialize(...) config call plus a theme setting token.
        Assert.Contains("mermaid.initialize", html);
        Assert.Contains("theme", html);

        // Load-bearing (invariant 1 — offline portability): the ~3.5 MB vendored Mermaid library BYTES must
        // be inlined into the artifact, proven by a distinctive interior marker of the minified library
        // (present in src/Charter.Core/assets/mermaid.min.js). Emitting only the mermaid.initialize / run
        // call WITHOUT the library bytes is a FAIL — a saved :::diagram would not render with no network.
        Assert.Contains("__esbuild_esm_mermaid_nm", html);

        // Offline: the runtime is inlined, never pulled from a CDN <script src="http...mermaid">.
        Assert.DoesNotMatch("src=\"https?://[^\"]*mermaid", html);
    }

    [Fact]
    public void Render_DocumentWithoutDiagram_DoesNotInlineMermaidRuntime()
    {
        var html = CharterRenderer.Render(ProseMarkdown);

        // The runtime is emitted ONLY when a diagram is present, so a diagram-free document stays lean.
        Assert.DoesNotContain("__esbuild_esm_mermaid_nm", html);
        Assert.DoesNotContain("mermaid.initialize", html);
    }

    [Fact]
    public void SourceMap_ResolvesDiagramBlockToItsStartLine()
    {
        var block = BlockDocument.Parse(DiagramMarkdown).Blocks[0];
        var map = SourceMap.Build(DiagramMarkdown);

        // The content-derived diagram anchor round-trips back to its 1-based markdown start line: the
        // :::diagram fence is line 1 of the document.
        Assert.Equal(1, map.LineForAnchor(block.Id));
    }
}
