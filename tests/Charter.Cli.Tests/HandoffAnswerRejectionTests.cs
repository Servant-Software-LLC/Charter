using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// What <c>charter handoff</c> DOES when an <c>--answers</c> entry is rejected (Charter #186), through the
/// REAL binary — because the whole decision is an exit code plus what is on disk when it is returned, and
/// neither is observable in-proc.
/// </summary>
/// <remarks>
/// <para>
/// <b>The decision: exit 1, and NOTHING is written.</b> An answers file naming a value the plan's own schema
/// forbids is not a plan defect, it is a bad INVOCATION — the same class as the unparseable-JSON answers file
/// that has always exited 1 here. Drawing the line at "is it syntactically JSON" would be arbitrary.
/// </para>
/// <para>
/// It is deliberately NOT the escalation code. Every exit 2 in this pipeline means <i>the output exists, go
/// read it</i>, and every "write it anyway" variant of this rule produces a <c>plan.md</c> that silently
/// differs from the resolution the caller asked for, with the difference living only on stderr — which is the
/// out-of-band-signalling failure the in-band provenance stamp exists to fight.
/// </para>
/// <para>
/// The residual hazard is stated rather than hidden: a refusal leaves a PREVIOUS run's <c>plan.md</c> in
/// place, and its <c>plan-sha256</c> stamp cannot distinguish it (same plan, different answers file). Closing
/// that is Charter #187's <c>answersSha256</c>, not this.
/// </para>
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","HandoffAnswerRejection")].
/// </remarks>
[Trait("Category", "HandoffAnswerRejection")]
public class HandoffAnswerRejectionTests : IDisposable
{
    private const string AnsweredHumanQuestion =
        "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
        + "{\"id\": \"db\", \"title\": \"Which database?\", \"mode\": \"single\", \"target\": \"human\", "
        + "\"options\": [\"Postgres\", \"MySQL\"], \"answer\": [\"Postgres\"]}\n:::\n";

    private readonly string _dir = CharterCliRunner.NewTempDirectory();

    public void Dispose() => CharterCliRunner.TryDeleteDirectory(_dir);

