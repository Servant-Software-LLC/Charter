using System.Text.RegularExpressions;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #171 — a link reference definition must not steal the line-1 block's anchor.
///
/// Markdig collects every <c>[foo]: http://…</c> declaration into ONE <c>LinkReferenceDefinitionGroup</c> and
/// appends it as a top-level child of the document with a SYNTHETIC position: its <c>Line</c> is always 0 and
/// its <c>Span</c> always starts at offset 0 and runs the length of the FIRST definition — wherever the
/// definitions actually sit. Until <c>CharterMarkdown.ParseDocument</c> stripped it (alongside the
/// <c>YamlFrontMatterBlock</c>, for the same reason — not content, renders nothing, must not occupy an anchor
/// slot) that node caused three distinct defects, one per seam that traverses the document:
///
/// <list type="number">
///   <item><description><b>The anchor collision (#171 as filed).</b> The group claimed the block slot at
///     1-based line 1, and <c>AnchorAssignment</c>'s block-namespace assignment is last-writer-wins, so the
///     group — walked after the real block — overwrote the id of whatever genuinely starts there, normally the
///     plan title. Adding or editing a link definition ANYWHERE then re-id'd the title and orphaned every note
///     on it.</description></item>
///   <item><description><b>The handoff duplication.</b> The group's span-derived raw content is a meaningless
///     PREFIX SLICE of the document, so <c>BlockDocument</c> yielded it as an extra <c>Prose</c> block and
///     <c>HandoffMarkdown</c> flattened a DUPLICATE of the plan's opening blocks into the plain CommonMark
///     Guardrails consumes.</description></item>
///   <item><description><b>The silent convert miss.</b> The group is inserted at the first definition's place
///     in the child order, so a definition between an "open questions" heading and its list broke
///     <c>MarkdownConvert</c>'s "the immediately following block must be the list" rule and the whole section
///     silently failed to promote.</description></item>
/// </list>
///
/// The load-bearing assertion here is EQUALITY ACROSS SHAPES, not a pinned hash: a test that merely pinned one
/// id would have passed against the defect. Every shape below carries the same <c># Title</c> on its own line
/// and must therefore produce the same anchor — and that anchor must additionally be the heading's OWN
/// content-derived id, so five shapes agreeing on a wrong value cannot pass either.
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","LinkReferenceDefinition")].
/// </summary>
[Trait("Category", "LinkReferenceDefinition")]
public class LinkReferenceDefinitionTests
{
    // The three shapes from #171's table: no definition, an UNUSED definition, and a USED one. All three start
    // "# Title" on line 1, so all three must anchor that heading identically.
    private const string NoDefinition = "# Title\n\nSee it.\n";
    private const string UnusedDefinition = "# Title\n\n[bar]: http://other.example\n\nSee it.\n";
    private const string UsedDefinition = "# Title\n\n[foo]: http://example.com\n\nSee [foo].\n";

    // Two further placements the same synthetic node reaches — the group's Line is 0 in every one of them.
    private const string DefinitionLast = "# Title\n\nSee [foo].\n\n[foo]: http://example.com\n";
    private const string DefinitionFirst = "[foo]: http://example.com\n\n# Title\n\nSee [foo].\n";

    // Front matter AND a definition — the case where the two stripped nodes meet. The heading is on line 5, so
    // the group does not collide with it; instead it used to register a PHANTOM anchor resolving to line 1,
    // which is inside the front matter the map is not supposed to address at all.
    private const string WithFrontMatter =
        "---\ncharter-format-version: 1\n---\n\n# Title\n\n[foo]: http://example.com\n\nSee [foo].\n";

    private static readonly string[] EveryShape =
        [NoDefinition, UnusedDefinition, UsedDefinition, DefinitionLast, DefinitionFirst, WithFrontMatter];

    private static readonly Regex H1WithId = new(@"<h1 id=""([^""]+)"">", RegexOptions.CultureInvariant);

    [Fact]
    public void Render_TheTitlesAnchor_IsIdenticalAcrossEveryLinkDefinitionShape()
    {
        var anchors = EveryShape.Select(HeadingAnchor).ToList();

        // The real assertion: same heading, same anchor, whatever link definitions the plan carries and
        // wherever they sit. Before the strip these were six different values.
        Assert.Single(anchors.Distinct(StringComparer.Ordinal));

        // ...and the value they agree on is the heading's OWN content-derived id — so "all six agree on
        // something wrong" fails too.
        Assert.Equal(BlockDocument.Parse(NoDefinition).Blocks[0].Id, anchors[0]);
    }

