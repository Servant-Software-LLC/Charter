using System.Text.RegularExpressions;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #68 — a wide table must be REACHABLE, not clipped. The renderer emits every pipe table inside a
/// <c>&lt;div class="table-scroll"&gt;</c> scroll container and the stylesheet puts <c>overflow-x</c> on THAT
/// wrapper, instead of on the <c>&lt;table&gt;</c> element itself — a shape that left the hidden columns
/// unreachable in practice (Chromium draws that scrollbar as a fading overlay, so nothing signalled the
/// content could move, and a <c>&lt;table&gt;</c> takes no tab stop).
///
/// The load-bearing risk of introducing a wrapper is ANCHORING: the annotation SDK walks up from the clicked
/// node to the nearest ancestor carrying an <c>id</c>/<c>data-anchor</c>, so a wrapper that stole the id — or
/// carried one of its own — would silently re-target every note on a table. These tests pin that the wrapper
/// is anchor-INVISIBLE: the stable id stays on the <c>&lt;table&gt;</c>, the wrapper carries no <c>id</c> and
/// no <c>data-anchor</c>, sub-anchors are untouched, and the <see cref="SourceMap"/> round-trip is unchanged.
///
/// Markup is asserted against <see cref="CharterRenderer.RenderBody(string)"/> — the rendered BLOCKS — rather
/// than the whole document, because the shell inlines the bundled stylesheet and that stylesheet names
/// <c>.table-scroll</c> too; counting or excluding a marker over the full document would be measuring the CSS.
/// One test below goes through the public <see cref="CharterRenderer.Render(string)"/> to prove the wrapper
/// survives the shell.
///
/// The browser half — that the container GENUINELY scrolls under a real <c>mouse.wheel</c> at a narrow
/// viewport, and that it shows a persistent scrollbar — lives in
/// <c>Charter.Browser.Tests.ReviewLoopBrowserTests</c>; a C#-string test cannot see any of it.
/// </summary>
[Trait("Category", "TableOverflow")]
public class TableScrollContainerTests
{
    private const string TableMarkdown =
        "| Source | Symbol |\n" +
        "| --- | --- |\n" +
        "| `src_Charter_Core_CharterRenderer_cs` | `RenderBody_string` |";

    /// <summary>The wrapper opening tag immediately followed by the table's own opening tag.</summary>
    private static Regex WrapperThenTable(string tableId)
        => new("<div class=\"table-scroll\"[^>]*>\\s*<table id=\"" + Regex.Escape(tableId) + "\">");

    [Fact]
    public void Render_Table_IsEmittedInsideAScrollContainer()
    {
        var block = BlockDocument.Parse(TableMarkdown).Blocks[0];
        var body = CharterRenderer.RenderBody(TableMarkdown);

        Assert.Equal(BlockKind.Table, block.Kind);

        // The wrapper opens immediately before the table and closes immediately after it: the scroll box is
        // the table's own box, never a region that swallows neighbouring blocks.
        Assert.Matches(WrapperThenTable(block.Id), body);
        Assert.Matches(new Regex("</table>\\s*</div>"), body);
    }

