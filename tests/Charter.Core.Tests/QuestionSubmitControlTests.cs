using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #56 / P0 — a rendered <c>:::question</c> must carry a real SUBMIT CONTROL.
///
/// Before the fix the renderer emitted the mode's inputs and NOTHING else: no <c>&lt;button&gt;</c>, no
/// <c>type="submit"</c> anywhere in the form. The SDK only listens for the <c>submit</c> event, and a form with
/// no submit control cannot be submitted by clicking a radio, by pressing Enter, or by Ctrl+Enter — the browser
/// fires no submit event at all. Only a scripted <c>form.requestSubmit()</c> worked, which is exactly what the
/// browser test did, so the suite was green while a HUMAN could not answer a single question.
///
/// These pin the fixed contract:
/// <list type="number">
///   <item><description>EVERY mode's form carries exactly one <c>&lt;button type="submit"&gt;</c> with visible
///     text, inside the form;</description></item>
///   <item><description>it ships <c>disabled</c> — the correct initial state of "nothing to submit yet" for an
///     open AND an answered question, and the guard that keeps the saved, SDK-free artifact from firing a
///     native form navigation. The SDK owns the enabled state from load onward;</description></item>
///   <item><description>the form declares its <c>mode</c> to the SDK (<c>data-question-mode</c>) so a
///     <c>bool</c> answer is collected and reported AS a bool rather than inferred as a
///     <c>single</c>;</description></item>
///   <item><description>the form still needs no Charter JS to DISPLAY (no <c>&lt;script&gt;</c>), and the
///     answered surface (chip, pre-selection) is untouched by the new control.</description></item>
/// </list>
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","QuestionSubmitControl")].
/// </summary>
[Trait("Category", "QuestionSubmitControl")]
public class QuestionSubmitControlTests
{
    [Theory]
    [InlineData("single", "\"options\": [\"A\", \"B\"]")]
    [InlineData("multi", "\"options\": [\"A\", \"B\"]")]
    [InlineData("free-text", null)]
    [InlineData("number", null)]
    [InlineData("bool", null)]
    public void Render_Question_EveryMode_EmitsExactlyOneSubmitButtonInsideTheForm(string mode, string? options)
    {
        // The headline P0 guard, per mode: without this the reviewer has no way to fire a submit event, so
        // the whole elicitation half of the review loop is unreachable from a browser.
        var form = FormMarkup(CharterRenderer.Render(Question(mode, options, answer: null)));

        Assert.Equal(1, Count(form, "type=\"submit\""));
        Assert.Contains("<button type=\"submit\"", form, StringComparison.Ordinal);
        Assert.Contains(">Save answer</button>", form, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("single", "\"options\": [\"A\", \"B\"]")]
    [InlineData("multi", "\"options\": [\"A\", \"B\"]")]
    [InlineData("free-text", null)]
    [InlineData("number", null)]
    [InlineData("bool", null)]
    public void Render_Question_SubmitButton_ShipsDisabled(string mode, string? options)
    {
        // "Nothing to submit yet" is the correct state the instant the page loads — for an OPEN question
        // (nothing chosen) and for an ANSWERED one (the recorded answer is already what is selected). It is
        // also what keeps the SDK-free saved artifact inert: a native submit there would navigate the page to
        // `?answer=…`, which is worse than no button at all. The SDK enables it the moment the reviewer's
        // answer DIFFERS from what the markup records.
        var form = FormMarkup(CharterRenderer.Render(Question(mode, options, answer: null)));

        Assert.Contains("<button type=\"submit\" class=\"question-submit\" disabled>", form, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("single", "single", "\"options\": [\"A\", \"B\"]")]
    [InlineData("multi", "multi", "\"options\": [\"A\", \"B\"]")]
    [InlineData("free-text", "free-text", null)]
    [InlineData("number", "number", null)]
    [InlineData("bool", "bool", null)]
    public void Render_Question_DeclaresItsModeToTheSdk(string mode, string token, string? options)
    {
        // The SDK's resolveMode() has always PREFERRED an explicit data-question-mode and only fallen back to
        // inferring the mode from the controls present — but the renderer never stamped it. Inference reads a
        // `bool` (two Yes/No radios since Charter #43) as a `single`, so a bool answer was collected by the
        // single branch and REPORTED to the agent with mode "single". Stamping the mode makes the SDK's bool
        // branch reachable and the drained `mode` truthful.
        var form = FormMarkup(CharterRenderer.Render(Question(mode, options, answer: null)));

        Assert.Contains($"data-question-mode=\"{token}\"", form, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_AnsweredQuestion_KeepsItsSubmitControl_SoADecisionCanBeRevised()
    {
        // A review round REVISES decisions: an already-answered question must still offer the control that
        // re-submits a changed answer. Rendering the answered state must not swallow the affordance.
        var html = CharterRenderer.Render(Question("single", "\"options\": [\"A\", \"B\"]", answer: "A"));
        var form = FormMarkup(html);

        Assert.Contains("class=\"question answered\"", form, StringComparison.Ordinal);
        Assert.Contains("data-answered=\"true\"", form, StringComparison.Ordinal);
        Assert.Contains("<span class=\"question-status\">Answered</span>", form, StringComparison.Ordinal);
        Assert.Contains("value=\"A\" checked />", form, StringComparison.Ordinal);

        // ...and the Save control is there, disabled, because nothing has CHANGED yet.
        Assert.Contains("<button type=\"submit\" class=\"question-submit\" disabled>", form, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Question_SubmitControl_NeedsNoScript()
    {
        // The submit control is plain HTML. The enabled/disabled rule and the POST are the SDK's job, injected
        // at serve time only — never inlined into the artifact (invariant 1).
        var html = CharterRenderer.Render(Question("single", "\"options\": [\"A\", \"B\"]", answer: null));

        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("onclick", html, StringComparison.Ordinal);
        Assert.DoesNotContain("onsubmit", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Question_SubmitControlIsStyledFromTheBundledTokens()
    {
        // The button must not read as unstyled browser chrome on the page: it carries the class the bundled
        // stylesheet styles, and that stylesheet — which the renderer inlines — actually styles it.
        var html = CharterRenderer.Render(Question("single", "\"options\": [\"A\", \"B\"]", answer: null));

        Assert.Contains("class=\"question-submit\"", html, StringComparison.Ordinal);
        Assert.Contains("form.question .question-submit", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A schema-valid <c>:::question</c> for <paramref name="mode"/>, with <paramref name="options"/> spliced in
    /// and an optional single-element <paramref name="answer"/> (the on-disk RESOLVED marker).
    /// </summary>
    private static string Question(string mode, string? options, string? answer)
    {
        var tail = string.IsNullOrEmpty(options) ? string.Empty : ", " + options;
        if (!string.IsNullOrEmpty(answer))
        {
            tail += $", \"answer\": [\"{answer}\"]";
        }

        return ":::question\n"
            + $"{{ \"id\": \"q-{mode}\", \"title\": \"A {mode} question\", \"mode\": \"{mode}\", "
            + $"\"target\": \"human\"{tail} }}\n"
            + ":::";
    }

    /// <summary>The rendered <c>&lt;form&gt;…&lt;/form&gt;</c> of the document's single <c>:::question</c>.</summary>
    private static string FormMarkup(string html)
    {
        var start = html.IndexOf("<form", StringComparison.Ordinal);
        Assert.True(start >= 0, "the rendered document contained no <form>.");

        var end = html.IndexOf("</form>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, "the rendered <form> was never closed.");

        return html.Substring(start, end - start + "</form>".Length);
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
