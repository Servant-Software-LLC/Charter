using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// The rules an out-of-band <c>--answers</c> entry must satisfy before it is allowed to settle a
/// <c>:::question</c> (Charter #186). Three decisions are pinned here, and each one exists because the
/// opposite behaviour shipped in 0.24.0:
/// <list type="number">
///   <item><description>a supplied value is checked against the question's <c>mode</c> and
///     <c>options</c>;</description></item>
///   <item><description>an empty or null value is a REJECTION, not an erasure — "no answer" is already
///     spelled by omitting the id;</description></item>
///   <item><description>an answers file may FILL an unanswered question and may re-state an answer the plan
///     already records, but it may never REPLACE one.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// The last one is the load-bearing decision. The living-document model's whole claim is that a resolved
/// question carries its answer inline and that answer is DURABLE; a channel that can quietly replace it makes
/// "durable" false. Recording the answer's source (Charter #187) would make an override auditable, not safe —
/// the flattened plan would still assert the overriding value and <c>plan-breakdown</c> would still read it as
/// settled, with the audit living in a side file the consumer may never open.
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","SuppliedAnswer")].
/// </remarks>
[Trait("Category", "SuppliedAnswer")]
public class SuppliedAnswerTests
{
    // The #186 fixture, verbatim: a human's review decision, recorded inline, on a select question.
    private const string AnsweredHumanQuestion =
        "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
        + "{\"id\": \"db\", \"title\": \"Which database?\", \"mode\": \"single\", \"target\": \"human\", "
        + "\"options\": [\"Postgres\", \"MySQL\"], \"answer\": [\"Postgres\"]}\n:::\n";

    // The same question with nobody's decision on it yet.
    private const string OpenHumanQuestion =
        "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
        + "{\"id\": \"db\", \"title\": \"Which database?\", \"mode\": \"single\", \"target\": \"human\", "
        + "\"options\": [\"Postgres\", \"MySQL\"]}\n:::\n";

