using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #48 / C1 — a RESOLVED <c>:::question</c> must RENDER as resolved.
///
/// Before the fix, <c>Answer</c> appeared in <see cref="QuestionSpec"/>, <c>QuestionResolution</c> and
/// <see cref="HandoffMarkdown"/> — and NOWHERE in the renderer. So a question carrying a non-empty inline
/// <c>answer</c> rendered as an unchecked control group byte-identical to an open one: the review artifact
/// could not show the human which decisions were already settled, and a second review round re-asked every
/// answered question, looking like the loop had lost their work.
///
/// These pin the fixed contract:
/// <list type="number">
///   <item><description>every mode pre-selects its chosen value(s) — <c>checked</c> on the matching
///     radio/checkbox, the value on a textarea/number input, the matching bool radio;</description></item>
///   <item><description>the resolved block is marked for BOTH audiences — <c>class="question answered"</c> +
///     <c>data-answered="true"</c> (machine) and an "Answered" status in the legend (human);</description></item>
///   <item><description>an OPEN question renders EXACTLY as before (byte-identical), so nothing about the
///     unanswered surface regressed;</description></item>
///   <item><description>every echoed answer value is HTML-escaped — reviewer-supplied answer text is an
///     injection surface;</description></item>
///   <item><description>an answer value matching no declared option is SURFACED as a checked write-in, never
///     silently dropped.</description></item>
/// </list>
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","AnsweredQuestionRender")].
/// </summary>
[Trait("Category", "AnsweredQuestionRender")]
public class AnsweredQuestionRenderTests
{
    [Fact]
    public void Render_AnsweredSingleSelect_ChecksTheChosenRadio_AndMarksTheBlockResolved()
    {
        var html = CharterRenderer.Render(Question(
            "single", "\"options\": [\"Postgres\", \"DynamoDB\"], \"answer\": [\"Postgres\"]"));

        // The chosen option is pre-selected; the rejected one is not.
        Assert.Contains("value=\"Postgres\" checked />", html, StringComparison.Ordinal);
        Assert.Contains("value=\"DynamoDB\" />", html, StringComparison.Ordinal);
        Assert.Equal(1, Count(html, " checked "));

        // Marked resolved for a machine AND for a human.
        Assert.Contains("class=\"question answered\"", html, StringComparison.Ordinal);
        Assert.Contains("data-answered=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"question-status\">Answered</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_AnsweredMultiSelect_ChecksEveryChosenValue()
    {
        var html = CharterRenderer.Render(Question(
            "multi",
            "\"options\": [\"us-east-1\", \"eu-west-1\", \"ap-south-1\"], "
                + "\"answer\": [\"us-east-1\", \"eu-west-1\"]"));

        // BOTH selected values are checked, and the unselected third is not — a multi answer is several
        // elements, so checking only the first would silently drop the rest of the decision.
        Assert.Contains("value=\"us-east-1\" checked />", html, StringComparison.Ordinal);
        Assert.Contains("value=\"eu-west-1\" checked />", html, StringComparison.Ordinal);
        Assert.Contains("value=\"ap-south-1\" />", html, StringComparison.Ordinal);
        Assert.Equal(2, Count(html, " checked "));
        Assert.Contains("data-answered=\"true\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_AnsweredFreeText_PutsTheAnswerInTheTextarea()
    {
        var html = CharterRenderer.Render(Question(
            "free-text", "\"answer\": [\"Keep the read path Postgres-only for v1.\"]"));

        Assert.Contains(
            "<textarea name=\"answer\">Keep the read path Postgres-only for v1.</textarea>",
            html,
            StringComparison.Ordinal);
        Assert.Contains("data-answered=\"true\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_AnsweredNumber_PutsTheAnswerInTheValueAttribute()
    {
        var html = CharterRenderer.Render(Question("number", "\"answer\": [\"3\"]"));

        Assert.Contains("<input type=\"number\" name=\"answer\" value=\"3\" />", html, StringComparison.Ordinal);
        Assert.Contains("data-answered=\"true\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true", "value=\"true\" checked />", "value=\"false\" />")]
    [InlineData("false", "value=\"false\" checked />", "value=\"true\" />")]
    public void Render_AnsweredBool_ChecksTheChosenYesNoRadio(string answer, string chosen, string rejected)
    {
        // Charter #43 pinned bool as TWO radios so Yes / No / unanswered stay distinguishable; a RESOLVED bool
        // must pre-check the one the human picked — including an explicit "No", which is a real decision.
        var html = CharterRenderer.Render(Question("bool", $"\"answer\": [\"{answer}\"]"));

        Assert.Contains(chosen, html, StringComparison.Ordinal);
        Assert.Contains(rejected, html, StringComparison.Ordinal);
        Assert.Equal(1, Count(html, " checked "));
        Assert.Contains("class=\"question answered\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("single", "\"options\": [\"A\", \"B\"]")]
    [InlineData("multi", "\"options\": [\"A\", \"B\"]")]
    [InlineData("free-text", null)]
    [InlineData("number", null)]
    [InlineData("bool", null)]
    public void Render_OpenQuestion_IsUnchanged_NoCheckedNoAnsweredMarker(string mode, string? options)
    {
        // The complement, per mode: the answered-state rendering must not leak into an OPEN question. Nothing
        // pre-selected, no resolved marker, and the plain `class="question"` root the SDK and charter.css
        // already bind to. Asserted on the FORM markup, not the whole document — the document also inlines the
        // stylesheet, which legitimately mentions the answered selectors.
        var form = FormMarkup(CharterRenderer.Render(Question(mode, options)));

        Assert.DoesNotContain("checked", form, StringComparison.Ordinal);
        Assert.DoesNotContain("data-answered", form, StringComparison.Ordinal);
        Assert.DoesNotContain("question-status", form, StringComparison.Ordinal);
        Assert.DoesNotContain("write-in", form, StringComparison.Ordinal);
        Assert.StartsWith("<form class=\"question\" id=\"", form, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_OpenQuestion_EmitsTheExactExpectedMarkup()
    {
        // The strongest statement of "the open-question surface is exactly what we think it is": the whole
        // form is pinned byte for byte. Any stray attribute, class, or badge leaking onto the open path fails
        // here.
        //
        // DELIBERATELY REBASED at Charter #56 (P0). This literal used to be the markup the renderer emitted
        // BEFORE answered questions were rendered — but that markup carried NO submit control, so it could
        // never have been the correct target: pinning it was pinning a form a human could not submit. The
        // baseline now includes the `<button type="submit">` and the `data-question-mode` the SDK reads.
        // What the test was ACTUALLY protecting — that rendering an answered question does not perturb the
        // OPEN questions around it — is not weakened; it is asserted directly, and independently of any
        // literal, by Render_AnsweringOneQuestion_LeavesTheOtherQuestionsMarkupUntouched below.
        const string markdown = ":::question\n"
            + "{ \"id\": \"q-single\", \"title\": \"A single question\", \"mode\": \"single\", "
            + "\"target\": \"human\", \"options\": [\"A\", \"B\"] }\n"
            + ":::";
        var blockId = BlockDocument.Parse(markdown).Blocks[0].Id;

        var expected =
            $"<form class=\"question\" id=\"{blockId}\" data-question-id=\"q-single\" data-question-mode=\"single\">\n"
            + "<input type=\"hidden\" name=\"question-id\" value=\"q-single\" />\n"
            + "<fieldset><legend>A single question</legend>\n"
            + "<label><input type=\"radio\" name=\"answer\" value=\"A\" /> A</label>\n"
            + "<label><input type=\"radio\" name=\"answer\" value=\"B\" /> B</label>\n"
            // #109 - the renderer always appends a free-text escape hatch to a select. Emitted HERE
            // rather than authored into `options`, so the option list handed to Guardrails stays
            // exactly the choices the agent proposed.
            + "<label class=\"charter-answer-other\"><input type=\"radio\" name=\"answer\" value=\"\" data-answer-other=\"1\" /> Something else: <input type=\"text\" class=\"charter-answer-other-text\" data-answer-other-text=\"1\" placeholder=\"your answer\" /></label>\n"
            + "<div class=\"question-actions\">"
            + "<button type=\"submit\" class=\"question-submit\" disabled>Save answer</button></div>\n"
            + "</fieldset>\n"
            + "</form>";

        Assert.Equal(expected, FormMarkup(CharterRenderer.Render(markdown)));
    }

    [Fact]
    public void Render_AnsweringOneQuestion_LeavesTheOtherQuestionsMarkupUntouched()
    {
        // The invariant the byte-identical baseline above existed to protect, stated DIRECTLY and without a
        // pinned literal: resolving one question must not perturb its neighbours' markup. Asserted by
        // rendering the same open question twice — once beside an unanswered sibling, once beside a RESOLVED
        // one — and requiring the two renderings to be byte-identical. This survives any future change to the
        // question markup, which a hard-coded literal cannot.
        const string sibling = ":::question\n"
            + "{{ \"id\": \"q-neighbour\", \"title\": \"The neighbour\", \"mode\": \"single\", "
            + "\"target\": \"human\", \"options\": [\"X\", \"Y\"]{0} }}\n"
            + ":::";
        const string subject = "\n\n:::question\n"
            + "{ \"id\": \"q-subject\", \"title\": \"The subject\", \"mode\": \"multi\", "
            + "\"target\": \"human\", \"options\": [\"P\", \"Q\"] }\n"
            + ":::";

        var besideOpen = SecondFormMarkup(
            CharterRenderer.Render(string.Format(sibling, string.Empty) + subject));
        var besideAnswered = SecondFormMarkup(
            CharterRenderer.Render(string.Format(sibling, ", \"answer\": [\"X\"]") + subject));

        Assert.Contains("q-subject", besideOpen, StringComparison.Ordinal);
        Assert.Equal(besideOpen, besideAnswered);
    }

    [Fact]
    public void Render_AnsweredFreeText_EscapesScriptAndQuotesInTheAnswer()
    {
        // Answers are FREE TEXT supplied by the reviewer and echoed into HTML — an injection surface. The
        // script tag must survive only as escaped text, and the quote must never close the enclosing markup.
        const string hostile = "<script>alert('xss')</script> and a \\\" quote";
        var html = CharterRenderer.Render(Question("free-text", $"\"answer\": [\"{hostile}\"]"));

        // The whole document's ONLY <script> would be the Mermaid runtime (absent here) — the answer's is escaped.
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert('xss')&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&quot; quote", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_AnsweredSingleSelect_EscapesAHostileOptionValueAndItsAnswer()
    {
        // The same escaping contract on the attribute path: a chosen option carrying a quote must not break
        // out of value="…" — and it must still be recognized as chosen (matched on the raw value, not the
        // escaped one).
        const string hostile = "a\\\" onfocus=\\\"alert(1)";
        var html = CharterRenderer.Render(Question(
            "single", $"\"options\": [\"{hostile}\", \"safe\"], \"answer\": [\"{hostile}\"]"));

        Assert.DoesNotContain("onfocus=\"alert(1)\"", html, StringComparison.Ordinal);
        Assert.Contains("a&quot; onfocus=&quot;alert(1)", html, StringComparison.Ordinal);
        Assert.Equal(1, Count(html, " checked "));
    }

    [Fact]
    public void Render_AnswerNotInOptions_IsSurfacedAsACheckedWriteIn_NeverDropped()
    {
        // An option renamed/removed after the question was resolved leaves an answer that matches nothing. The
        // decision is real: it must stay VISIBLE rather than silently vanishing into an all-unchecked group.
        var html = CharterRenderer.Render(Question(
            "single", "\"options\": [\"Postgres\", \"DynamoDB\"], \"answer\": [\"CockroachDB\"]"));

        Assert.Contains("CockroachDB", html, StringComparison.Ordinal);
        Assert.Contains("class=\"write-in\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"CockroachDB\" checked />", html, StringComparison.Ordinal);

        // ...and it did not spuriously select one of the declared options.
        Assert.Contains("value=\"Postgres\" />", html, StringComparison.Ordinal);
        Assert.Contains("value=\"DynamoDB\" />", html, StringComparison.Ordinal);
        Assert.Equal(1, Count(html, " checked "));
    }

    [Fact]
    public void Render_AnsweredBoolWithAnUnknownValue_IsSurfacedAsAWriteIn()
    {
        // The bool flavour of the same rule: a stored answer that is neither "true" nor "false" is surfaced
        // beside the two Yes/No radios rather than rendering as an unanswered bool.
        var html = CharterRenderer.Render(Question("bool", "\"answer\": [\"maybe\"]"));

        Assert.Contains("value=\"maybe\" checked />", html, StringComparison.Ordinal);
        Assert.Contains("class=\"write-in\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"true\" />", html, StringComparison.Ordinal);
        Assert.Contains("value=\"false\" />", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A schema-valid <c>:::question</c> for <paramref name="mode"/>, with <paramref name="extra"/> spliced in
    /// as additional JSON fields (options and/or answer). Keeps every fixture above a one-liner.
    /// </summary>
    private static string Question(string mode, string? extra)
    {
        var tail = string.IsNullOrEmpty(extra) ? string.Empty : ", " + extra;
        return ":::question\n"
            + $"{{ \"id\": \"q-{mode}\", \"title\": \"A {mode} question\", \"mode\": \"{mode}\", "
            + $"\"target\": \"human\"{tail} }}\n"
            + ":::";
    }

    /// <summary>
    /// The rendered <c>&lt;form&gt;…&lt;/form&gt;</c> of the document's single <c>:::question</c>. The renderer
    /// emits a COMPLETE document (shell + inlined stylesheet), and that stylesheet names the answered-state
    /// selectors — so any "the open form contains no X" assertion must be made against the FORM, not the page.
    /// </summary>
    private static string FormMarkup(string html)
    {
        var start = html.IndexOf("<form", StringComparison.Ordinal);
        Assert.True(start >= 0, "the rendered document contained no <form>.");

        var end = html.IndexOf("</form>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, "the rendered <form> was never closed.");

        return html.Substring(start, end - start + "</form>".Length);
    }

    /// <summary>The SECOND rendered <c>&lt;form&gt;…&lt;/form&gt;</c> in <paramref name="html"/>.</summary>
    private static string SecondFormMarkup(string html)
    {
        var first = html.IndexOf("</form>", StringComparison.Ordinal);
        Assert.True(first >= 0, "the rendered document contained fewer than two <form> elements.");

        return FormMarkup(html.Substring(first + "</form>".Length));
    }

    /// <summary>Counts non-overlapping occurrences of <paramref name="needle"/>.</summary>
    private static int Count(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
