using System.Text.RegularExpressions;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #87 — the four block types that clipped their content with NO reachable affordance, fixed with
/// the treatment Charter #68 established for a wide table: a scroll box that carries a persistent
/// <c>::-webkit-scrollbar</c> gutter, a <c>tabindex="0"</c> tab stop, a <c>role="region"</c> and an
/// accessible name.
///
/// <para>The four are a <c>:::diff</c> (the severe one — <c>overflow: hidden</c>, so its clipped text could
/// not be reached by mouse, keyboard or gesture on ANY platform), a fenced code block, an unknown
/// directive's preserved body, and a <c>&lt;table&gt;</c> an author put inside <c>:::custom-html</c>. Each
/// carries content a plan is actually made of — file paths and method signatures, which have no line-break
/// opportunity anywhere in them.</para>
///
/// <para>These are the DETERMINISTIC half of the fix, asserted against the emitted markup and the bundled
/// stylesheet on every OS. What a C#-string test structurally cannot see — that a real wheel gesture moves
/// the box, that the browser lays out the gutter the stylesheet declares, that Tab reaches it — is asserted
/// in <c>Charter.Browser.Tests</c>, where it is measured.</para>
///
/// <para>The load-bearing risk in all four is ANCHORING. The SDK walks up from the clicked node to the
/// nearest ancestor carrying an <c>id</c>/<c>data-anchor</c>/<c>data-charter-anchor</c>
/// (<c>closestAnchored</c>), so any wrapper introduced here must be ANCHOR-INVISIBLE or it silently
/// re-targets every note on that block. For <c>:::diff</c> the stake is higher still: a diff carries
/// per-LINE sub-anchors, so the wrapper must be invisible to the walk that resolves an individual
/// <c>.diff-line</c> as well as to the one that resolves the block.</para>
/// </summary>
[Trait("Category", "ClipAffordance")]
public class ClipAffordanceTests
{
    // ---- :::diff -------------------------------------------------------------------------------------------

    // A diff line's raw text INCLUDES its +/- (or space) marker, and the sub-anchor is Block.StableId of that
    // raw text — computed here exactly as the renderer must, so every assertion stays structural.
    private const string AddedLine = "+src/Charter.Core/CharterRenderer.cs::RenderBody_no_break_opportunity";
    private const string RemovedLine = "-src/Charter.Core/CharterRenderer.cs::RenderBody_old_signature_here";
    private const string ContextLine = " a context line";

    private const string DiffMarkdown =
        ":::diff\n" + AddedLine + "\n" + RemovedLine + "\n" + ContextLine + "\n:::";

    /// <summary>
    /// The structure chosen for <c>:::diff</c>, and the reason a wrapper was needed rather than flipping
    /// <c>.diff { overflow: hidden }</c> to <c>auto</c>: the <c>hidden</c> is what clips the per-line
    /// FULL-BLEED add/del backgrounds to the card's 6px radius, and that clip has to hold unconditionally —
    /// including while the content is scrolled. One box cannot both hard-clip its own painted corners and be
    /// the user-scrollable box. So the clip and the radius stay on the outer <c>.diff</c> (which is also the
    /// annotation anchor and, at review time, the positioned host of the SDK's whole-block badge), and the
    /// scrolling moves to an inner <c>.diff-scroll</c>.
    /// </summary>
    [Fact]
    public void Render_Diff_ScrollsInsideAnInnerRegion_WhileTheCardKeepsTheClipAndTheAnchor()
    {
        var block = BlockDocument.Parse(DiffMarkdown).Blocks[0];
        var body = CharterRenderer.RenderBody(DiffMarkdown);

        // The anchored card opens, then the scroll region, then the lines — the scroll box is INSIDE the
        // clipping card, never around it (which would have moved the block's own path in the DOM).
        Assert.Matches(
            new Regex(
                "<div class=\"diff\" id=\"" + Regex.Escape(block.Id) + "\">\\s*" +
                "<div class=\"diff-scroll\"[^>]*>\\s*<div class=\"diff-line "),
            body);

        // …and it closes immediately after the last line, inside the card.
        Assert.Matches(new Regex("</div>\\s*</div>\\s*</div>\\s*$"), body.TrimEnd());
    }

