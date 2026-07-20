using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Golden-HTML tests for the <c>:::diff</c> block (<c>[Trait("Category","DiffBlock")]</c>). A diff block is
/// a container wrapping a few unified-diff lines (prefixed <c>+</c>/<c>-</c>/space). Like
/// <c>:::comparison</c>, it is annotatable per SUB-ELEMENT — here, per LINE. The load-bearing invariant:
/// every diff line carries its OWN content-derived stable sub-anchor (<see cref="Block.StableId(string)"/>
/// of that line's raw text, marker included), so a reviewer's note on one line survives edits to the other
/// lines, and the <see cref="SourceMap"/> resolves each line's sub-anchor back to that line's markdown
/// source line — the per-sub-element half of the comment-in-place round-trip.
///
/// TDD "red" without stubs: these compile against the existing renderer surface plus
/// <see cref="BlockKind.Diff"/> (added by task 01) and FAIL at runtime because <c>:::diff</c> still
/// classifies to <see cref="BlockKind.Note"/> and no per-line sub-anchors are emitted. Task
/// <c>08-implement-diff-block</c> makes them pass.
/// </summary>
[Trait("Category", "DiffBlock")]
public class DiffBlockTests
{
    // A diff line's raw text INCLUDES its +/- (or space) marker — the marker is content, so an added line
    // and a removed line hash to distinct sub-anchors. The renderer derives each line's sub-anchor the same
    // way: Block.StableId(<that line's raw source text>). These constants are the single source of truth for
    // both the document and the expected sub-anchors, so the assertions stay structural (never a pinned hash).
    private const string AddedLine = "+added apple pie";
    private const string RemovedLine = "-removed banana bread";
    private const string ContextLine = " context stays the same";

    // A small :::diff document: the container (Blocks[0]) wrapping three diff lines.
    //   line 1: :::diff
    //   line 2: +added apple pie
    //   line 3: -removed banana bread
    //   line 4:  context stays the same
    //   line 5: :::
    private const string DiffMarkdown =
        ":::diff\n" +
        AddedLine + "\n" +
        RemovedLine + "\n" +
        ContextLine + "\n" +
        ":::";

    [Fact]
    public void Diff_Container_ClassifiesAsDiff()
    {
        var block = BlockDocument.Parse(DiffMarkdown).Blocks[0];

        // RED today: a :::diff container still classifies to Note; task 08 adds the Diff classifier case.
        Assert.Equal(BlockKind.Diff, block.Kind);
    }

    [Fact]
    public void Render_Diff_RootCarriesBlockStableId()
    {
        var block = BlockDocument.Parse(DiffMarkdown).Blocks[0];
        var html = CharterRenderer.Render(DiffMarkdown);

        // The block root carries the content-derived stable id every block anchors to — asserted against
        // Block.Id, never a hard-coded hash, so any stable hash the renderer and block model agree on works.
        Assert.Contains($"id=\"{block.Id}\"", html);
    }

    [Fact]
    public void Render_Diff_EachLineCarriesDistinctContentDerivedSubAnchor()
    {
        var block = BlockDocument.Parse(DiffMarkdown).Blocks[0];
        var html = CharterRenderer.Render(DiffMarkdown);

        // Each line's sub-anchor is derived from THAT line's own raw text — computed here exactly as the
        // renderer must, so this is asserted structurally rather than against a pinned hash.
        var addedSubId = Block.StableId(AddedLine);
        var removedSubId = Block.StableId(RemovedLine);

        // Distinct line content => distinct sub-anchors, and each is the LINE's own anchor, not the block's.
        Assert.NotEqual(addedSubId, removedSubId);
        Assert.NotEqual(block.Id, addedSubId);
        Assert.NotEqual(block.Id, removedSubId);

        // RED today: the fallback note rendering emits neither per-line data-anchor sub-anchors nor add/del
        // classes. Each diff LINE must carry its own stable sub-anchor the SDK can bind to (data-anchor)...
        Assert.Contains($"data-anchor=\"{addedSubId}\"", html);
        Assert.Contains($"data-anchor=\"{removedSubId}\"", html);

        // ...and added vs. removed lines must be distinguishable in the markup.
        Assert.Contains("diff-add", html);
        Assert.Contains("diff-del", html);
    }

    [Fact]
    public void SourceMap_ResolvesPerLineSubAnchorToItsSourceLine()
    {
        var map = SourceMap.Build(DiffMarkdown);

        var addedSubId = Block.StableId(AddedLine);
        var removedSubId = Block.StableId(RemovedLine);

        // The per-sub-element half of the round-trip: each line's sub-anchor resolves to THAT line's 1-based
        // markdown line (not merely the block's start line). RED today — Build registers only the block-level
        // anchor, so LineForAnchor(lineSubId) returns null and these equalities fail.
        Assert.Equal(LineOf(DiffMarkdown, AddedLine), map.LineForAnchor(addedSubId));
        Assert.Equal(LineOf(DiffMarkdown, RemovedLine), map.LineForAnchor(removedSubId));

        // The block-level anchor still resolves too — block and per-line sub-anchors coexist in one map.
        var blockId = BlockDocument.Parse(DiffMarkdown).Blocks[0].Id;
        Assert.Equal(LineOf(DiffMarkdown, ":::diff"), map.LineForAnchor(blockId));
    }

    /// <summary>The 1-based markdown line number of the first line containing <paramref name="needle"/>.</summary>
    private static int LineOf(string markdown, string needle)
        => Array.FindIndex(markdown.Split('\n'), line => line.Contains(needle, StringComparison.Ordinal)) + 1;
}
