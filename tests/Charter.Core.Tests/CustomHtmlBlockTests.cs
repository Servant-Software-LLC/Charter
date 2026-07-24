using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Tests for the promoted <c>:::custom-html</c> block (Architecture B §2.1). The renderer already
/// special-cased custom-html, but <c>ClassifyContainer</c> misclassified it as a <see cref="BlockKind.Note"/>;
/// promoting it to a first-class <see cref="BlockKind.CustomHtml"/> makes the three seams AGREE: the block
/// model classifies it as custom-html, the renderer passes its inner HTML through live (the one sanctioned
/// raw-HTML escape hatch), and the handoff flattener emits that inner HTML verbatim instead of wrapping it as
/// a blockquote callout (which is what it wrongly did while misclassified).
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","CustomHtmlBlock")].
/// </summary>
[Trait("Category", "CustomHtmlBlock")]
public class CustomHtmlBlockTests
{
    private const string CustomHtmlDoc =
        ":::custom-html\n" +
        "<div class=\"widget\"><span>live content</span></div>\n" +
        ":::";

    [Fact]
    public void Classify_CustomHtml_IsFirstClassKind_NotNote()
    {
        var block = Assert.Single(BlockDocument.Parse(CustomHtmlDoc).Blocks);

        // The promotion: classified as its own kind, never the note fallback it used to land in.
        Assert.Equal(BlockKind.CustomHtml, block.Kind);
        Assert.NotEqual(BlockKind.Note, block.Kind);
    }

    [Fact]
    public void Render_CustomHtml_PassesInnerHtmlThroughLive_WithStableId()
    {
        var block = BlockDocument.Parse(CustomHtmlDoc).Blocks[0];
        var html = CharterRenderer.Render(CustomHtmlDoc);

        // The escape hatch: the inner HTML is emitted LIVE (unescaped), wrapped in the custom-html carrier that
        // carries the block's content-derived stable id so the escape-hatch content stays annotatable.
        Assert.Contains($"<div class=\"custom-html\" id=\"{block.Id}\">", html);
        Assert.Contains("<div class=\"widget\"><span>live content</span></div>", html);
        // Not escaped to inert text (that is what every OTHER surface does).
        Assert.DoesNotContain("&lt;div class=\"widget\"", html);
    }

    [Fact]
    public void Handoff_CustomHtml_EmitsInnerHtmlVerbatim_NotACallout()
    {
        var output = HandoffMarkdown.Emit(CustomHtmlDoc);

        // The inner HTML survives the bridge verbatim (raw HTML is valid CommonMark)...
        Assert.Contains("<div class=\"widget\"><span>live content</span></div>", output);
        // ...no ::: fence line leaks (invariant 5's line-anchored proxy)...
        Assert.DoesNotMatch(@"(?m)^:::", output);
        // ...and it is NOT flattened into a labeled blockquote callout (the old misclassified-as-note behavior).
        Assert.DoesNotContain("**Note:**", output);
        Assert.DoesNotMatch(@"(?m)^>\s", output);
    }

    [Fact]
    public void CustomHtml_ClassifyRenderHandoff_AllAgreeOnTheSameBlock()
    {
        // Consistency across the three seams for the same source: one block, classified custom-html, whose
        // inner HTML appears live in BOTH the rendered artifact and the flattened handoff.
        var block = Assert.Single(BlockDocument.Parse(CustomHtmlDoc).Blocks);
        Assert.Equal(BlockKind.CustomHtml, block.Kind);

        const string inner = "<div class=\"widget\"><span>live content</span></div>";
        Assert.Contains(inner, CharterRenderer.Render(CustomHtmlDoc));
        Assert.Contains(inner, HandoffMarkdown.Emit(CustomHtmlDoc));
    }
}