    [Fact]
    public void Render_DiffScrollRegion_IsKeyboardReachableAndLabelled()
    {
        var wrapper = TagOf(CharterRenderer.RenderBody(DiffMarkdown), "div", "diff-scroll");

        Assert.Contains("tabindex=\"0\"", wrapper, StringComparison.Ordinal);
        Assert.Contains("role=\"region\"", wrapper, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Diff\"", wrapper, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wrapper is ANCHOR-INVISIBLE, and for a <c>:::diff</c> that must hold for the per-LINE sub-anchors
    /// too: a reviewer annotates an individual line, so <c>closestAnchored</c> has to walk from the clicked
    /// line straight past the wrapper. It therefore carries no <c>id</c>, no <c>data-anchor</c> and no
    /// <c>data-charter-anchor</c>, and every line keeps the id AND data-anchor it had before.
    /// </summary>
    [Fact]
    public void Render_DiffScrollRegion_IsAnchorInvisible_AndEveryLineKeepsItsOwnSubAnchor()
    {
        var body = CharterRenderer.RenderBody(DiffMarkdown);
        var wrapper = TagOf(body, "div", "diff-scroll");

        Assert.DoesNotContain("id=\"", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("data-anchor", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("data-charter-anchor", wrapper, StringComparison.Ordinal);

        foreach (var line in new[] { AddedLine, RemovedLine, ContextLine })
        {
            var sub = Block.StableId(line);
            Assert.Contains("data-anchor=\"" + sub + "\"", body, StringComparison.Ordinal);
            Assert.Contains("id=\"" + sub + "\"", body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The anchor→markdown-line map is built from the MARKDOWN, so a render-only wrapper cannot appear in it.
    /// Asserted rather than assumed, because the sub-anchor round-trip is the half of invariant 2 a wrapper
    /// would break invisibly: the note would still post, and reach the agent pointing at the wrong line.
    /// </summary>
    [Fact]
    public void Render_Diff_SourceMapSubAnchorRoundTripIsUnchanged()
    {
        var map = SourceMap.Build(DiffMarkdown);
        var block = BlockDocument.Parse(DiffMarkdown).Blocks[0];

        Assert.Equal(1, map.LineForAnchor(block.Id));
        Assert.Equal(2, map.LineForAnchor(Block.StableId(AddedLine)));
        Assert.Equal(3, map.LineForAnchor(Block.StableId(RemovedLine)));
        Assert.Equal(4, map.LineForAnchor(Block.StableId(ContextLine)));
    }

    // ---- fenced code ---------------------------------------------------------------------------------------

    private const string CodeMarkdown =
        "```csharp\n" +
        "var path = \"src/Charter.Core/CharterRenderer.cs::RenderBody_no_break_opportunity_here\";\n" +
        "```";

    /// <summary>
    /// A fenced code block needs NO wrapper: <c>&lt;pre&gt;</c> is already the scroll container
    /// (<c>pre { overflow: auto }</c>) and is a real element that can take a tab stop, so the affordance goes
    /// straight onto it. That also keeps the block's own DOM path unchanged, and the stable id stays exactly
    /// where every other block carries it.
    /// </summary>
    [Fact]
    public void Render_FencedCode_IsAKeyboardReachableNamedRegion_WithTheAnchorStillOnThePre()
    {
        var block = BlockDocument.Parse(CodeMarkdown).Blocks[0];
        var body = CharterRenderer.RenderBody(CodeMarkdown);
        var tag = TagOf(body, "pre", null);

        Assert.Contains("id=\"" + block.Id + "\"", tag, StringComparison.Ordinal);
        Assert.Contains("tabindex=\"0\"", tag, StringComparison.Ordinal);
        Assert.Contains("role=\"region\"", tag, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Code block\"", tag, StringComparison.Ordinal);

        // The language still rides <code>, never the anchored <pre> root.
        Assert.Contains("<code class=\"language-csharp\">", body, StringComparison.Ordinal);
    }

    // ---- an unknown directive's preserved body -------------------------------------------------------------

    private const string UnknownMarkdown =
        ":::file-tree\n" +
        "src/Charter.Core/CharterRenderer.cs::RenderBody_no_break_opportunity_here_at_all\n" +
        ":::";

    /// <summary>
    /// The unknown directive's body is preserved precisely so a directive typo cannot lose real plan content
    /// (#48/C5). Clipping it invisibly undercuts the reason it exists, so its <c>&lt;pre&gt;</c> gets the same
    /// treatment as a code block's. It carries NO id — the block's anchor is on the wrapping
    /// <c>div.unknown-directive</c> — so it is anchor-invisible by construction.
    /// </summary>
    [Fact]
    public void Render_UnknownDirectiveBody_IsAKeyboardReachableNamedRegion_AndStaysAnchorInvisible()
    {
        var block = BlockDocument.Parse(UnknownMarkdown).Blocks[0];
        var body = CharterRenderer.RenderBody(UnknownMarkdown);
        var tag = TagOf(body, "pre", "unknown-directive-body");

        Assert.Contains("tabindex=\"0\"", tag, StringComparison.Ordinal);
        Assert.Contains("role=\"region\"", tag, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Unknown directive body\"", tag, StringComparison.Ordinal);

        Assert.DoesNotContain("id=\"", tag, StringComparison.Ordinal);
        Assert.DoesNotContain("data-anchor", tag, StringComparison.Ordinal);
        Assert.Contains("<div class=\"unknown-directive\" id=\"" + block.Id + "\">", body, StringComparison.Ordinal);

        // …and the body itself still survives, still escaped.
        Assert.Contains("CharterRenderer.cs", body, StringComparison.Ordinal);
    }

    // ---- :::custom-html ------------------------------------------------------------------------------------

    private const string CustomHtmlMarkdown =
        ":::custom-html\n" +
        "<table><tr><td>src/Charter.Core/CharterRenderer.cs::RenderBody_no_break_here</td></tr></table>\n" +
        ":::";

    /// <summary>
    /// The escape hatch's body is emitted VERBATIM, so the renderer cannot put an attribute on a
    /// <c>&lt;table&gt;</c> the author wrote — the containment has to be a box Charter owns. The scroll region
    /// therefore wraps the whole raw body, and the anchored <c>div.custom-html</c> stays a plain,
    /// non-scrolling frame (so the SDK's absolutely-positioned whole-block badge stays pinned to its corner
    /// rather than riding away with the horizontal scroll — the Charter #51 lesson).
    /// </summary>
    [Fact]
    public void Render_CustomHtml_WrapsItsRawBodyInAKeyboardReachableNamedRegion()
    {
        var block = BlockDocument.Parse(CustomHtmlMarkdown).Blocks[0];
        var body = CharterRenderer.RenderBody(CustomHtmlMarkdown);

        Assert.Matches(
            new Regex(
                "<div class=\"custom-html\" id=\"" + Regex.Escape(block.Id) + "\">" +
                "<div class=\"custom-html-scroll\"[^>]*>"),
            body);

        var wrapper = TagOf(body, "div", "custom-html-scroll");
        Assert.Contains("tabindex=\"0\"", wrapper, StringComparison.Ordinal);
        Assert.Contains("role=\"region\"", wrapper, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Custom HTML\"", wrapper, StringComparison.Ordinal);

        // Anchor-invisible, and the author's HTML is still live and verbatim inside it.
        Assert.DoesNotContain("id=\"", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("data-anchor", wrapper, StringComparison.Ordinal);
        Assert.Contains("<table><tr><td>", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The containment that used to sit on the raw <c>&lt;table&gt;</c> itself
    /// (<c>display: block; overflow-x: auto</c>) is GONE: it was a clip box that could carry neither a gutter
    /// nor a tab stop, so it hid columns exactly the way #68's table did. Leaving it in place alongside the
    /// wrapper would recreate the defect one level down — a clipping box inside a scrolling one.
    /// </summary>
    [Fact]
    public void Stylesheet_NoLongerClipsTheRawTableInsideCustomHtml()
    {
        Assert.DoesNotContain(".custom-html table", Rules(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The bundled stylesheet with its <c>/* … */</c> comments stripped — i.e. what the browser actually
    /// applies. charter.css is heavily commented (deliberately: every rule here records the defect it
    /// prevents), and those comments NAME the properties and selectors these tests search for, so scanning
    /// the raw text finds prose and calls it a declaration.
    /// </summary>
    private static string Rules()
        => Regex.Replace(CharterStyles.Css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    // ---- the stylesheet half, and the OFFLINE artifact ------------------------------------------------------

    /// <summary>
    /// Every scroll region declares a SIZED <c>::-webkit-scrollbar</c>. That is what opts the box out of
    /// Chromium/WebKit's fading OVERLAY scrollbars — the 0px gutter #68 was reported against — so an
    /// overflowing region always shows a real, persistent bar. It costs a region that FITS nothing:
    /// <c>overflow-x: auto</c> draws no bar and takes no gutter when there is nothing to scroll.
    ///
    /// <para>Each region also gets a visible focus ring (it is a tab stop, so its focus stop has to be
    /// visible) and <c>overscroll-behavior-x: contain</c>, so a horizontal wheel/trackpad gesture that runs
    /// out of content cannot become browser back-navigation.</para>
    /// </summary>
    [Theory]
    [InlineData(".table-scroll")]
    [InlineData(".diff-scroll")]
    [InlineData(".custom-html-scroll")]
    [InlineData("pre:not(.mermaid)")]
    public void Stylesheet_EveryScrollRegion_DeclaresAPersistentGutterAndAFocusRing(string selector)
    {
        var css = Rules();

        Assert.Contains(selector + "::-webkit-scrollbar", css, StringComparison.Ordinal);
        Assert.Contains(selector + "::-webkit-scrollbar-thumb", css, StringComparison.Ordinal);
        Assert.Contains(selector + ":focus-visible", css, StringComparison.Ordinal);

        // The gutter is SIZED, not merely mentioned: an unsized ::-webkit-scrollbar leaves the platform's
        // fading overlay bar in place, which is the whole defect.
        Assert.Contains(selector, RuleDeclaring(css, "height: 10px;"), StringComparison.Ordinal);
        Assert.Contains(selector, RuleDeclaring(css, "overscroll-behavior-x: contain;"), StringComparison.Ordinal);

        // Firefox's mutually-exclusive branch covers the same set.
        Assert.Contains(selector, RuleDeclaring(css, "scrollbar-width: thin;"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The selector list of the (single) rule whose body declares <paramref name="declaration"/> — used to
    /// assert MEMBERSHIP of a grouped rule rather than the exact textual layout of the stylesheet, so the CSS
    /// can be reformatted without these tests becoming a pinned literal.
    /// </summary>
    private static string RuleDeclaring(string css, string declaration)
    {
        css = Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var at = css.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(at >= 0, "the stylesheet declares no '" + declaration + "'");

        var open = css.LastIndexOf('{', at);
        Assert.True(open >= 0, "'" + declaration + "' is not inside a rule");

        var previous = css.LastIndexOf('}', open);
        return css.Substring(previous + 1, open - previous - 1);
    }

    /// <summary>
    /// Firefox has no <c>::-webkit-scrollbar</c> and gets <c>scrollbar-width</c>/<c>scrollbar-color</c>
    /// instead — in a MUTUALLY-EXCLUSIVE <c>@supports not selector(::-webkit-scrollbar)</c> branch, because
    /// Chromium ignores <c>::-webkit-scrollbar</c> styling entirely once those standard properties are set on
    /// the same element. Declaring both unconditionally would cancel the gutter this whole fix rests on.
    /// </summary>
    [Fact]
    public void Stylesheet_TheFirefoxScrollbarBranchStaysMutuallyExclusive()
    {
        var css = Rules();

        Assert.Contains("@supports not selector(::-webkit-scrollbar)", css, StringComparison.Ordinal);

        // scrollbar-width/-color appear ONLY inside that branch: every occurrence must sit after the @supports
        // opener, and there must be exactly one opener.
        Assert.Single(Regex.Matches(css, Regex.Escape("@supports not selector(::-webkit-scrollbar)")));
        var branchStart = css.IndexOf("@supports not selector(::-webkit-scrollbar)", StringComparison.Ordinal);
        foreach (Match property in Regex.Matches(css, "scrollbar-(width|color)"))
        {
            Assert.True(
                property.Index > branchStart,
                "'" + property.Value + "' is declared outside the @supports branch, which cancels " +
                    "::-webkit-scrollbar styling in Chromium and removes the persistent gutter");
        }
    }

    /// <summary>
    /// Invariant 1: the saved/exported artifact carries NO SDK, so every one of these affordances has to be
    /// plain markup + CSS or it silently works only while <c>charter review</c> is running. Both halves —
    /// the region markup and the rules that scroll it — ship inline in the offline file.
    /// </summary>
    [Fact]
    public void Export_OfflineArtifact_ShipsEveryRegionAndTheRulesThatScrollThem()
    {
        var markdown =
            DiffMarkdown + "\n\n" + CodeMarkdown + "\n\n" + UnknownMarkdown + "\n\n" + CustomHtmlMarkdown;

        var exported = ArtifactExporter.Export(markdown, Path.GetTempPath());

        foreach (var region in new[] { "diff-scroll", "custom-html-scroll" })
        {
            Assert.Contains("class=\"" + region + "\" tabindex=\"0\" role=\"region\"", exported, StringComparison.Ordinal);
        }

        Assert.Contains("aria-label=\"Code block\"", exported, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Unknown directive body\"", exported, StringComparison.Ordinal);

        // The rules, inline in the artifact's own <style>.
        Assert.Contains(".diff-scroll::-webkit-scrollbar", exported, StringComparison.Ordinal);
        Assert.Contains("pre:not(.mermaid)::-webkit-scrollbar", exported, StringComparison.Ordinal);
        Assert.Contains("overscroll-behavior-x: contain;", exported, StringComparison.Ordinal);

        // …and no SDK, which is what makes the above the only thing holding it up.
        Assert.DoesNotContain("data-charter-sdk", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("CharterAnnotate", exported, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rendered <c>:::diagram</c> is deliberately EXCLUDED from all of it. Its oversize affordance is
    /// Charter #51's pan/zoom, which is SDK-only by design so the exported artifact stays byte-identical —
    /// a <c>pre</c> rule reaching <c>pre.mermaid</c> would put a review-time affordance into
    /// <c>charter.css</c> and break invariant 1 (which is what <c>DiagramPanZoomArtifactTests</c> guards).
    /// </summary>
    [Fact]
    public void Render_Diagram_GainsNoneOfTheScrollRegionChrome()
    {
        const string markdown = ":::diagram\ngraph LR\nA[One] --> B[Two]\n:::";

        var tag = TagOf(CharterRenderer.RenderBody(markdown), "pre", "mermaid");

        Assert.DoesNotContain("tabindex", tag, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role=", tag, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-", tag, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>&lt;table&gt;</c> path is untouched by #87 — #68's wrapper is still the one and only table
    /// scroll region, and it still carries the affordance rather than the table.
    /// </summary>
    [Fact]
    public void Render_Table_StillUsesTheUnchangedTableScrollWrapper()
    {
        const string markdown = "| A | B |\n| --- | --- |\n| 1 | 2 |";
        var block = BlockDocument.Parse(markdown).Blocks[0];
        var body = CharterRenderer.RenderBody(markdown);

        Assert.Contains(
            "<div class=\"table-scroll\" tabindex=\"0\" role=\"region\" aria-label=\"Table\">",
            body,
            StringComparison.Ordinal);
        Assert.Contains("<table id=\"" + block.Id + "\">", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The first <c>&lt;{tag} …&gt;</c> opening tag, attributes included — optionally the first one carrying
    /// <paramref name="cssClass"/>. Scoped to an opening TAG rather than the whole body on purpose: a rendered
    /// document containing a <c>:::diagram</c> also carries the 3.5MB vendored Mermaid blob, which contains
    /// ordinary English tokens (<c>tabindex</c> among them), so a whole-body scan measures the third-party
    /// bytes rather than Charter's markup.
    /// </summary>
    private static string TagOf(string body, string tag, string? cssClass)
    {
        var pattern = cssClass is null
            ? "<" + tag + "(?![a-z])[^>]*>"
            : "<" + tag + " class=\"" + Regex.Escape(cssClass) + "\"[^>]*>";
        var match = Regex.Match(body, pattern);
        Assert.True(
            match.Success,
            "the renderer emitted no <" + tag + (cssClass is null ? string.Empty : " class=\"" + cssClass + "\"") +
                "> element:\n" + body);
        return match.Value;
    }
}