    [Fact]
    public void Render_AReferenceLink_StillResolvesToItsDefinitionsTarget()
    {
        // Stripping the group must not cost link RESOLUTION: Markdig resolves [foo] against the document's
        // link-reference dictionary during Parse, so the LinkInline is already materialized before
        // ParseDocument removes the node. This is the proof, not the argument.
        foreach (var plan in new[] { UsedDefinition, DefinitionLast, DefinitionFirst, WithFrontMatter })
        {
            var body = Body(CharterRenderer.Render(plan));

            Assert.Contains("<a href=\"http://example.com\">foo</a>", body, StringComparison.Ordinal);
            // The reference survived as a LINK, not as literal text.
            Assert.DoesNotContain("[foo]", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Render_ALinkDefinition_RendersNothingAndShiftsNoOtherBlock()
    {
        // The definition is invisible in the output, and the ONLY difference an added definition makes to the
        // rest of the document is the source lines it physically occupies — every anchor is unchanged.
        var withoutBody = Body(CharterRenderer.Render(NoDefinition));
        var withBody = Body(CharterRenderer.Render(UnusedDefinition));

        Assert.Equal(withoutBody, withBody);
        Assert.DoesNotContain("other.example", withBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ALinkDefinition_IsNotAContentBlock_AndDoesNotShiftTheBlockSet()
    {
        var with = BlockDocument.Parse(UnusedDefinition).Blocks;
        var without = BlockDocument.Parse(NoDefinition).Blocks;

        Assert.Equal(without.Count, with.Count);
        Assert.Equal(without.Select(b => b.Kind), with.Select(b => b.Kind));
        Assert.Equal(without.Select(b => b.Id), with.Select(b => b.Id));
        Assert.DoesNotContain(with, b => b.RawContent.Contains("other.example", StringComparison.Ordinal));
    }

    [Fact]
    public void Handoff_ALinkDefinition_DoesNotDuplicateThePlansOpeningBlocks()
    {
        // Defect 2: the group's raw content is a prefix slice of the document, so the handoff read
        // "# Title\n\n# Title\n\n[bar]: http://other.example\n\nSee it." — the plan title, twice, in the plain
        // CommonMark Guardrails breaks down.
        var handoff = HandoffMarkdown.Emit(UnusedDefinition);

        // Compared WITHOUT the trailing provenance stamp (Charter #172/#187), which hashes the SOURCE plan —
        // and these two sources genuinely differ by the link definition. The claim being pinned is that the
        // flattened CONTENT is identical, which is exactly what the stamp is not.
        Assert.Equal(HandoffOutput.WithoutStamp(HandoffMarkdown.Emit(NoDefinition)), HandoffOutput.WithoutStamp(handoff));
        Assert.Equal(1, Occurrences(handoff, "# Title"));

        // NOTE (#175): the definitions themselves are now absent from the handoff, so a plan whose prose uses
        // [foo] hands Guardrails a dangling reference. That is a KNOWN GAP, not the intended end state — it was
        // never carried faithfully (the pre-#171 "carry" was the corrupt prefix slice this test pins against),
        // and carrying it properly is a handoff-contract decision. This equality is the behaviour to revisit
        // when #175 is settled, NOT a statement that dropping them is correct.
    }

    [Fact]
    public void SourceMap_ALinkDefinition_AddsNoAnchorAndLeavesEveryRealBlockAtItsOwnLine()
    {
        var without = SourceMap.Build(NoDefinition);
        var with = SourceMap.Build(UnusedDefinition);

        // Anchors are content-derived, so a definition adds none — the set is identical...
        Assert.Equal(
            without.Anchors.OrderBy(a => a, StringComparer.Ordinal),
            with.Anchors.OrderBy(a => a, StringComparer.Ordinal));

        // ...and every anchor still resolves to the REAL source line of its own block, two lines lower for the
        // prose because the definition and its blank line physically sit above it.
        var blocks = BlockDocument.Parse(NoDefinition).Blocks;
        Assert.Equal(1, with.LineForAnchor(blocks[0].Id));
        Assert.Equal(3, without.LineForAnchor(blocks[1].Id));
        Assert.Equal(5, with.LineForAnchor(blocks[1].Id));
    }

    [Fact]
    public void SourceMap_FrontMatterAndALinkDefinition_RegisterNoPhantomAnchorIntoTheFrontMatter()
    {
        var map = SourceMap.Build(WithFrontMatter);

        // Every anchor the map holds belongs to a real block, and the plan's first real block is the heading on
        // line 5 — nothing resolves into the three front-matter lines above it.
        Assert.Equal(2, map.Anchors.Count);
        Assert.DoesNotContain(map.Anchors, a => map.LineForAnchor(a) < 5);
    }

    [Fact]
    public void Anchor_ANoteOnTheTitle_StillResolvesAfterALinkDefinitionIsAddedElsewhere()
    {
        // The reviewer's guarantee (invariant 2) as the round trip that used to break: annotate the title,
        // then edit a DIFFERENT part of the plan by adding a link definition. The anchor a queued note carries
        // must still resolve — and to the block's real line, wherever the inserted definition pushed it.
        const string before = "# Title\n\nSee it.\n\nAnd more.\n";
        const string after = "# Title\n\n[bar]: http://other.example\n\nSee it.\n\nAnd more.\n";

        var blocks = BlockDocument.Parse(before).Blocks;
        var titleAnchor = blocks[0].Id;
        var proseAnchor = blocks[2].Id;

        var beforeMap = SourceMap.Build(before);
        Assert.Equal(1, beforeMap.LineForAnchor(titleAnchor));
        Assert.Equal(5, beforeMap.LineForAnchor(proseAnchor));

        var afterMap = SourceMap.Build(after);

        // The title is above the definition, so it neither moves nor — the #171 defect — changes id.
        Assert.Equal(1, afterMap.LineForAnchor(titleAnchor));
        // A block BELOW the definition keeps its id too, and re-resolves to the line it has now.
        Assert.Equal(7, afterMap.LineForAnchor(proseAnchor));
    }

    [Fact]
    public void Convert_PromotesAQuestionSection_EvenWhenALinkDefinitionPrecedesItsList()
    {
        // Defect 3: the group is inserted at the first definition's position in the child order, so it sat
        // between the heading and the list and MarkdownConvert's "the next block must be the list" test failed.
        // The section promoted nothing, silently.
        const string plan =
            "# Plan\n\n"
            + "## Open questions\n\n"
            + "[foo]: http://example.com\n\n"
            + "- Which datastore?\n"
            + "- Which region?\n";

        var result = MarkdownConvert.Convert(plan);

        var section = Assert.Single(result.Sections);
        Assert.Equal(2, section.Promoted);
        Assert.Empty(section.Skipped);
        Assert.Contains(":::question", result.Markdown, StringComparison.Ordinal);
        // The definition itself is untouched in the seed — Convert splices on the raw source, never on the
        // stripped parse.
        Assert.Contains("[foo]: http://example.com", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseDocument_NeverYieldsTwoTopLevelBlocksOnTheSameLine()
    {
        // The invariant the whole block namespace rests on: AnchorAssignment keys block slots by line and the
        // last writer wins, so two top-level blocks claiming one line silently means one of them loses its id.
        // A synthetic node — YamlFrontMatterBlock, LinkReferenceDefinitionGroup, or any future one an added
        // Markdig extension appends at Line 0 — is the only way that happens, which makes this the tripwire for
        // the whole defect class rather than for #171 alone. The corpus deliberately includes footnote syntax:
        // Markdig's FootnoteGroup has exactly this shape, so the day UseFootnotes() joins the pipeline, this
        // goes red instead of quietly re-ids'ing the plan title.
        string[] corpus =
        [
            .. EveryShape,
            "[foo]: http://example.com\n",
            "# T\n\n[a]: http://a.example\n\nMid.\n\n[b]: http://b.example\n\nEnd.\n",
            "---\ncharter-format-version: 1\n---\n\n# T\n\nProse.\n",
            "# T\n\nText with a footnote[^1].\n\n[^1]: body\n",
            "# T\n\n- one\n- two\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n:::note\nA note.\n:::\n\n---\n\n```\ncode\n```\n",
        ];

        foreach (var markdown in corpus)
        {
            var lines = CharterMarkdown.ParseDocument(markdown)
                .Select(CharterMarkdown.StartLine)
                .ToList();

            Assert.Equal(lines.Count, lines.Distinct().Count());
        }
    }

    /// <summary>The heading anchor the renderer actually stamped on the plan's <c>&lt;h1&gt;</c>.</summary>
    private static string HeadingAnchor(string markdown)
    {
        var match = H1WithId.Match(CharterRenderer.Render(markdown));
        Assert.True(match.Success, "the rendered plan carries no <h1 id=...>");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// The rendered document's BODY only. Scoped deliberately: the shell inlines the whole commented
    /// stylesheet, so a whole-document string scan reads CSS prose rather than the plan (the trap
    /// <c>ClipAffordanceTests</c> hit from the other direction).
    /// </summary>
    private static string Body(string html)
    {
        var start = html.IndexOf("<body>", StringComparison.Ordinal);
        return start < 0 ? html : html[start..];
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
