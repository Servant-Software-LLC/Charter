using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #164, part 1 — golden renderer tests for PER-<c>&lt;li&gt;</c> sub-anchors on a plain top-level list.
///
/// <para><b>Why the renderer half exists at all.</b> The annotation SDK withheld the note-count badge from
/// <c>UL</c>/<c>OL</c> because a <c>&lt;button&gt;</c> is not a legal child of a list, so a reviewer's note on a
/// list was the one note with no visible marker. <c>LI</c> was never on that deny-list, so giving each list item
/// its own anchor gives list feedback a badge with no SDK change at all — and gives it a BETTER anchor than the
/// whole list ever was, because a note now names the row it is about and survives edits to the other rows
/// (invariant 2).</para>
///
/// <para><b>The contract is the one <c>:::comparison</c> already established</b>, deliberately reused rather
/// than invented: a row's sub-anchor is <see cref="Block.StableId(string)"/> of that row's OWN trimmed markdown
/// source line, exposed as <c>data-anchor</c> on the <c>&lt;li&gt;</c>, and registered in the
/// <see cref="SourceMap"/> against that row's own 1-based line. Every sub-anchor here is RECOMPUTED the way the
/// renderer must derive it — never pinned as a literal hash — so these tests survive any hash choice while still
/// proving the renderer, the block model and the source map agree on one anchor per row.</para>
///
/// <para><b>The orphan consequence is deliberate and is asserted as a SAFETY property, not as a shape.</b>
/// Charter's governing anchor rule is that a wrong attribution is unrecoverable and invisible while an orphan is
/// visible and carries its <c>quote</c>, so an upgrade that orphans pre-existing whole-list notes is acceptable
/// and an upgrade that silently re-points one onto a single row is not.
/// <see cref="Anchors_A_whole_list_anchor_never_resolves_to_a_single_rows_line"/> is that rule, written so it
/// holds under EITHER implementation choice — the list keeping its block-level anchor, or losing it — because
/// the choice is the architect's and the danger is the same either way.</para>
/// </summary>
[Trait("Category", "CoreRenderer")]
public class ListItemSubAnchorTests
{
    // Each item on its own source line, so each has its own line to resolve to and its own content to hash.
    // Deliberately NOT inside any ::: container: the rule under test is scoped to PLAIN TOP-LEVEL lists, and
    // Nested_list_inside_a_note_gains_no_sub_anchors is the negative control that proves that scoping.
    private const string FirstItem = "- Keep the loopback review server exactly as it is today";
    private const string SecondItem = "- Replace it, and pay for the migration from the feature budget";
    private const string ThirdItem = "- Defer the decision until the write path is proven";

    private const string BulletList = FirstItem + "\n" + SecondItem + "\n" + ThirdItem;

    private const string FirstOrdered = "1. Draft the plan";
    private const string SecondOrdered = "2. Review the plan";
    private const string OrderedList = FirstOrdered + "\n" + SecondOrdered;

    /// <summary>The sub-anchor a row MUST carry, derived the way the renderer has to derive it.</summary>
    private static string SubAnchor(string sourceLine) => Block.StableId(sourceLine.Trim());

