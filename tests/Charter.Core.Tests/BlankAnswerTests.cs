using System.Text.Json;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// A blank value is not a decision (Charter #188). <c>HeadlessQuestion.Answered</c> was
/// <c>Answer.Count &gt; 0</c> — it counted ELEMENTS, not content — so <c>[""]</c> reported the question
/// answered and the flatten emitted <c>**Q: …** — Answered:</c> with nothing after it. That is exactly what a
/// bad <c>jq</c> in a machine-generated answers file produces, and it is the shape an unattended pipeline is
/// most likely to generate by accident.
/// </summary>
/// <remarks>
/// The field is read in three places and all three move together here, because a predicate that disagrees with
/// itself across the record, the page and the flatten is the same class of defect one level up: the forensic
/// record (<c>answered</c>), the renderer's resolved-question display, and the flattened plan's
/// Answered/Open branch.
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","BlankAnswer")].
/// </remarks>
[Trait("Category", "BlankAnswer")]
public class BlankAnswerTests
{
    private const string BlankInlineAnswer =
        "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
        + "{\"id\": \"db\", \"title\": \"Which database?\", \"mode\": \"single\", \"target\": \"human\", "
        + "\"options\": [\"Postgres\", \"MySQL\"], \"answer\": [\"\"]}\n:::\n";

    private const string OpenHumanQuestion =
        "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
        + "{\"id\": \"db\", \"title\": \"Which database?\", \"mode\": \"single\", \"target\": \"human\", "
        + "\"options\": [\"Postgres\", \"MySQL\"]}\n:::\n";

    [Fact]
    public void TheRecord_DoesNotCallABlankValueAnswered()
    {
        var question = Assert.Single(PlanInventory.Build(BlankInlineAnswer).Questions);

        Assert.False(question.Answered);
    }

    [Fact]
    public void TheRecord_EscalatesABlankAnswerOnAHumanQuestion()
    {
        // `needsHuman` reads `Answered`, so the whole point of the fix is that this escalates rather than
        // certifying a decision nobody made.
        var record = HeadlessRecord.Build(BlankInlineAnswer, "p.charter.md", "p.charter.html", "0.0.0-test");

        Assert.True(record.NeedsHuman);
        Assert.False(
            JsonDocument.Parse(record.ToJson()).RootElement
                .GetProperty("questions")[0].GetProperty("answered").GetBoolean());
    }

    [Fact]
    public void TheFlatten_EmitsAnOpenQuestionRatherThanAnAnsweredLineWithNothingAfterIt()
    {
        var output = HandoffMarkdown.Emit(BlankInlineAnswer, answers: null);

        Assert.Contains("Open question (unresolved)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Answered:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStrictGate_BlocksABlankAnswerSuppliedByAnAnswersFile()
    {
        // The composition that #188 is actually about: `--fail-if-needs-human` shipped reading the same
        // predicate, so `{"db": [""]}` certified a blank decision as a made one and exited 0.
        var answers = new Dictionary<string, IReadOnlyList<string>> { ["db"] = new[] { string.Empty } };

        var gate = HandoffGate.Evaluate(OpenHumanQuestion, answers);

        Assert.True(gate.NeedsHuman);
    }

    [Fact]
    public void TheRecord_TheGate_AndTheFlatten_GiveONEAnswerForOnePlan()
    {
        // The scoping trap this fix had to avoid, stated as a test. "Answered" was THREE independent
        // implementations, not one definition -- the record's `Answer.Count > 0`, the gate's own
        // `ResolvedAnswer(...).Count > 0`, and the flatten's own `resolved.Count > 0` -- so narrowing only the
        // property the issue names would have produced, on this exact input: a record saying `answered: false`,
        // a gate raising nothing and exiting 0 even under --fail-if-needs-human, and a handoff emitting
        // "Answered:" with an empty tail. Three artifacts, three answers, one plan -- which is Charter #187's
        // own reproduced defect, recreated by the fix for #188.
        //
        // They agree because all three read AnswerRules.IsDecision. Any future re-fork fails HERE, however it
        // is spelled, because this asserts the three VERDICTS rather than that they call the same method.
        var answers = new Dictionary<string, IReadOnlyList<string>> { ["db"] = new[] { string.Empty } };

        Assert.False(PlanInventory.Build(OpenHumanQuestion).Questions[0].Answered);
        Assert.True(HandoffGate.Evaluate(OpenHumanQuestion, answers).NeedsHuman);
        Assert.DoesNotContain(
            "Answered:", HandoffMarkdown.Emit(OpenHumanQuestion, answers), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRenderedPage_DoesNotShowABlankAnswerAsAnswered()
    {
        // A reviewer opening the page must not be told a decision was made. `data-answered` is the machine
        // half of the same claim and moves with it.
        var html = CharterRenderer.Render(BlankInlineAnswer);

        Assert.DoesNotContain("data-answered=\"true\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"question-status\">Answered</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AWhitespaceOnlyValueIsBlankToo_AndSoIsOneBlankValueAmongRealOnes()
    {
        Assert.False(AnswerRules.IsDecision(new[] { "   " }));
        Assert.False(AnswerRules.IsDecision(Array.Empty<string>()));
        Assert.True(AnswerRules.IsDecision(new[] { "Postgres" }));

        // A multi answer carrying a blank element is a DEFECTIVE answer, not a partial one: the blank came
        // from the same generator as the real values, so nothing about it is more trustworthy than the [""]
        // case above.
        Assert.False(AnswerRules.IsDecision(new[] { "Postgres", "" }));
    }
}
