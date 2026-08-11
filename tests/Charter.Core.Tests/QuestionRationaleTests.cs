using System.Linq;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// A question's reasoning, bound to the question (Charter #132).
/// <para>
/// Reported from a real review: an agent wrote its justification as prose beside the block, and the reviewer
/// read it as the introduction to the NEXT question further down the page — answering one question while
/// reading the argument for another. Both readings were available and nothing on the page distinguished them.
/// </para>
/// <para>
/// Adjacency cannot be enforced and, more to the point, cannot be PERCEIVED. A field can be: it renders
/// inside the box. Same argument that made <c>recommended</c> a field rather than a label convention (#125).
/// </para>
/// </summary>
[Trait("Category", "QuestionRationale")]
public class QuestionRationaleTests
{
    private const string Why =
        "Postgres is the cheapest option that still fixes the installed base.";

    private static string Body(string extra = "") =>
        "{\"id\":\"db\",\"title\":\"Which datastore?\",\"mode\":\"single\"," +
        "\"options\":[\"Postgres\",\"DynamoDB\"],\"target\":\"human\"" + extra + "}";

    private static string Plan(string extra = "") =>
        "# Plan\n\n:::question\n" + Body(extra) + "\n:::\n";

    /// <summary>
    /// Just the rendered question's form. The document inlines the stylesheet, so a whole-document search for
    /// a CSS class name finds the RULE that defines it — and every "does not contain" assertion becomes
    /// vacuously false while every ordering assertion measures the stylesheet instead of the markup.
    /// </summary>
    private static string Form(string html)
    {
        var start = html.IndexOf("<form class=\"question", System.StringComparison.Ordinal);
        var end = html.IndexOf("</form>", start, System.StringComparison.Ordinal);
        return html[start..end];
    }

