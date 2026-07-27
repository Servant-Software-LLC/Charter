using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Guards for Charter #44 — a bare newline inside a paragraph must render as a VISUAL line break.
/// <para>
/// A single newline within a CommonMark paragraph is a <em>soft</em> break, which HTML's default
/// <c>white-space: normal</c> collapses — so an evidently-intentional multi-line block (the metadata header
/// that surfaced this: Status / Author / Date / Last-reviewed) rendered as one run-on line. Charter enables
/// Markdig's <c>UseSoftlineBreakAsHardlineBreak</c> so the reviewer sees what the author intended, matching
/// GFM.
/// </para>
/// <para>
/// The load-bearing safety property is the second half of this suite: the extension is RENDERER-level, so it
/// must never perturb the parsed model. Content-derived anchors, the <see cref="SourceMap"/>, and the
/// plain-CommonMark handoff must all be byte-identical to their pre-fix values — otherwise every existing
/// annotation on a multi-line paragraph would silently re-anchor or orphan.
/// </para>
/// </summary>
[Trait("Category", "SoftLineBreak")]
public class SoftLineBreakTests
{
    // The real-world shape from the issue: four visually distinct lines the author clearly intended.
    private const string MetadataHeader =
        "Status: Draft for review\n" +
        "Author: j.humphrey\n" +
        "Date: 2026-06-26\n" +
        "Last technical review: 2026-07-23\n";

    [Fact]
    public void Render_SingleNewlinesInAParagraph_BecomeVisualLineBreaks()
    {
        var html = CharterRenderer.Render(MetadataHeader);

        // Three interior newlines across four lines => three breaks.
        var breaks = System.Text.RegularExpressions.Regex.Matches(html, "<br\\s*/?>").Count;
        Assert.Equal(3, breaks);
    }

    [Fact]
    public void Render_KeepsTheLinesInOrder_AndDoesNotDropText()
    {
        var html = CharterRenderer.Render(MetadataHeader);

        var status = html.IndexOf("Status: Draft for review", System.StringComparison.Ordinal);
        var author = html.IndexOf("Author: j.humphrey", System.StringComparison.Ordinal);
        var date = html.IndexOf("Date: 2026-06-26", System.StringComparison.Ordinal);
        var reviewed = html.IndexOf("Last technical review: 2026-07-23", System.StringComparison.Ordinal);

        Assert.True(status >= 0 && author > status && date > author && reviewed > date,
            "every authored line must survive, in source order");
    }

    [Fact]
    public void Render_ABlankLineStillSeparatesParagraphs_NotJustABreak()
    {
        var html = CharterRenderer.Render("First para.\n\nSecond para.");

        // A hard paragraph break must remain a <p> boundary, not degrade into a <br>.
        Assert.Contains("<p", html);
        Assert.DoesNotContain("<br", html);
    }

    [Fact]
    public void Render_ParagraphWithNoInteriorNewline_IsUnaffected()
    {
        var html = CharterRenderer.Render("A single ordinary sentence.");

        Assert.DoesNotContain("<br", html);
        Assert.Contains("A single ordinary sentence.", html);
    }

    // ---- The safety half: a RENDERER extension must not perturb the parsed model. ----

    [Fact]
    public void Anchors_OnAMultiLineParagraph_AreUnchangedByTheExtension()
    {
        // Block ids are derived from the RAW markdown source, never from rendered HTML. If the extension
        // ever leaked into the model, this id would shift and every existing annotation on a multi-line
        // paragraph would silently orphan.
        var document = BlockDocument.Parse(MetadataHeader);
        var block = Assert.Single(document.Blocks);

        Assert.Equal(Block.StableId(block.RawContent), block.Id);
        Assert.Contains("Author: j.humphrey", block.RawContent);
    }

    [Fact]
    public void SourceMap_StillResolvesAMultiLineParagraphToItsFirstLine()
    {
        var markdown = "# Heading\n\n" + MetadataHeader;
        var document = BlockDocument.Parse(markdown);
        var paragraph = document.Blocks[^1];

        var line = SourceMap.Build(markdown).LineForAnchor(paragraph.Id);

        Assert.Equal(3, line); // heading(1), blank(2), paragraph starts at 3
    }

    [Fact]
    public void Handoff_LeavesTheMarkdownAsPlainCommonMark_WithNoBrTags()
    {
        // Handoff emits markdown for Guardrails, not HTML — the visual-break extension must not leak into it.
        var handoff = HandoffMarkdown.Emit(MetadataHeader);

        Assert.DoesNotContain("<br", handoff);
        Assert.Contains("Status: Draft for review", handoff);
        Assert.Contains("Author: j.humphrey", handoff);
    }
}
