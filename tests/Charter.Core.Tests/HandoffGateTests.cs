using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// <c>charter handoff --fail-if-needs-human</c>'s predicate (Charter #172): what makes a flattened plan unsafe
/// to hand to an unattended breakdown, judged AFTER <c>--answers</c> is merged.
/// </summary>
/// <remarks>
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","HandoffGate")].
/// </remarks>
[Trait("Category", "HandoffGate")]
public class HandoffGateTests
{
    private static string Question(string body) => "# Plan\n\n:::question\n" + body + "\n:::\n";

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Answers(
        params (string Id, string[] Values)[] entries)
        => entries.ToDictionary(
            entry => entry.Id,
            entry => (IReadOnlyList<string>)entry.Values,
            StringComparer.Ordinal);

    [Fact]
    public void AnOpenHumanQuestion_Blocks_AndIsNamedWithItsIdTitleAndTarget()
    {
        var gate = HandoffGate.Evaluate(
            Question("{\"id\":\"db\",\"title\":\"Which database?\",\"mode\":\"single\","
                + "\"target\":\"human\",\"options\":[\"Postgres\",\"MySQL\"]}"),
            answers: null);

        Assert.True(gate.NeedsHuman);
        var blocker = Assert.Single(gate.Blockers);
        Assert.Equal(HandoffGate.UnansweredHumanQuestion, blocker.Kind);
        Assert.Equal("db", blocker.Id);
        Assert.Equal("Which database?", blocker.Title);
        Assert.Equal("human", blocker.Target);
        Assert.NotNull(blocker.SourceLine);
    }

    [Fact]
    public void TheSameQuestion_AnsweredByTheAnswersFile_DoesNotBlock()
    {
        // The whole reason the predicate could not simply reuse HeadlessRecord.NeedsHuman: that property is a
        // pure function of the plan TEXT and has no answers parameter, so it would report this plan as
        // needing a human on the very run whose answers file settled it.
        var gate = HandoffGate.Evaluate(
            Question("{\"id\":\"db\",\"title\":\"Which database?\",\"mode\":\"single\","
                + "\"target\":\"human\",\"options\":[\"Postgres\",\"MySQL\"]}"),
            Answers(("db", ["Postgres"])));

        Assert.False(gate.NeedsHuman);
        Assert.Empty(gate.Blockers);
    }

    [Fact]
    public void AnInlineAnsweredHumanQuestion_DoesNotBlock()
    {
        var gate = HandoffGate.Evaluate(
            Question("{\"id\":\"db\",\"title\":\"Which database?\",\"mode\":\"single\","
                + "\"target\":\"human\",\"options\":[\"Postgres\",\"MySQL\"],\"answer\":[\"Postgres\"]}"),
            answers: null);

        Assert.False(gate.NeedsHuman);
    }

    [Fact]
    public void ADecidableAgentQuestion_DoesNotBlock()
    {
        // The carve-out that survives: options give the delegate something to decide WITH.
        var gate = HandoffGate.Evaluate(
            Question("{\"id\":\"cache\",\"title\":\"Which cache?\",\"mode\":\"single\","
                + "\"target\":\"agent\",\"options\":[\"Redis\",\"in-memory\"]}"),
            answers: null);

        Assert.False(gate.NeedsHuman);
    }

    [Fact]
    public void AnUndecidableAgentQuestion_Blocks()
    {
        // The narrowing (Charter #172). A free-text agent question with no options and no lean is invisible to
        // BOTH of Charter's existing gates -- headless's NeedsHuman skips agent questions outright, and
        // FindQuestionsMissingRecommendation skips agent questions AND is scoped to select modes -- so Charter
        // would certify "no human needed" while the downstream invents an answer out of nothing.
        var gate = HandoffGate.Evaluate(
            Question("{\"id\":\"backoff\",\"title\":\"What retry backoff?\",\"mode\":\"free-text\","
                + "\"target\":\"agent\"}"),
            answers: null);

        Assert.True(gate.NeedsHuman);
        var blocker = Assert.Single(gate.Blockers);
        Assert.Equal(HandoffGate.UndecidableAgentQuestion, blocker.Kind);
        Assert.Equal("backoff", blocker.Id);
        Assert.Equal("agent", blocker.Target);
    }