    /// <summary>THE point: the reasoning renders INSIDE the question's form, not as a sibling block.</summary>
    [Fact]
    public void TheRationaleRendersInsideTheQuestionsOwnForm()
    {
        var html = CharterRenderer.Render(Plan(",\"rationale\":\"" + Why + "\""));

        var form = Form(html);

        Assert.Contains(Why, form, System.StringComparison.Ordinal);
        Assert.Contains("question-rationale", form, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Between the title and the controls: the reviewer reads what is being asked, then why, then chooses.
    /// Rendering it after the options would put the argument where a decision has already been made.
    /// </summary>
    [Fact]
    public void ItSitsBetweenTheTitleAndTheControls()
    {
        var html = CharterRenderer.Render(Plan(",\"rationale\":\"" + Why + "\""));

        var form = Form(html);
        var legend = form.IndexOf("</legend>", System.StringComparison.Ordinal);
        var rationale = form.IndexOf("question-rationale", System.StringComparison.Ordinal);
        var firstOption = form.IndexOf("value=\"Postgres\"", System.StringComparison.Ordinal);

        Assert.True(legend < rationale, "the rationale must follow the question's title");
        Assert.True(rationale < firstOption, "the rationale must precede the options it argues about");
    }

    /// <summary>
    /// Rewriting an explanation does not change what was ASKED, so it must never stale an answer a human has
    /// already given — otherwise an agent could invalidate a decision by improving its own prose.
    /// </summary>
    [Fact]
    public void ChangingTheRationale_DoesNotStaleAnAnswerAlreadyGiven()
    {
        var none = QuestionSpec.Parse(Body());
        var explained = QuestionSpec.Parse(Body(",\"rationale\":\"" + Why + "\""));
        var rewritten = QuestionSpec.Parse(Body(",\"rationale\":\"An entirely different argument.\""));

        Assert.Equal(QuestionIdentity.Fingerprint(none), QuestionIdentity.Fingerprint(explained));
        Assert.Equal(QuestionIdentity.Fingerprint(none), QuestionIdentity.Fingerprint(rewritten));
    }

    /// <summary>It is echoed like every other value the author supplies — never as markup.</summary>
    [Fact]
    public void ItIsEscaped_NotRenderedAsMarkup()
    {
        var html = CharterRenderer.Render(
            Plan(",\"rationale\":\"weigh <b>cost</b> & latency\""));

        Assert.Contains("&lt;b&gt;cost&lt;/b&gt;", html, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<b>cost</b>", html, System.StringComparison.Ordinal);
    }

    /// <summary>A whitespace-only rationale is absent, not an empty box — that is what a template with the
    /// field left unfilled produces.</summary>
    [Theory]
    [InlineData(",\"rationale\":\"\"")]
    [InlineData(",\"rationale\":\"   \"")]
    public void AnEmptyRationale_RendersNothing(string extra)
    {
        Assert.Null(QuestionSpec.Parse(Body(extra)).Rationale);
        Assert.DoesNotContain("question-rationale", Form(CharterRenderer.Render(Plan(extra))), System.StringComparison.Ordinal);
    }

    [Fact]
    public void AQuestionWithoutOne_RendersExactlyAsBefore()
    {
        Assert.Null(QuestionSpec.Parse(Body()).Rationale);
        Assert.DoesNotContain("question-rationale", Form(CharterRenderer.Render(Plan())), System.StringComparison.Ordinal);
        Assert.True(QuestionSpec.Validate(Body(",\"rationale\":\"" + Why + "\"")).Ok);
    }

    /// <summary>
    /// The reasoning travels downstream. On a resolved question it is the sharper signal: read against
    /// <c>recommended</c>, an answer that went the other way shows not only which option the human rejected
    /// but the argument they rejected with it.
    /// </summary>
    [Fact]
    public void HandoffCarriesIt_ForBothOpenAndAnsweredQuestions()
    {
        var open = HandoffMarkdown.Emit(Plan(",\"rationale\":\"" + Why + "\""));
        Assert.Contains("_Why: " + Why + "_", open, System.StringComparison.Ordinal);

        var answered = HandoffMarkdown.Emit(
            Plan(",\"recommended\":\"Postgres\",\"rationale\":\"" + Why + "\",\"answer\":[\"DynamoDB\"]"));
        Assert.Contains("_Why: " + Why + "_", answered, System.StringComparison.Ordinal);
        Assert.Contains("recommended: `Postgres`", answered, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// An OPEN question is emitted inside a blockquote, so every one of its lines must carry the marker — a
    /// rationale that fell out of the quote would read as ordinary plan prose, which is the very confusion
    /// this field exists to end. And no emitted line may start with <c>:::</c> (invariant 5), whatever the
    /// author's text begins with.
    /// </summary>
    [Fact]
    public void InAnOpenQuestion_TheRationaleStaysInsideTheBlockquote_AndNeverOpensADirective()
    {
        var handoff = HandoffMarkdown.Emit(
            Plan(",\"rationale\":\"::: this begins like a directive\\nand runs onto a second line\""));

        var lines = handoff.Replace("\r\n", "\n").Split('\n');
        var why = lines.Single(l => l.Contains("_Why:", System.StringComparison.Ordinal));

        Assert.StartsWith("> ", why, System.StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.StartsWith(":::", System.StringComparison.Ordinal));

        // Collapsed to one line, so the tail cannot escape the quote.
        Assert.Contains("runs onto a second line", why, System.StringComparison.Ordinal);
    }

    /// <summary>Applying an answer must not drop it — the resolution kernel splices one key rather than
    /// re-serializing, which is what makes every other body key survive.</summary>
    [Fact]
    public void ResolvingAnAnswer_PreservesTheRationale()
    {
        var applied = QuestionResolution.Apply(
            Plan(",\"rationale\":\"" + Why + "\""),
            new System.Collections.Generic.Dictionary<string, IReadOnlyList<string>>
            {
                ["db"] = new[] { "DynamoDB" },
            });

        Assert.Contains(Why, applied, System.StringComparison.Ordinal);
        Assert.Contains(Why, CharterRenderer.Render(applied), System.StringComparison.Ordinal);
    }
}