    [Fact]
    public void Render_FullDocument_CarriesTheScrollContainerThroughTheSharedShell()
    {
        var block = BlockDocument.Parse(TableMarkdown).Blocks[0];

        // The public surface every consumer sees — render/review/export all wrap this same body — still
        // carries the container, and the bundled stylesheet that styles it.
        var html = CharterRenderer.Render(TableMarkdown);

        Assert.Matches(WrapperThenTable(block.Id), html);
        Assert.Contains(".table-scroll", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_OfflineArtifact_ShipsTheContainerAndTheRuleThatScrollsIt()
    {
        var block = BlockDocument.Parse(TableMarkdown).Blocks[0];

        var exported = ArtifactExporter.Export(TableMarkdown, Path.GetTempPath());

        // The saved artifact carries no SDK (invariant 1), so scrolling a wide table must be pure markup +
        // CSS or it silently only works while the review server is running. Both halves ship inline.
        Assert.Matches(WrapperThenTable(block.Id), exported);
        Assert.Contains(".table-scroll {", exported, StringComparison.Ordinal);
        Assert.Contains("overflow-x: auto;", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("data-charter-sdk", exported, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Table_KeepsItsStableIdOnTheTableNotTheWrapper()
    {
        var block = BlockDocument.Parse(TableMarkdown).Blocks[0];
        var body = CharterRenderer.RenderBody(TableMarkdown);

        // The anchored element is still the <table>. This is the exact assertion RendererGoldenTests makes,
        // restated here because the wrapper is the thing that could break it.
        Assert.Contains($"<table id=\"{block.Id}\">", body);

        // And the wrapper is ANCHOR-INVISIBLE: no id, no data-anchor, no data-charter-anchor. The SDK's
        // closestAnchored() walk therefore passes straight through it to the table.
        var wrapper = WrapperTag(body);
        Assert.DoesNotContain("id=\"", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("data-anchor", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("data-charter-anchor", wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_TableScrollContainer_IsKeyboardReachableAndLabelled()
    {
        var wrapper = WrapperTag(CharterRenderer.RenderBody(TableMarkdown));

        // A scroll container only a mouse can reach hides the clipped columns from keyboard-only reviewers
        // just as effectively as clipping did. tabindex="0" puts it in the tab order; the role + accessible
        // name are what make that stop announce as something rather than as nothing.
        Assert.Contains("tabindex=\"0\"", wrapper, StringComparison.Ordinal);
        Assert.Contains("role=\"region\"", wrapper, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Table\"", wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Table_SourceMapRoundTripIsUnchanged()
    {
        var block = BlockDocument.Parse(TableMarkdown).Blocks[0];
        var map = SourceMap.Build(TableMarkdown);

        // The wrapper is a RENDER-only element: the anchor→markdown-line map never sees it, so an annotation
        // on the table still resolves to the table's first markdown line.
        Assert.Equal(1, map.LineForAnchor(block.Id));
    }

    [Fact]
    public void Render_ComparisonWithRows_KeepsItsIdAndEveryRowSubAnchor()
    {
        const string optionAlpha = "- Option Alpha: fast to ship, higher cost";
        const string optionBeta = "- Option Beta: lower cost, slower to ship";
        const string markdown = ":::comparison\n" + optionAlpha + "\n" + optionBeta + "\n:::";

        var block = BlockDocument.Parse(markdown).Blocks[0];
        var body = CharterRenderer.RenderBody(markdown);

        // A :::comparison that renders as a LIST has no table to wrap, so its markup must be untouched: the
        // container keeps its block id and each row keeps its own content-derived sub-anchor.
        Assert.Contains($"id=\"{block.Id}\" class=\"comparison\"", body);
        Assert.Contains($"data-anchor=\"{Block.StableId(optionAlpha)}\"", body);
        Assert.Contains($"data-anchor=\"{Block.StableId(optionBeta)}\"", body);
        Assert.DoesNotContain("table-scroll", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ComparisonWithTable_WrapsTheInnerTableWithoutDisturbingTheBlockAnchor()
    {
        const string markdown =
            ":::comparison\n" +
            "| Option | Cost |\n" +
            "| --- | --- |\n" +
            "| Alpha | High |\n" +
            "| Beta | Low |\n" +
            ":::";

        var block = BlockDocument.Parse(markdown).Blocks[0];
        var body = CharterRenderer.RenderBody(markdown);

        Assert.Equal(BlockKind.Comparison, block.Kind);

        // The comparison card is still the anchored element…
        Assert.Contains($"id=\"{block.Id}\" class=\"comparison\"", body);

        // …and the inner table scrolls inside its own wrapper. That table is NOT a top-level node, so it
        // carries no id of its own — meaning the wrapper must not introduce one either, or a click inside the
        // card would stop resolving to the card.
        Assert.Matches(new Regex("<div class=\"table-scroll\"[^>]*>\\s*<table>"), body);
        Assert.DoesNotContain("id=\"\"", body, StringComparison.Ordinal);

        // Exactly one anchored element in the card: the card itself. (The wrapper adding an id would make two.)
        Assert.Single(Regex.Matches(body, "id=\"" + Regex.Escape(block.Id) + "\""));
    }

    [Fact]
    public void Render_NarrowTable_IsNotDecoratedBeyondTheWrapper()
    {
        const string markdown =
            "| A | B |\n" +
            "| --- | --- |\n" +
            "| 1 | 2 |";

        var block = BlockDocument.Parse(markdown).Blocks[0];
        var body = CharterRenderer.RenderBody(markdown);

        // A table that fits must render exactly as it always did — the wrapper adds no class, no style and no
        // attribute to the table, and the cells are untouched. (Whether it stays visually unharmed at a real
        // viewport is asserted in the browser test, which measures it.)
        Assert.Contains($"<table id=\"{block.Id}\">", body);
        Assert.Contains("<th>A</th>", body);
        Assert.Contains("<td>1</td>", body);
        Assert.Single(Regex.Matches(body, "table-scroll"));
    }

    [Fact]
    public void Render_TwoTables_EachGetsItsOwnContainer()
    {
        const string markdown =
            "| A | B |\n| --- | --- |\n| 1 | 2 |\n\n" +
            "Prose between them.\n\n" +
            "| C | D |\n| --- | --- |\n| 3 | 4 |";

        var body = CharterRenderer.RenderBody(markdown);

        // Two tables, two independent scroll boxes — never one region spanning the prose between them.
        Assert.Equal(2, Regex.Matches(body, "<div class=\"table-scroll\"").Count);
        Assert.Equal(2, Regex.Matches(body, "<table id=\"").Count);
        Assert.Matches(new Regex("</table>\\s*</div>\\s*<p id=\"[^\"]+\">Prose between them\\.</p>"), body);
    }

    /// <summary>The first <c>&lt;div class="table-scroll" …&gt;</c> opening tag, attributes included.</summary>
    private static string WrapperTag(string body)
    {
        var match = Regex.Match(body, "<div class=\"table-scroll\"[^>]*>");
        Assert.True(match.Success, "the renderer emitted no .table-scroll container:\n" + body);
        return match.Value;
    }
}