    [Fact]
    public void AnUnparseableQuestionBody_Blocks()
    {
        // A trailing comma. It matters more here than anywhere else because of what the FLATTEN does with it:
        // the whole question collapses to "> **Malformed question (could not parse): …**", so its title, id
        // and target are deleted from the document handed downstream.
        const string plan = "# Plan\n\n:::question\n{\"id\":\"db\",\"title\":\"Which database?\",}\n:::\n";

        var gate = HandoffGate.Evaluate(plan, answers: null);

        Assert.True(gate.NeedsHuman);
        Assert.Contains(gate.Blockers, blocker => blocker.Kind == HandoffGate.MalformedQuestion);
        Assert.DoesNotContain("Which database?", HandoffMarkdown.Emit(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void AMisspelledQuestionDirective_Blocks()
    {
        // :::questoin classifies as an unknown directive, so under the RECORD's rule it raises nothing and
        // both verbs exit 0 -- on a hidden `target: human` decision. Charter cannot tell a typo'd question
        // from a container the catalog genuinely does not define, so the strict gate resolves toward the
        // human, the same way the anchor model orphans rather than misattributing.
        var gate = HandoffGate.Evaluate(
            "# Plan\n\n:::questoin\n{\"id\":\"db\",\"title\":\"Which database?\",\"mode\":\"single\","
                + "\"target\":\"human\",\"options\":[\"Postgres\",\"MySQL\"]}\n:::\n",
            answers: null);

        Assert.True(gate.NeedsHuman);
        var blocker = Assert.Single(gate.Blockers);
        Assert.Equal(HandoffGate.UnknownDirective, blocker.Kind);
    }

    [Fact]
    public void DuplicateQuestionIds_Block()
    {
        const string body = "{\"id\":\"db\",\"title\":\"Which database?\",\"mode\":\"single\","
            + "\"target\":\"human\",\"options\":[\"A\",\"B\"],\"answer\":[\"A\"]}";

        var gate = HandoffGate.Evaluate("# Plan\n\n:::question\n" + body + "\n:::\n\n:::question\n" + body + "\n:::\n", answers: null);

        Assert.True(gate.NeedsHuman);
        Assert.Contains(gate.Blockers, blocker => blocker.Kind == HandoffGate.DuplicateQuestionId && blocker.Id == "db");
    }

    [Fact]
    public void ACleanAnsweredPlan_Blocks_Nothing()
    {
        var gate = HandoffGate.Evaluate(
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\nJust prose.\n",
            answers: null);

        Assert.False(gate.NeedsHuman);
        Assert.Empty(gate.Blockers);
        Assert.Empty(gate.UnmatchedAnswerIds);
    }

    [Fact]
    public void AnswerIdsThatMatchNoQuestion_AreReported_ButDoNotBlock()
    {
        // "Your answers file had three ids and none of them matched" is a signal a pipeline needs -- a stale
        // id, a renamed question, or a generator written against a different plan all look identical today
        // because Charter discards them in silence. It is reported, not vetoed: the questions those ids failed
        // to answer already block on their own account, and a second veto here would be a rule nothing else
        // in the pipeline shares.
        var gate = HandoffGate.Evaluate(
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\nJust prose.\n",
            Answers(("stale-one", ["x"]), ("stale-two", ["y"])));

        Assert.False(gate.NeedsHuman);
        Assert.Equal(new[] { "stale-one", "stale-two" }, gate.UnmatchedAnswerIds);
    }

    [Fact]
    public void TheGateAndTheEmitter_AgreeOnWhatCountsAsAnswered()
    {
        // A gate that computed "answered" differently from the emitter would certify a document other than the
        // one written -- the exact failure the flag exists to prevent, one level up. Both read
        // HandoffGate.ResolvedAnswer, so this holds for the shapes that make the two rules differ: an answers
        // file OVERRIDING an inline answer, and an empty value RE-OPENING one (both Charter #186 behaviours,
        // preserved verbatim here rather than fixed).
        const string plan =
            "# Plan\n\n:::question\n{\"id\":\"db\",\"title\":\"Which database?\",\"mode\":\"single\","
            + "\"target\":\"human\",\"options\":[\"Postgres\",\"MySQL\"],\"answer\":[\"Postgres\"]}\n:::\n";

        var overridden = HandoffGate.Evaluate(plan, Answers(("db", ["MySQL"])));
        Assert.False(overridden.NeedsHuman);
        Assert.Contains("Answered: MySQL", HandoffMarkdown.Emit(plan, Answers(("db", ["MySQL"]))), StringComparison.Ordinal);

        var erased = HandoffGate.Evaluate(plan, Answers(("db", [])));
        Assert.True(erased.NeedsHuman);
        Assert.Contains("Open question (unresolved)", HandoffMarkdown.Emit(plan, Answers(("db", []))), StringComparison.Ordinal);
    }
}
