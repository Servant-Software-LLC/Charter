using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #238 — every declared option carries its 1-based position, so a reviewer can refer to one from
/// the "Something else" box without retyping its text.
///
/// <para>
/// The number is <b>calculated from position, never authored</b> — the same rule "Something else" itself
/// follows (#109), and for the same reason: anything in <c>options</c> is emitted verbatim into the
/// CommonMark a downstream tool consumes, so an authored number would arrive as though the agent had
/// proposed it.
/// </para>
/// <para>
/// It rides the LABEL only, exactly as <c>(Recommended)</c> does (#125). That is not tidiness: the option
/// text is also the SUBMITTED VALUE, <c>recommended</c> must match an option verbatim, and write-in
/// detection is the same ordinal comparison. A number inside the value breaks all three silently, which is
/// why the value assertions below exist rather than a single label check.
/// </para>
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","OptionPositionNumber")].
/// </summary>
[Trait("Category", "OptionPositionNumber")]
public class OptionPositionNumberTests
{
    private static string Question(string extra = "") =>
        "# Plan\n\n:::question\n" +
        "{\"id\":\"cache\",\"title\":\"Which cache?\",\"mode\":\"single\"," +
        "\"options\":[\"Redis\",\"in-memory\",\"Memcached\"],\"target\":\"human\"" + extra + "}\n" +
        ":::\n";

    [Fact]
    public void EveryOptionIsNumberedFromOneInDocumentOrder()
    {
        var html = CharterRenderer.Render(Question());

        Assert.Contains("1. Redis", html, StringComparison.Ordinal);
        Assert.Contains("2. in-memory", html, StringComparison.Ordinal);
        Assert.Contains("3. Memcached", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE assertion this feature turns on. The option's text is also its submitted value, so a number that
    /// leaked into it would be recorded as the reviewer's decision — <c>"1. Redis"</c> — and carried into the
    /// answer, the plan, the handoff and everything downstream.
    /// </summary>
    [Fact]
    public void TheNumberNeverReachesTheSubmittedValue()
    {
        var html = CharterRenderer.Render(Question());

        Assert.Contains("value=\"Redis\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"in-memory\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"Memcached\"", html, StringComparison.Ordinal);

        Assert.DoesNotContain("value=\"1. Redis\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"2. in-memory\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"3. Memcached\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The number and the lean stack on one label without either becoming the value — the two display-only
    /// markers meeting on the same option is the case a naive concatenation gets wrong.
    /// </summary>
    [Fact]
    public void TheNumberComposesWithTheRecommendedMarker()
    {
        var html = CharterRenderer.Render(Question(",\"recommended\":\"Redis\""));

        Assert.Contains("1. Redis (Recommended)", html, StringComparison.Ordinal);
        Assert.Contains("value=\"Redis\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"1. Redis (Recommended)\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The recorded answer still pre-checks its option, which it can only do if the value comparison is
    /// untouched — the write-in machinery decides "this matches no declared option" the same way, so a number
    /// in the value would render a resolved answer as a stray write-in instead.
    /// </summary>
    [Fact]
    public void AResolvedAnswerStillMatchesItsNumberedOption()
    {
        var html = CharterRenderer.Render(Question(",\"answer\":[\"in-memory\"]"));

        Assert.Contains("value=\"in-memory\" checked", html, StringComparison.Ordinal);
        Assert.Contains("2. in-memory", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The handoff is a DIFFERENT surface and must not move. Its <c>options</c> list is consumed by the
    /// Guardrails breakdown gate, which was settled with that session on 2026-08-27, so a number appearing
    /// there would be a consumer break rather than a nicety. The reviewer's numbering is reproducible from
    /// this list because it is already document-ordered — which is exactly why nothing needs adding to it.
    /// </summary>
    [Fact]
    public void TheFlattenIsUnchanged()
    {
        var flat = HandoffMarkdown.Emit(Question());

        Assert.Contains(
            "options: `Redis`, `in-memory`, `Memcached`", flat, StringComparison.Ordinal);
        Assert.DoesNotContain("`1. Redis`", flat, StringComparison.Ordinal);
        Assert.DoesNotContain("1. Redis", flat, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>bool</c> renders Yes/No through the same choice writer but they are not <c>options</c>, and two
    /// named answers are not a list anybody indexes.
    /// </summary>
    [Fact]
    public void BoolAnswersAreNotNumbered()
    {
        var html = CharterRenderer.Render(
            "# Plan\n\n:::question\n" +
            "{\"id\":\"ship\",\"title\":\"Ship it?\",\"mode\":\"bool\",\"target\":\"human\"}\n" +
            ":::\n");

        Assert.Contains("Yes", html, StringComparison.Ordinal);
        Assert.DoesNotContain("1. Yes", html, StringComparison.Ordinal);
        Assert.DoesNotContain("2. No", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The escape hatch is not one of the author's choices, so it carries no position of its own — numbering
    /// it would imply the agent had proposed it, which is the thing #109 built it out of <c>options</c> to
    /// avoid.
    /// </summary>
    [Fact]
    public void TheWriteInHatchIsNotNumbered()
    {
        var html = CharterRenderer.Render(Question());

        Assert.Contains("Something else:", html, StringComparison.Ordinal);
        Assert.DoesNotContain("4. Something else", html, StringComparison.Ordinal);
    }
}
