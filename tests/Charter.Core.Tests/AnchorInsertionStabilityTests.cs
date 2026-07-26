using System.Text.RegularExpressions;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #50 — INSERTION STABILITY of duplicate-content anchors. <c>AnchorAssignment</c> must discriminate
/// two identical blocks WITHOUT making a later duplicate's id a function of how many identical blocks precede
/// it: a positional (occurrence-count) discriminator renumbers every later duplicate when an identical block
/// is inserted earlier, so an existing annotation on <c>bH-2</c> resolves — successfully and silently — to a
/// DIFFERENT block. That is misattribution, strictly worse than orphaning.
///
/// The load-bearing test is
/// <see cref="InsertingAnIdenticalBlockEarlier_LeavesTheExistingDuplicateAnchorOnTheSameBlock"/>: it is the
/// exact gap <see cref="DuplicateContentAnchorTests"/> left open (that class covers duplicates, but never an
/// insertion BEFORE one).
///
/// The honest limit is <see cref="InsertingIntoARunOfIdenticalSiblings_Orphans_RatherThanMisattributes"/>:
/// consecutive identical blocks with identical surroundings are genuinely interchangeable, so no
/// content-derived scheme can keep them stable. The requirement there is weaker but still absolute — the
/// annotation must ORPHAN (detectable, and reported as such since #49), never slide onto a neighbour.
/// </summary>
[Trait("Category", "CoreRenderer")]
public class AnchorInsertionStabilityTests
{
    [Fact]
    public void InsertingAnIdenticalBlockEarlier_LeavesTheExistingDuplicateAnchorOnTheSameBlock()
    {
        // 1: Intro paragraph.
        // 3: Shared boilerplate.      <- 1st occurrence
        // 5: Divider paragraph.
        // 7: Shared boilerplate.      <- 2nd occurrence, the one the reviewer annotates
        const string before =
            "Intro paragraph.\n\n" +
            "Shared boilerplate.\n\n" +
            "Divider paragraph.\n\n" +
            "Shared boilerplate.\n";

        var mapBefore = SourceMap.Build(before);
        var annotated = AnchorAtLine(mapBefore, 7);
        var otherOccurrence = AnchorAtLine(mapBefore, 3);
        Assert.NotEqual(annotated, otherOccurrence);

        // A THIRD identical block is inserted BEFORE both — the agent (or a teammate) adding boilerplate above.
        // 1: Shared boilerplate.      <- new
        // 3: Intro paragraph.
        // 5: Shared boilerplate.      <- was line 3
        // 7: Divider paragraph.
        // 9: Shared boilerplate.      <- was line 7: THE ANNOTATED BLOCK
        const string after =
            "Shared boilerplate.\n\n" +
            "Intro paragraph.\n\n" +
            "Shared boilerplate.\n\n" +
            "Divider paragraph.\n\n" +
            "Shared boilerplate.\n";

        var mapAfter = SourceMap.Build(after);

        // The teeth: the annotation still points at the SAME block (now at line 9). Under the positional
        // discriminator it resolved to line 5 instead — a different block, silently.
        Assert.Equal(9, mapAfter.LineForAnchor(annotated));
        Assert.NotEqual(5, mapAfter.LineForAnchor(annotated));

        // The other pre-existing duplicate is likewise untouched.
        Assert.Equal(5, mapAfter.LineForAnchor(otherOccurrence));
    }

    [Fact]
    public void InsertingAnIdenticalComparisonBlockEarlier_LeavesTheExistingRowSubAnchorOnTheSameRow()
    {
        // Sub-anchors are the issue's other named trigger (":::comparison rows, :::diff lines"). The shared row
        // appears in two containers; the reviewer annotates the one in the SECOND container (line 8).
        const string before =
            ":::comparison\n- Option A\n- Shared row\n:::\n" +
            "\n" +
            ":::comparison\n- Option B\n- Shared row\n:::\n";

        var mapBefore = SourceMap.Build(before);
        var annotated = AnchorAtLine(mapBefore, 8);

        // A third comparison — carrying the same shared row — is inserted at the TOP.
        const string after =
            ":::comparison\n- Option Z\n- Shared row\n:::\n" +
            "\n" +
            ":::comparison\n- Option A\n- Shared row\n:::\n" +
            "\n" +
            ":::comparison\n- Option B\n- Shared row\n:::\n";

        var mapAfter = SourceMap.Build(after);

        // The annotated row moved down by the 5 inserted lines and kept its identity.
        Assert.Equal(13, mapAfter.LineForAnchor(annotated));
    }