    [Fact]
    public void AValueThatIsNotAnOption_NeverReachesTheFlattenedPlan()
    {
        // The headline repro. `--answers '{"db": ["Cassandra"]}'` emitted
        //   **Q: Which database?** - Answered: Cassandra
        //   _Question - id: `db`; ...; options: `Postgres`, `MySQL`_
        // asserting an answer that is not in its own options list, printed on the very next line.
        var output = HandoffMarkdown.Emit(AnsweredHumanQuestion, Answers(("db", ["Cassandra"])));

        Assert.DoesNotContain("Cassandra", output, StringComparison.Ordinal);
        Assert.Contains("Answered: Postgres", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueThatIsNotAnOption_IsRejectedNamingTheValueAndTheOptions()
    {
        var rejections = AnswerRules.CheckAll(AnsweredHumanQuestion, Answers(("db", ["Cassandra"])));

        var rejection = Assert.Single(rejections);
        Assert.Equal("db", rejection.QuestionId);
        Assert.Contains("Cassandra", rejection.Reason, StringComparison.Ordinal);
        Assert.Contains("Postgres", rejection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ASingleModeQuestion_RefusesMoreThanOneValue()
    {
        // "A `single`-mode question can also receive multiple values this way" (#186).
        var rejections = AnswerRules.CheckAll(OpenHumanQuestion, Answers(("db", ["Postgres", "MySQL"])));

        var rejection = Assert.Single(rejections);
        Assert.Contains("single", rejection.Reason, StringComparison.Ordinal);
        Assert.Contains("exactly one", rejection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyValue_DoesNotEraseARecordedDecision()
    {
        // `--answers '{"db": []}'` re-opened a question a human had settled, and the run continued. `null`
        // reaches Charter as the same empty array (ReadAnswers maps it), so this covers both spellings.
        var output = HandoffMarkdown.Emit(AnsweredHumanQuestion, Answers(("db", [])));

        Assert.Contains("Answered: Postgres", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Open question", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyValue_IsRejected_BecauseOmittingTheIdAlreadyMeansNoAnswer()
    {
        // Decision 2: an empty value has no honest meaning left. "This question was not answered here" is
        // already spelled by leaving the id out, so accepting `[]` as an erasure only ever lets a generator
        // delete a decision it could not make itself.
        var rejections = AnswerRules.CheckAll(AnsweredHumanQuestion, Answers(("db", [])));

        var rejection = Assert.Single(rejections);
        Assert.Contains("omit", rejection.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AValidValue_MayNotReplaceAnAnswerThePlanAlreadyRecords()
    {
        // Decision 3, and the one that costs the most. "MySQL" is a perfectly legal option -- it is the
        // REPLACEMENT that is refused, because the plan already carries a human's decision at this id.
        var rejections = AnswerRules.CheckAll(AnsweredHumanQuestion, Answers(("db", ["MySQL"])));

        var rejection = Assert.Single(rejections);
        Assert.Contains("Postgres", rejection.Reason, StringComparison.Ordinal);

        Assert.Contains(
            "Answered: Postgres",
            HandoffMarkdown.Emit(AnsweredHumanQuestion, Answers(("db", ["MySQL"]))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AValueIdenticalToTheInlineOne_IsAccepted_SoARepeatedAnswersFileKeepsWorking()
    {
        // The complement of the rule above, and it is what keeps the rule usable: a generator that re-supplies
        // its whole answer set on every run must not break the day one of those questions gets answered inline.
        // The rule is therefore "an answers file may only ADD information", which is monotone.
        Assert.Empty(AnswerRules.CheckAll(AnsweredHumanQuestion, Answers(("db", ["Postgres"]))));

        Assert.Contains(
            "Answered: Postgres",
            HandoffMarkdown.Emit(AnsweredHumanQuestion, Answers(("db", ["Postgres"]))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AValidValue_StillFillsAnUnansweredQuestion()
    {
        // The case `--answers` exists for, unchanged.
        Assert.Empty(AnswerRules.CheckAll(OpenHumanQuestion, Answers(("db", ["MySQL"]))));

        Assert.Contains(
            "Answered: MySQL",
            HandoffMarkdown.Emit(OpenHumanQuestion, Answers(("db", ["MySQL"]))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ARejectedAnswerOnAnOpenQuestion_BLOCKS_TheStrictGate()
    {
        // The composition rule with Charter #172: a question whose supplied answer is rejected must block the
        // gate, never pass it. It blocks because the rejected value never becomes the resolved answer, so the
        // question is still what it was -- open, routed to a human, with nobody there.
        var gate = HandoffGate.Evaluate(OpenHumanQuestion, Answers(("db", ["Cassandra"])));

        Assert.True(gate.NeedsHuman);
        Assert.Contains(gate.Blockers, blocker => blocker.Kind == HandoffGate.UnansweredHumanQuestion);
    }

    [Fact]
    public void ABoolQuestion_TakesOnlyTrueOrFalse()
    {
        const string plan =
            "# Plan\n\n:::question\n{\"id\":\"flag\",\"title\":\"Enable it?\",\"mode\":\"bool\","
            + "\"target\":\"human\"}\n:::\n";

        Assert.Empty(AnswerRules.CheckAll(plan, Answers(("flag", ["false"]))));
        Assert.Single(AnswerRules.CheckAll(plan, Answers(("flag", ["No"]))));
    }

    [Fact]
    public void ANumberQuestion_TakesOnlyANumber()
    {
        const string plan =
            "# Plan\n\n:::question\n{\"id\":\"replicas\",\"title\":\"How many?\",\"mode\":\"number\","
            + "\"target\":\"human\"}\n:::\n";

        Assert.Empty(AnswerRules.CheckAll(plan, Answers(("replicas", ["3"]))));
        Assert.Single(AnswerRules.CheckAll(plan, Answers(("replicas", ["three"]))));
    }

    [Fact]
    public void AFreeTextQuestion_CanOnlyBeCheckedForSHAPE_NeverForMEMBERSHIP()
    {
        // The asymmetry, named rather than left to be discovered. A free-text question declares no options, so
        // there is no set to test a value against: the only facts checkable are the ones its mode states -- one
        // value, and not a blank one. That is not a hole in the rule; the rule is "a supplied answer must be
        // something the question's DECLARED shape can accept", and free-text declares less shape than `single`.
        const string plan =
            "# Plan\n\n:::question\n{\"id\":\"backoff\",\"title\":\"What backoff?\",\"mode\":\"free-text\","
            + "\"target\":\"agent\"}\n:::\n";

        Assert.Empty(AnswerRules.CheckAll(plan, Answers(("backoff", ["exponential, capped at 30s"]))));

        Assert.Single(AnswerRules.CheckAll(plan, Answers(("backoff", ["  "]))));
        Assert.Single(AnswerRules.CheckAll(plan, Answers(("backoff", ["a", "b"]))));
    }

    [Fact]
    public void AnIdMatchingNoQuestion_IsStillNotARejection()
    {
        // Charter #172 §4.2: an unmatched id is REPORTED on stderr and never a veto. This rule must not turn
        // one into an exit code by the back door -- CheckAll judges only the questions the plan actually has.
        Assert.Empty(AnswerRules.CheckAll(OpenHumanQuestion, Answers(("gone", ["x"]))));
    }

    [Fact]
    public void EveryViolationIsNamed_NotJustTheFirst()
    {
        // A pipeline fixing an answers file one exit code at a time is a pipeline that runs many times to learn
        // what it could have learned once.
        const string plan =
            "# Plan\n\n:::question\n{\"id\":\"db\",\"title\":\"Which database?\",\"mode\":\"single\","
            + "\"target\":\"human\",\"options\":[\"Postgres\",\"MySQL\"]}\n:::\n\n"
            + ":::question\n{\"id\":\"cache\",\"title\":\"Which cache?\",\"mode\":\"single\","
            + "\"target\":\"human\",\"options\":[\"Redis\",\"in-memory\"]}\n:::\n";

        var rejections = AnswerRules.CheckAll(plan, Answers(("db", ["Cassandra"]), ("cache", [])));

        Assert.Equal(2, rejections.Count);
        Assert.Contains(rejections, r => r.QuestionId == "db");
        Assert.Contains(rejections, r => r.QuestionId == "cache");
    }

    private static Dictionary<string, IReadOnlyList<string>> Answers(
        params (string Id, string[] Values)[] entries)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (id, values) in entries)
        {
            map[id] = values;
        }

        return map;
    }
}