    [Fact]
    public void Render_PlainBulletList_StampsEachItemWithItsOwnContentDerivedSubAnchor()
    {
        var block = BlockDocument.Parse(BulletList).Blocks[0];
        var html = CharterRenderer.RenderBody(BulletList);

        Assert.Equal(BlockKind.List, block.Kind);

        // The list itself is unchanged: it is still the block, and it still carries the block's own id.
        Assert.Contains($"<ul id=\"{block.Id}\">", html, StringComparison.Ordinal);

        // ...and every row now carries its own anchor, the :::comparison shape exactly.
        Assert.Contains($"<li data-anchor=\"{SubAnchor(FirstItem)}\">", html, StringComparison.Ordinal);
        Assert.Contains($"<li data-anchor=\"{SubAnchor(SecondItem)}\">", html, StringComparison.Ordinal);
        Assert.Contains($"<li data-anchor=\"{SubAnchor(ThirdItem)}\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_OrderedList_StampsEachItemToo()
    {
        // OL is on the SDK's badge deny-list beside UL, so an implementation that only taught UL about
        // sub-anchors would leave numbered lists — which is what a plan's step lists actually are — with the
        // original defect and a green suite.
        var block = BlockDocument.Parse(OrderedList).Blocks[0];
        var html = CharterRenderer.RenderBody(OrderedList);

        Assert.Equal(BlockKind.List, block.Kind);
        Assert.Contains($"id=\"{block.Id}\"", html, StringComparison.Ordinal);
        Assert.Contains($"<li data-anchor=\"{SubAnchor(FirstOrdered)}\">", html, StringComparison.Ordinal);
        Assert.Contains($"<li data-anchor=\"{SubAnchor(SecondOrdered)}\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_DistinctRows_GetDistinctSubAnchors()
    {
        // The whole point of a per-row anchor: one row's note can never bind to another row.
        var first = SubAnchor(FirstItem);
        var second = SubAnchor(SecondItem);
        var third = SubAnchor(ThirdItem);

        Assert.NotEqual(first, second);
        Assert.NotEqual(second, third);
        Assert.NotEqual(first, third);
    }

    [Fact]
    public void SourceMap_ResolvesEachItemsSubAnchorToThatItemsOwnLine()
    {
        // The round-trip half (invariant 2): the agent must be able to take a row's anchor and edit THAT row.
        var map = SourceMap.Build(BulletList);
        var lines = BulletList.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var anchor = SubAnchor(lines[i]);
            var line = map.LineForAnchor(anchor);

            Assert.True(line.HasValue, $"the source map does not resolve row {i + 1}'s sub-anchor '{anchor}'");

            // Not merely "some line" — the line whose TEXT is the row, which is the only thing that makes an
            // agent's edit land where the reviewer pointed.
            Assert.Equal(lines[i], lines[line!.Value - 1]);
            Assert.Equal(anchor, SubAnchor(lines[line.Value - 1]));
        }
    }

    [Fact]
    public void SourceMap_EditingOneRow_LeavesEveryOtherRowsAnchorByteIdentical()
    {
        // Content-derived, never positional. This is what stops a note on row 1 sliding onto row 2 when the
        // reviewer's agent rewrites row 2 — the misattribution class (#50) the whole anchor model exists to
        // avoid, restated for the sub-anchor.
        const string edited = FirstItem + "\n- Replace it, but stage the migration over two releases\n" + ThirdItem;

        var before = SourceMap.Build(BulletList);
        var after = SourceMap.Build(edited);

        Assert.Contains(SubAnchor(FirstItem), before.Anchors);
        Assert.Contains(SubAnchor(FirstItem), after.Anchors);
        Assert.Contains(SubAnchor(ThirdItem), before.Anchors);
        Assert.Contains(SubAnchor(ThirdItem), after.Anchors);

        // The edited row's OLD anchor is gone — it orphans, visibly, rather than rebinding to the new text.
        Assert.Contains(SubAnchor(SecondItem), before.Anchors);
        Assert.DoesNotContain(SubAnchor(SecondItem), after.Anchors);
    }

    /// <summary>
    /// The safety rule, written to hold whichever way the block-level list anchor goes.
    ///
    /// <para>Charter #164 accepts that pre-existing WHOLE-LIST notes orphan on upgrade. Orphaning is safe: the
    /// note is reported with <c>anchorStatus: "orphaned"</c> and its <c>quote</c>, and a human can see what
    /// happened. What is NOT acceptable — and is what this asserts — is the same anchor quietly resolving to
    /// ONE ROW's line, which would send the agent to edit a single bullet on the strength of a note written
    /// about the whole list, confidently and invisibly.</para>
    /// </summary>
    [Fact]
    public void Anchors_A_whole_list_anchor_never_resolves_to_a_single_rows_line()
    {
        var block = BlockDocument.Parse(BulletList).Blocks[0];
        var map = SourceMap.Build(BulletList);
        var lines = BulletList.Split('\n');

        var resolved = map.LineForAnchor(block.Id);

        if (resolved is null)
        {
            // The list-level anchor was dropped: every pre-existing whole-list note orphans. Approved.
            return;
        }

        // It still resolves — so it must resolve to the LIST's own start line (line 1 here), never to row 2
        // or row 3. Anything else is a silent re-target.
        Assert.Equal(1, resolved.Value);
        Assert.Equal(lines[0], lines[resolved.Value - 1]);

        // ...and it must not be one of the row sub-anchors wearing the block id's name.
        for (var i = 0; i < lines.Length; i++)
        {
            Assert.NotEqual(SubAnchor(lines[i]), block.Id);
        }
    }

    /// <summary>
    /// The collision this whole sub-anchor namespace exists to prevent, asserted INDEPENDENTLY of the anchor
    /// assignment that produces it.
    ///
    /// <para>Markdig gives a plain top-level <c>ListBlock</c> and its FIRST <c>ListItemBlock</c> the same
    /// <c>Line</c>, so a single line-keyed assignment lets the item's sub-anchor overwrite the list's block
    /// anchor — handing the <c>&lt;ul&gt;</c> and its first <c>&lt;li&gt;</c> one shared id. Two elements with
    /// one anchor is the misattribution case, not the orphan case: a note meant for the whole list and a note
    /// meant for its first bullet become indistinguishable, and the agent edits whichever the source map
    /// happens to answer with.</para>
    ///
    /// <para><b>Why it is asserted against a recomputed hash and not against <c>Block.Id</c>.</b> Every other
    /// test in this file reads the list's id through <see cref="BlockDocument"/>, which is fed by the same
    /// assignment the renderer uses — so under the collision BOTH would report the item's sub-anchor and every
    /// one of those tests would still pass. Deriving the expected block id here straight from
    /// <see cref="Block.StableId(string)"/> of the list's own source is the only reading that does not go
    /// through the machinery under test.</para>
    /// </summary>
    [Fact]
    public void A_list_and_its_first_item_never_share_one_anchor()
    {
        var block = BlockDocument.Parse(BulletList).Blocks[0];
        var html = CharterRenderer.RenderBody(BulletList);

        var listIdFromContent = Block.StableId(BulletList);
        var firstItemAnchor = SubAnchor(FirstItem);

        // They start on the same markdown line, which is what makes the collision possible at all...
        Assert.NotEqual(listIdFromContent, firstItemAnchor);

        // ...and the list still carries ITS OWN id, hashed from the whole list, not its first bullet's.
        Assert.Equal(listIdFromContent, block.Id);
        Assert.Contains($"<ul id=\"{listIdFromContent}\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"<ul id=\"{firstItemAnchor}\">", html, StringComparison.Ordinal);

        // Both resolve, to the same line — and that is FINE, because they are different anchors naming
        // different things. What would not be fine is one id meaning both, which the lines above rule out.
        var map = SourceMap.Build(BulletList);
        Assert.Contains(listIdFromContent, map.Anchors);
        Assert.Contains(firstItemAnchor, map.Anchors);
    }

    /// <summary>
    /// The negative control, and the anti-vacuity guard for the whole file: the rule is scoped to PLAIN
    /// TOP-LEVEL lists. A list nested inside a <c>:::note</c> is not a top-level node, so it gains nothing —
    /// its anchor is the callout, which is not on the badge deny-list and already badges correctly.
    ///
    /// <para>Without this, "stamp <c>data-anchor</c> on every <c>&lt;li&gt;</c> in the document" would satisfy
    /// every other test here while inventing anchors the source map never registered — sub-anchors that resolve
    /// to nothing, which is precisely the silent-orphan factory invariant 2 forbids.</para>
    /// </summary>
    [Fact]
    public void Nested_list_inside_a_note_gains_no_sub_anchors()
    {
        const string markdown =
            ":::note\n" +
            "The callout's own prose.\n\n" +
            "- a nested bullet\n" +
            "- another nested bullet\n" +
            ":::";

        var html = CharterRenderer.RenderBody(markdown);
        var map = SourceMap.Build(markdown);

        Assert.DoesNotContain("<li data-anchor=", html, StringComparison.Ordinal);
        Assert.DoesNotContain(SubAnchor("- a nested bullet"), map.Anchors);
        Assert.DoesNotContain(SubAnchor("- another nested bullet"), map.Anchors);
    }

    /// <summary>
    /// The other half of the same guard: every <c>data-anchor</c> the renderer emits is an anchor the source
    /// map can resolve. A renderer and a source map that disagree produce anchors that look real in the browser
    /// and orphan at drain time — the failure is invisible until an agent is already editing the wrong thing.
    /// </summary>
    [Fact]
    public void Every_emitted_sub_anchor_is_registered_in_the_source_map()
    {
        const string markdown =
            "# A plan\n\n" +
            BulletList + "\n\n" +
            OrderedList + "\n\n" +
            ":::comparison\n" +
            "- Option A — ship now\n" +
            "- Option B — ship later\n" +
            ":::\n";

        var html = CharterRenderer.RenderBody(markdown);
        var map = SourceMap.Build(markdown);

        var emitted = System.Text.RegularExpressions.Regex
            .Matches(html, "data-anchor=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // The fixture must actually HAVE sub-anchors, or the loop below sweeps nothing and passes blind.
        // Five: three bullets, two ordered items — plus the two :::comparison rows that already worked.
        Assert.Equal(7, emitted.Length);

        foreach (var anchor in emitted)
        {
            Assert.True(
                map.LineForAnchor(anchor).HasValue,
                $"the renderer emitted data-anchor=\"{anchor}\" but the source map cannot resolve it — a note " +
                "written there would orphan at drain time with nothing explaining why");
        }
    }
}
