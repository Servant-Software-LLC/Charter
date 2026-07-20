using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Golden-HTML tests for the <c>:::comparison</c> block. These are TDD "red": they compile against the
/// existing renderer surface plus <see cref="BlockKind.Comparison"/> and FAIL at runtime because a
/// <c>:::comparison</c> container still classifies to <see cref="BlockKind.Note"/> and renders no per-row
/// anchors. Task <c>06-implement-comparison-block</c> makes them pass.
///
/// The load-bearing new concern is PER-SUB-ELEMENT anchoring: the block catalog says <c>:::comparison</c>
/// is "annotatable per-row/option", so each option row carries its OWN content-derived stable sub-anchor
/// that the annotation SDK can bind to, and the <see cref="SourceMap"/> resolves each sub-anchor to that
/// row's own markdown line — the per-row half of the comment-in-place round-trip.
///
/// The sub-anchor contract these tests pin: a row's sub-anchor is
/// <c>Block.StableId(&lt;that row's own markdown source line, trimmed&gt;)</c> — the SAME text found at the
/// line <see cref="SourceMap.LineForAnchor(string)"/> resolves that sub-anchor to. Deriving it from the
/// row's own content (not its position) is what lets one row's annotation survive edits to another row.
/// Sub-anchors are asserted STRUCTURALLY — recomputed via <see cref="Block.StableId(string)"/> in the test
/// the same way — never as a hard-coded hash, so these tests survive any hash choice.
/// </summary>
[Trait("Category","ComparisonBlock")]
public class ComparisonBlockTests
{
    // A small comparison document: a :::comparison container wrapping two option rows, each on its own
    // source line so it has its own content-derived sub-anchor and its own resolvable source line.
    private const string OptionAlpha = "- Option Alpha: fast to ship, higher cost";
    private const string OptionBeta = "- Option Beta: lower cost, slower to ship";

    private const string Markdown =
        ":::comparison\n" +
        OptionAlpha + "\n" +
        OptionBeta + "\n" +
        ":::";

    [Fact]
    public void Parse_ComparisonContainer_ClassifiesAsComparison()
    {
        // Invariant 1 (RED today): the classifier still maps every non-warn ::: container to Note; task 06
        // recognizes the "comparison" info string and this resolves to BlockKind.Comparison.
        var block = BlockDocument.Parse(Markdown).Blocks[0];

        Assert.Equal(BlockKind.Comparison, block.Kind);
    }

    [Fact]
    public void Render_ComparisonBlock_RootCarriesBlockLevelStableId()
    {
        // Invariant 2: like every other block, the comparison's root element carries the block's own
        // content-derived stable id (asserted against Block.Id, never a hard-coded hash).
        var block = BlockDocument.Parse(Markdown).Blocks[0];
        var html = CharterRenderer.Render(Markdown);

        Assert.Contains($"id=\"{block.Id}\"", html);
    }

    [Fact]
    public void Render_ComparisonRows_CarryDistinctContentDerivedSubAnchors()
    {
        // Invariant 3 (the load-bearing one, RED today): each option row carries its OWN stable sub-anchor
        // the SDK can bind to, derived from that row's own content. Recomputed here the same way the
        // renderer must derive it — Block.StableId of the row's markdown source line — so it is asserted
        // structurally, never as a hard-coded hash.
        var alphaSubId = Block.StableId(OptionAlpha);
        var betaSubId = Block.StableId(OptionBeta);

        var html = CharterRenderer.Render(Markdown);

        // Each row exposes its own sub-anchor. (Rows render without any per-row anchor today.)
        Assert.Contains($"data-anchor=\"{alphaSubId}\"", html);
        Assert.Contains($"data-anchor=\"{betaSubId}\"", html);

        // Two distinct rows => two DISTINCT sub-anchors: one row's annotation can never bind to another,
        // and each is derived from that row's own content, so it survives edits to the other row.
        Assert.NotEqual(alphaSubId, betaSubId);
    }

    [Fact]
    public void SourceMap_ResolvesEachRowSubAnchorToItsOwnLine()
    {
        // Invariant 4 (RED today): the per-sub-element half of the round-trip. Each row's sub-anchor
        // resolves to THAT row's 1-based markdown line — not merely the block's start line — and the
        // block-level id still resolves to the container's start line.
        var block = BlockDocument.Parse(Markdown).Blocks[0];
        var alphaSubId = Block.StableId(OptionAlpha);
        var betaSubId = Block.StableId(OptionBeta);

        var map = SourceMap.Build(Markdown);

        // Block-level anchor still resolves — to the container's opening line.
        Assert.Equal(LineOf(Markdown, ":::comparison"), map.LineForAnchor(block.Id));

        // Sub-anchors resolve to their OWN rows' lines. (The map knows only block ids today, so these
        // return null until task 06 registers each sub-anchor at that row's source line.)
        Assert.Equal(LineOf(Markdown, "Option Alpha"), map.LineForAnchor(alphaSubId));
        Assert.Equal(LineOf(Markdown, "Option Beta"), map.LineForAnchor(betaSubId));

        // The two rows resolve to DIFFERENT lines — proof it is per-row, not the block's single start line.
        Assert.NotEqual(map.LineForAnchor(alphaSubId), map.LineForAnchor(betaSubId));
    }

    /// <summary>The 1-based markdown line number of the first line containing <paramref name="needle"/>.</summary>
    private static int LineOf(string markdown, string needle)
        => Array.FindIndex(markdown.Split('\n'), line => line.Contains(needle, StringComparison.Ordinal)) + 1;
}