    [Fact]
    public void AValueThatIsNotAnOption_ExitsOneAndWritesNothing()
    {
        var (exit, _, stderr) = Run(AnsweredHumanQuestion, "{\"db\": [\"Cassandra\"]}");

        Assert.Equal(1, exit);
        Assert.False(File.Exists(Out()), "a rejected answers file must not produce a handoff at all.");
        Assert.Contains("Cassandra", stderr, StringComparison.Ordinal);
        Assert.Contains("'db'", stderr, StringComparison.Ordinal);
        Assert.Contains("NOTHING was written", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyValue_ExitsOne_RatherThanErasingTheRecordedDecision()
    {
        var (exit, _, stderr) = Run(AnsweredHumanQuestion, "{\"db\": []}");

        Assert.Equal(1, exit);
        Assert.False(File.Exists(Out()));
        Assert.Contains("omit", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AJsonNullValue_IsTheSameRejectionAsAnEmptyArray()
    {
        // ReadAnswers maps JSON null to an empty array, so the two spellings must not diverge here.
        var (exit, _, _) = Run(AnsweredHumanQuestion, "{\"db\": null}");

        Assert.Equal(1, exit);
        Assert.False(File.Exists(Out()));
    }

    [Fact]
    public void ABlankValue_ExitsOne_RatherThanCertifyingABlankDecision()
    {
        // Charter #188's shape reaching the verb: `{"db": [""]}` used to emit `Answered:` with nothing after
        // it AND exit 0 even under --fail-if-needs-human.
        var (exit, _, _) = Run(AnsweredHumanQuestion, "{\"db\": [\"\"]}", "--fail-if-needs-human");

        Assert.Equal(1, exit);
        Assert.False(File.Exists(Out()));
    }

    [Fact]
    public void OverridingTheRecordedDecisionWithAnotherValidOption_ExitsOne()
    {
        // "MySQL" is a declared option; it is the REPLACEMENT that is refused. The message names the decision
        // that is already on record, so the caller can either match it or drop the id.
        var (exit, _, stderr) = Run(AnsweredHumanQuestion, "{\"db\": [\"MySQL\"]}");

        Assert.Equal(1, exit);
        Assert.Contains("Postgres", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RestatingTheRecordedDecision_IsAcceptedAndWritesTheHandoff()
    {
        var (exit, _, _) = Run(AnsweredHumanQuestion, "{\"db\": [\"Postgres\"]}", "--fail-if-needs-human");

        Assert.Equal(0, exit);
        Assert.Contains("Answered: Postgres", File.ReadAllText(Out()), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRejectionIsNamedInOneRun()
    {
        const string twoQuestions =
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
            + "{\"id\": \"db\", \"title\": \"Which database?\", \"mode\": \"single\", \"target\": \"human\", "
            + "\"options\": [\"Postgres\", \"MySQL\"]}\n:::\n\n:::question\n"
            + "{\"id\": \"cache\", \"title\": \"Which cache?\", \"mode\": \"single\", \"target\": \"human\", "
            + "\"options\": [\"Redis\", \"in-memory\"]}\n:::\n";

        var (exit, _, stderr) = Run(twoQuestions, "{\"db\": [\"Cassandra\"], \"cache\": [\"\"]}");

        Assert.Equal(1, exit);
        Assert.Contains("'db'", stderr, StringComparison.Ordinal);
        Assert.Contains("'cache'", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ACarriageReturnInAFreeTextAnswer_ExitsOneAndWritesNothing()
    {
        // Charter #202, through the real binary. A free-text question declares no options, so #186's
        // membership check has nothing to test against and every shape check passes -- which is precisely why
        // free-text is where a control character lands. The refusal is the same class as every other bad
        // --answers entry: exit 1, nothing written, named on stderr.
        const string openFreeText =
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
            + "{\"id\": \"why\", \"title\": \"Why this approach?\", \"mode\": \"free-text\", "
            + "\"target\": \"human\"}\n:::\n";

        var (exit, _, stderr) = Run(openFreeText, "{\"why\": [\"alpha\\rbeta\"]}");

        Assert.Equal(1, exit);
        Assert.False(File.Exists(Out()), "a bare CR must never reach the handed-off document.");
        Assert.Contains("'why'", stderr, StringComparison.Ordinal);
        Assert.Contains("U+000D", stderr, StringComparison.Ordinal);

        // And the reason itself must be printable: echoing the raw CR would tear the very line explaining it.
        Assert.DoesNotContain('\r', stderr.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void AMultiLineFreeTextAnswer_IsAcceptedAndFlattensIntact()
    {
        // The boundary Charter #202 draws. LF is the one line break an answer may carry: the review page gives
        // a free-text question a <textarea>, and Emit joins blocks with a blank line so the extra newline can
        // never carry a reader out of the question it belongs to.
        const string openFreeText =
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
            + "{\"id\": \"why\", \"title\": \"Why this approach?\", \"mode\": \"free-text\", "
            + "\"target\": \"human\"}\n:::\n";

        var (exit, _, _) = Run(openFreeText, "{\"why\": [\"one\\ntwo\"]}", "--fail-if-needs-human");

        Assert.Equal(0, exit);
        Assert.Contains("Answered: one\ntwo", File.ReadAllText(Out()), StringComparison.Ordinal);
    }

    [Fact]
    public void HandoffHelp_SaysWhatMakesAnAnswersFileUnusable()
    {
        var (_, stdout, _) = CharterCliRunner.Run("handoff", "--help");

        Assert.Contains("--answers", stdout, StringComparison.Ordinal);
        Assert.Contains("rejected", stdout, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private string Out() => Path.Combine(_dir, "plan.md");

    private (int ExitCode, string StdOut, string StdErr) Run(
        string plan, string answersJson, params string[] extraArgs)
    {
        var planPath = Path.Combine(_dir, "plan.charter.md");
        File.WriteAllText(planPath, plan);

        var answersPath = Path.Combine(_dir, "answers.json");
        File.WriteAllText(answersPath, answersJson);

        var args = new List<string> { "handoff", planPath, "-o", Out(), "--answers", answersPath };
        args.AddRange(extraArgs);
        return CharterCliRunner.Run(args.ToArray());
    }
}
