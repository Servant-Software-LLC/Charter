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

    /// <summary>
    /// The DOCUMENTED authoring form (Charter #40): a <c>:::diagram</c> container wrapping a fenced
    /// <c>```mermaid</c> code block. The renderer must emit ONLY the Mermaid source — the fenced block's ```
    /// markers and its <c>mermaid</c> info string are Markdig syntax, not diagram source, and Mermaid chokes on
    /// a leading "```mermaid" line ("No diagram type detected").
    /// </summary>
    private const string FencedDiagramMarkdown =
        ":::diagram\n" +
        "```mermaid\n" +
        "flowchart TD\n" +
        "    A[\"x\"] --> B\n" +
        "```\n" +
        ":::";

    /// <summary>A fenced diagram whose source uses only HTML-inert characters, so the emitted element text is an
    /// exact (escape-free) match against the authored Mermaid source.</summary>
    private const string FencedPlainDiagramMarkdown =
        ":::diagram\n" +
        "```mermaid\n" +
        "flowchart TD\n" +
        "    Alpha\n" +
        "    Beta\n" +
        "```\n" +
        ":::";

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
    public void Render_FencedMermaidDiagram_EmitsSourceWithoutFenceMarkersOrInfoString()
    {
        // Charter #40: the documented ```mermaid form must render its SOURCE into the mermaid element — never
        // the ``` fence markers nor the `mermaid` info-string line, which broke Mermaid ("Syntax error").
        var block = BlockDocument.Parse(FencedDiagramMarkdown).Blocks[0];
        var html = CharterRenderer.Render(FencedDiagramMarkdown);

        Assert.Equal(BlockKind.Diagram, block.Kind);

        // The stable id (the diagram-node annotation anchor) still rides the <pre class="mermaid"> root.
        Assert.Contains($"<pre class=\"mermaid\" id=\"{block.Id}\">", html);

        var inner = MermaidPreInner(html);

        // No fence markers and no `mermaid` info-string line survive into the element text — the exact bug.
        Assert.DoesNotContain("```", inner);
        Assert.DoesNotContain("mermaid", inner);

        // The actual Mermaid source IS present (the arrow survives raw or HTML-escaped, as Mermaid reads textContent).
        Assert.Contains("flowchart TD", inner);
        Assert.True(
            inner.Contains("A[\"x\"] --> B") || inner.Contains("A[&quot;x&quot;] --&gt; B"),
            "the fenced Mermaid source must survive into the element, raw or HTML-escaped.");
    }

    [Fact]
    public void Render_FencedMermaidDiagram_InnerTextIsExactlyTheAuthoredSource()
    {
        // The stronger form: with HTML-inert source, the mermaid element's text equals the authored Mermaid
        // source EXACTLY — no ``` opener, no info string, no ``` closer.
        var html = CharterRenderer.Render(FencedPlainDiagramMarkdown);

        var inner = MermaidPreInner(html).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

        Assert.Equal("flowchart TD\n    Alpha\n    Beta", inner);
    }

    [Fact]
    public void Render_RawFenceLessDiagram_StillEmitsItsSource()
    {
        // The fence-less form (raw Mermaid as the container body) is STILL accepted — the renderer emits its
        // source verbatim, so both authoring forms work.
        var html = CharterRenderer.Render(DiagramMarkdown);

        var inner = MermaidPreInner(html);
        Assert.Contains("graph TD", inner);
        Assert.DoesNotContain("```", inner);
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

    /// <summary>
    /// The text INSIDE the rendered <c>&lt;pre class="mermaid" …&gt;…&lt;/pre&gt;</c> element — what Mermaid
    /// reads as the diagram source. Extracted between the open tag's <c>&gt;</c> and the closing
    /// <c>&lt;/pre&gt;</c> so assertions target the Mermaid source alone, not the whole (Mermaid-library-laden)
    /// document.
    /// </summary>
    private static string MermaidPreInner(string html)
    {
        const string open = "<pre class=\"mermaid\"";
        var start = html.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, "no <pre class=\"mermaid\"> element was rendered.");

        var gt = html.IndexOf('>', start);
        Assert.True(gt >= 0, "the <pre class=\"mermaid\"> open tag was not terminated.");

        var end = html.IndexOf("</pre>", gt, StringComparison.Ordinal);
        Assert.True(end >= 0, "the <pre class=\"mermaid\"> element was not closed.");

        return html.Substring(gt + 1, end - (gt + 1));
    }
}