    [Fact]
    public void InsertingAnIdenticalDiffLineEarlier_LeavesTheExistingLineSubAnchorOnTheSameLine()
    {
        // 1: :::diff
        // 2: +shared added line     <- 1st
        // 3: -a removed line
        // 4: +shared added line     <- 2nd, annotated
        // 5: :::
        const string before =
            ":::diff\n+shared added line\n-a removed line\n+shared added line\n:::\n";

        var mapBefore = SourceMap.Build(before);
        var annotated = AnchorAtLine(mapBefore, 4);

        // A third identical added line is inserted BEFORE both (line 2), separated by distinct context so the
        // two originals keep their neighbourhoods.
        // 1: :::diff
        // 2: +shared added line     <- new
        // 3: -another removed line
        // 4: +shared added line     <- was line 2
        // 5: -a removed line
        // 6: +shared added line     <- was line 4: THE ANNOTATED LINE
        // 7: :::
        const string after =
            ":::diff\n+shared added line\n-another removed line\n+shared added line\n" +
            "-a removed line\n+shared added line\n:::\n";

        var mapAfter = SourceMap.Build(after);

        Assert.Equal(6, mapAfter.LineForAnchor(annotated));
    }

    [Fact]
    public void InsertingIntoARunOfIdenticalSiblings_Orphans_RatherThanMisattributes()
    {
        // The honest limit. Two ADJACENT identical blocks share every neighbourhood at every depth, so nothing
        // content-derived tells "the 2nd of 2" from "the 2nd of 3". The contract here is therefore the weaker
        // (but still absolute) one: growing the run must INVALIDATE the anchor, never quietly re-point it.
        // 1: Intro paragraph.
        // 3: Same block.        <- 1st
        // 5: Same block.        <- 2nd, annotated
        const string before = "Intro paragraph.\n\nSame block.\n\nSame block.\n";

        var mapBefore = SourceMap.Build(before);
        var annotated = AnchorAtLine(mapBefore, 5);

        // A third identical block joins the run.
        const string after = "Intro paragraph.\n\nSame block.\n\nSame block.\n\nSame block.\n";

        var mapAfter = SourceMap.Build(after);

        // It resolves to NOTHING (an orphan the reviewer/agent is told about) rather than to the newly inserted
        // block at line 5 — which is exactly what the positional discriminator did.
        Assert.Null(mapAfter.LineForAnchor(annotated));
    }

    [Fact]
    public void DuplicateAnchorAssignment_IsIdenticalUnderCrlf()
    {
        // Ids must survive a Windows checkout: content is CRLF-normalized before hashing, and the duplicate
        // discriminator is a function of already-normalized ids, so an LF and a CRLF copy of one document must
        // assign byte-identical anchors at identical lines.
        const string lf =
            "Intro paragraph.\n\nShared boilerplate.\n\nDivider paragraph.\n\nShared boilerplate.\n";
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        var mapLf = SourceMap.Build(lf);
        var mapCrlf = SourceMap.Build(crlf);

        Assert.True(mapLf.Anchors.SetEquals(mapCrlf.Anchors), "CRLF changed the assigned anchor ids.");
        foreach (var anchor in mapLf.Anchors)
        {
            Assert.Equal(mapLf.LineForAnchor(anchor), mapCrlf.LineForAnchor(anchor));
        }
    }

    [Fact]
    public void AfterAnInsertion_RenderedAnchors_AndSourceMapKeys_StillAgree()
    {
        // ANTI-DRIFT under the new scheme: the renderer and the source map each run AnchorAssignment over their
        // own parse, so a context-sensitive discriminator must still land byte-identically on both paths.
        const string markdown =
            "Intro paragraph.\n\n" +
            "Shared boilerplate.\n\n" +
            ":::comparison\n- Option A\n- Shared row\n:::\n\n" +
            "Shared boilerplate.\n\n" +
            ":::comparison\n- Option B\n- Shared row\n:::\n\n" +
            ":::diff\n+shared added line\n-a removed line\n+shared added line\n:::\n";

        var html = CharterRenderer.Render(markdown);
        var renderedAnchors = RenderedAnchors(html);
        var sourceMapKeys = SourceMap.Build(markdown).Anchors;

        // Sanity: discrimination actually happened (this document is full of duplicates).
        Assert.Contains(renderedAnchors, a => a.Contains('-', StringComparison.Ordinal));

        Assert.True(renderedAnchors.SetEquals(sourceMapKeys),
            "Rendered anchors and source-map keys diverged.\n" +
            "rendered-only: " + string.Join(", ", renderedAnchors.Except(sourceMapKeys)) + "\n" +
            "sourcemap-only: " + string.Join(", ", sourceMapKeys.Except(renderedAnchors)));
    }

    /// <summary>The single anchor the map resolves to <paramref name="line"/> (asserting exactly one does).</summary>
    private static string AnchorAtLine(SourceMap map, int line)
        => Assert.Single(map.Anchors, a => map.LineForAnchor(a) == line);

    /// <summary>Every id/data-anchor value the rendered HTML carries, as a set.</summary>
    private static HashSet<string> RenderedAnchors(string html)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(html, "(?<![-\\w])id=\"([^\"]+)\""))
        {
            anchors.Add(m.Groups[1].Value);
        }

        foreach (Match m in Regex.Matches(html, "data-anchor=\"([^\"]+)\""))
        {
            anchors.Add(m.Groups[1].Value);
        }

        return anchors;
    }
}
