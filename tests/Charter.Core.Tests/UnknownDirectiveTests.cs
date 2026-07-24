using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// The unknown-directive footgun fix (Architecture B §2.2, Charter #22). An unrecognized <c>:::foo</c> — a typo
/// or an unlisted container — no longer masquerades as a silent <see cref="BlockKind.Note"/>: it classifies to
/// <see cref="BlockKind.Unknown"/>, renders as a VISIBLE "unknown directive" element that names the offending
/// directive (still carrying its stable annotation anchor), and flattens in the handoff as a flagged line rather
/// than a plain note. The real catalog (<c>:::note</c>/<c>:::warn</c>/<c>:::comparison</c>/<c>:::diagram</c>/
/// <c>:::diff</c>/<c>:::question</c>/<c>:::custom-html</c>) is unaffected.
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","UnknownDirective")].
/// </summary>
[Trait("Category", "UnknownDirective")]
public class UnknownDirectiveTests
{
    [Fact]
    public void UnknownDirective_ClassifiesAsUnknown_NotNote()
    {
        var block = BlockDocument.Parse(":::foo\nsome body\n:::").Blocks[0];

        Assert.Equal(BlockKind.Unknown, block.Kind);
        Assert.NotEqual(BlockKind.Note, block.Kind);
    }

    [Theory]
    [InlineData(":::note\nx\n:::", BlockKind.Note)]
    [InlineData(":::warn\nx\n:::", BlockKind.Warn)]
    [InlineData(":::comparison\n- a\n- b\n:::", BlockKind.Comparison)]
    [InlineData(":::custom-html\n<b>x</b>\n:::", BlockKind.CustomHtml)]
    public void KnownDirectives_StillClassifyToTheirCatalogKinds(string markdown, BlockKind expected)
    {
        Assert.Equal(expected, BlockDocument.Parse(markdown).Blocks[0].Kind);
    }

    [Fact]
    public void Render_UnknownDirective_IsVisibleElement_NamingTheDirective_WithStableId()
    {
        const string markdown = ":::mysterious\nbody text\n:::";
        var block = BlockDocument.Parse(markdown).Blocks[0];

        var html = CharterRenderer.Render(markdown);

        Assert.Contains("unknown-directive", html, StringComparison.Ordinal);   // clearly marked
        Assert.Contains(":::mysterious", html, StringComparison.Ordinal);        // names the directive
        Assert.Contains($"id=\"{block.Id}\"", html, StringComparison.Ordinal);   // still annotatable
        Assert.DoesNotContain("class=\"note\"", html, StringComparison.Ordinal); // NOT masquerading as a note
    }

    [Fact]
    public void Handoff_UnknownDirective_IsFlagged_NotANote_AndNeverReopensADirectiveLine()
    {
        const string markdown = ":::mysterious\nbody text\n:::";

        var handoff = HandoffMarkdown.Emit(markdown);

        Assert.Contains("Unknown Charter directive", handoff, StringComparison.Ordinal);
        Assert.Contains(":::mysterious", handoff, StringComparison.Ordinal);
        Assert.DoesNotContain("**Note:**", handoff, StringComparison.Ordinal);

        // Invariant 5: the plain-markdown handoff must never re-open a ::: directive line (the flag carries the
        // directive name inside inline code on a blockquote line instead).
        foreach (var line in handoff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            Assert.False(
                line.TrimStart().StartsWith(":::", StringComparison.Ordinal),
                $"handoff re-opened a directive line: {line}");
        }
    }

    [Fact]
    public void KnownContainers_Unaffected_ByTheUnknownRoute_WarnStaysWarn_CustomHtmlStaysVerbatim()
    {
        // :::warn still resolves to a labeled callout, and :::custom-html still passes its inner HTML verbatim —
        // the unknown route is strictly the else-branch, touching neither.
        Assert.Contains("**Warning:**", HandoffMarkdown.Emit(":::warn\nheads up\n:::"), StringComparison.Ordinal);
        Assert.Contains("<b>live</b>", HandoffMarkdown.Emit(":::custom-html\n<b>live</b>\n:::"), StringComparison.Ordinal);
    }
}
