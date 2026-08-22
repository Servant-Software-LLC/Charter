using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// <c>charter handoff --fail-if-needs-human</c> through the REAL binary (Charter #172): the flag's whole
/// contract is an exit code plus what is on disk when it is returned, and neither is observable in-proc.
/// </summary>
/// <remarks>
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","HandoffStrictMode")].
/// </remarks>
[Trait("Category", "HandoffStrictMode")]
public class HandoffStrictModeTests : IDisposable
{
    private const string OpenHumanQuestion =
        "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
        + "{\"id\": \"db\", \"title\": \"Which database?\", \"mode\": \"single\", \"target\": \"human\", "
        + "\"options\": [\"Postgres\", \"MySQL\"], \"recommended\": \"Postgres\"}\n:::\n";

    private const string CleanPlan = "---\ncharter-format-version: 1\n---\n\n# Plan\n\nJust prose.\n";

    private readonly string _dir = CharterCliRunner.NewTempDirectory();

    public void Dispose() => CharterCliRunner.TryDeleteDirectory(_dir);

    [Fact]
    public void WithoutTheFlag_AnOpenHumanQuestionStillExitsZero()
    {
        // Default behaviour is unchanged. An attended workflow hands off a plan with open questions all the
        // time -- that is the case the flatten's "Open question (unresolved)" line exists for.
        var (exit, _, _) = Run(OpenHumanQuestion);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Out()));
    }

    [Fact]
    public void WithTheFlag_ItWritesTheHandoffANDExitsTwo()
    {
        // The load-bearing half. Every exit 2 in this pipeline means "the output exists, go read it"
        // (charter headless's 2, Guardrails' BreakdownCommand 2, Guardrails' ExitCodes.TaskFailed) and
        // inverting that at this seam is the exact sin the issue exists to prevent.
        var (exit, stdout, _) = Run(OpenHumanQuestion, "--fail-if-needs-human");

        Assert.Equal(2, exit);
        Assert.True(File.Exists(Out()), "the handoff must be written even when the gate escalates.");
        Assert.Contains("Handed off", stdout, StringComparison.Ordinal);
        Assert.Contains("Open question (unresolved)", File.ReadAllText(Out()), StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalWouldLeaveAStalePlan_WhichIsWhyItDoesNotRefuse()
    {
        // Written as a fact rather than as prose: a previous run's plan.md carries no open-question markers at
        // all, is internally consistent, passes any lint, and Guardrails' BreakdownCommand only checks the
        // file extension. So "fail closed" would hand a downstream a plan it cannot tell is stale.
        RunNamed(CleanPlan, "clean.charter.md");
        var firstHandoff = File.ReadAllText(Out());
        Assert.DoesNotContain("Open question", firstHandoff, StringComparison.Ordinal);

        var (exit, _, _) = Run(OpenHumanQuestion, "--fail-if-needs-human");

        Assert.Equal(2, exit);
        Assert.NotEqual(firstHandoff, File.ReadAllText(Out()));
        Assert.Contains("Open question (unresolved)", File.ReadAllText(Out()), StringComparison.Ordinal);
    }

    [Fact]
    public void StderrNamesEachBlockingQuestionsIdTitleAndTarget()
    {
        var (exit, _, stderr) = Run(OpenHumanQuestion, "--fail-if-needs-human");

        Assert.Equal(2, exit);
        Assert.Contains("unanswered-human-question", stderr, StringComparison.Ordinal);
        Assert.Contains("'db'", stderr, StringComparison.Ordinal);
        Assert.Contains("Which database?", stderr, StringComparison.Ordinal);
        Assert.Contains("target: human", stderr, StringComparison.Ordinal);

        // And it says plainly that the file was still written, so a reader of stderr alone does not conclude
        // the run produced nothing.
        Assert.Contains("WAS written", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePredicateIsEvaluatedAFTERAnswersIsMerged()
    {
        // The whole point of the flag: a pipeline supplies an answers file and wants to know it was complete.
        var answersPath = Path.Combine(_dir, "answers.json");
        File.WriteAllText(answersPath, "{\"db\": [\"Postgres\"]}");

        var (exit, _, _) = Run(OpenHumanQuestion, "--fail-if-needs-human", "--answers", answersPath);

        Assert.Equal(0, exit);
        Assert.Contains("Answered: Postgres", File.ReadAllText(Out()), StringComparison.Ordinal);
    }

    [Fact]
    public void AnswerIdsMatchingNoQuestion_AreNamedOnStderr()
    {
        // "Your answers file had ids and none of them matched" is a signal the pipeline needs; today they are
        // discarded in silence, so a stale id and a correct file look identical.
        var answersPath = Path.Combine(_dir, "answers.json");
        File.WriteAllText(answersPath, "{\"db\": [\"Postgres\"], \"gone\": [\"x\"], \"also-gone\": [\"y\"]}");

        var (exit, _, stderr) = Run(OpenHumanQuestion, "--fail-if-needs-human", "--answers", answersPath);

        Assert.Equal(0, exit);   // reported, never a veto -- the questions they failed to answer already are
        Assert.Contains("2 --answers id(s) matched no :::question", stderr, StringComparison.Ordinal);
        Assert.Contains("gone", stderr, StringComparison.Ordinal);
        Assert.Contains("also-gone", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUndecidableAgentQuestion_Escalates()
    {
        var (exit, _, stderr) = Run(
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
            + "{\"id\": \"backoff\", \"title\": \"What retry backoff?\", \"mode\": \"free-text\", "
            + "\"target\": \"agent\"}\n:::\n",
            "--fail-if-needs-human");

        Assert.Equal(2, exit);
        Assert.Contains("undecidable-agent-question", stderr, StringComparison.Ordinal);
        Assert.Contains("'backoff'", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ADecidableAgentQuestion_DoesNotEscalate_AndTheFlattenTellsTheAgentToDecideIt()
    {
        var (exit, _, _) = Run(
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
            + "{\"id\": \"cache\", \"title\": \"Which cache?\", \"mode\": \"single\", \"target\": \"agent\", "
            + "\"options\": [\"Redis\", \"in-memory\"], \"recommended\": \"Redis\"}\n:::\n",
            "--fail-if-needs-human");

        Assert.Equal(0, exit);

        var handoff = File.ReadAllText(Out());
        Assert.Contains("Delegated decision", handoff, StringComparison.Ordinal);
        Assert.Contains("Decide: choose exactly one of the options above", handoff, StringComparison.Ordinal);
    }

    [Fact]
    public void AMisspelledQuestionDirective_Escalates()
    {
        // Both verbs exit 0 on this today, on a hidden `target: human` decision, because :::questoin
        // classifies as an unknown directive and the record files that as a warning.
        var (exit, _, stderr) = Run(
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::questoin\n"
            + "{\"id\": \"db\", \"title\": \"Which database?\", \"mode\": \"single\", \"target\": \"human\", "
            + "\"options\": [\"Postgres\", \"MySQL\"]}\n:::\n",
            "--fail-if-needs-human");

        Assert.Equal(2, exit);
        Assert.Contains("unknown-directive", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnparseableQuestionBody_Escalates()
    {
        var (exit, _, stderr) = Run(
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
            + "{\"id\": \"db\", \"title\": \"Which database?\", \"mode\": \"single\", \"target\": \"human\", "
            + "\"options\": [\"Postgres\", \"MySQL\"],}\n:::\n",
            "--fail-if-needs-human");

        Assert.Equal(2, exit);
        Assert.Contains("malformed-question", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ACleanPlan_ExitsZeroWithTheFlag()
    {
        var (exit, _, stderr) = Run(CleanPlan, "--fail-if-needs-human");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("need a human", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFlattenedPlan_CarriesThePlansSha256Stamp()
    {
        // The in-band half (Charter #172/#187): it survives a consumer that ignores exit codes and side files,
        // which is the one thing no out-of-band record can do.
        Run(CleanPlan);

        Assert.Matches(@"<!-- charter: plan-sha256=[0-9a-f]{64} -->", File.ReadAllText(Out()));
    }

    [Fact]
    public void HandoffHelp_DocumentsItsExitCodesAndTheTwoVocabularyWarning()
    {
        var (_, stdout, _) = CharterCliRunner.Run("handoff", "--help");

        Assert.Contains("--fail-if-needs-human", stdout, StringComparison.Ordinal);
        Assert.Contains("NOTE ON 2", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveHelp_DocumentsTheFiveExitCodesItReturns()
    {
        // `resolve` has always returned 0/2/3/4/5 and documented none of them anywhere a caller looks.
        var (_, stdout, _) = CharterCliRunner.Run("resolve", "--help");

        Assert.Contains("Exit codes", stdout, StringComparison.Ordinal);
        Assert.Contains("UNKNOWN", stdout, StringComparison.Ordinal);
        Assert.Contains("PRESERVED", stdout, StringComparison.Ordinal);
        Assert.Contains("NOTE ON 2", stdout, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private string Out() => Path.Combine(_dir, "plan.md");

    private (int ExitCode, string StdOut, string StdErr) Run(
        string plan, params string[] extraArgs)
        => RunNamed(plan, "plan.charter.md", extraArgs);

    // Named distinctly (not a Run overload) so a call like Run(plan, "--fail-if-needs-human") can never bind
    // its flag to the plan-name parameter -- the params-vs-overload trap CharterCliRunner already warns about,
    // and which silently made every escalation case in this file assert against a run with no flag at all.
    private (int ExitCode, string StdOut, string StdErr) RunNamed(
        string plan, string planName, params string[] extraArgs)
    {
        var planPath = Path.Combine(_dir, planName);
        File.WriteAllText(planPath, plan);

        var args = new List<string> { "handoff", planPath, "-o", Out() };
        args.AddRange(extraArgs);
        return CharterCliRunner.Run(args.ToArray());
    }
}
