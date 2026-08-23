using System;
using System.Collections.Generic;
using System.IO;
using Charter.Core;
using Charter.Server;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #75 item 3, through the REAL <c>charter resolve</c> binary. Answers are keyed by <c>:::question</c>
/// id, not by an anchor, so the #67 replaced-plan quarantine deliberately says nothing about them — which left
/// a plan re-authored at the same path, reusing an id a <c>charter convert</c> seed will happily regenerate,
/// able to inherit the OLD document's decision. Unlike an orphaned annotation, that write lands in the plan
/// file: silent, durable, and possibly already shaping a Guardrails DAG.
///
/// The rule under test: refuse when a queued answer carries a question fingerprint, a question with that id
/// still exists, and its declared shape has changed. Consistent with #67's philosophy — never silently destroy,
/// never silently apply; surface, and make the human's choice explicit (<c>--apply-stale-answers</c>).
/// </summary>
[Trait("Category", "Cli")]
public class AnswerStalenessTests
{
    private const string OriginalQuestion =
        ":::question\n"
        + "{ \"id\": \"db-choice\", \"title\": \"Which database?\", \"mode\": \"single\", "
        + "\"target\": \"human\", \"options\": [\"Postgres\", \"SQLite\"] }\n"
        + ":::\n";

    // A DIFFERENT document that reuses the id — the exact #75 item 3 case.
    private const string ReplacementQuestion =
        ":::question\n"
        + "{ \"id\": \"db-choice\", \"title\": \"Which tenancy model?\", \"mode\": \"single\", "
        + "\"target\": \"human\", \"options\": [\"Schema per tenant\", \"Row-level\"] }\n"
        + ":::\n";

    private static string Plan(string question, string prose) =>
        "# A plan\n\n" + prose + "\n\n" + question;

    private const string OriginalProse = "The read path stays Postgres-only until the write path is proven.";
    private const string EditedProse = "A completely rewritten paragraph about something else entirely.";

    [Fact]
    public void Resolve_WhenTheQuestionWasReplacedUnderTheSameId_RefusesAndKeepsTheAnswer()
    {
        using var work = new Workspace();
        var planPath = work.SeedPlan(Plan(OriginalQuestion, OriginalProse));
        work.SeedAnswer(planPath, "db-choice", "Postgres");

        // The plan is replaced at the same path; the new document happens to reuse `db-choice`.
        File.WriteAllText(planPath, Plan(ReplacementQuestion, "Every tenant gets an isolated schema."));

        var run = work.Resolve(planPath);

        Assert.Equal(ReviewExitCodes.ApplyFailed, run.ExitCode);
        Assert.Contains("DIFFERENT version of their :::question", run.StdErr, StringComparison.Ordinal);
        Assert.Contains("db-choice", run.StdErr, StringComparison.Ordinal);
        Assert.Contains("--apply-stale-answers", run.StdErr, StringComparison.Ordinal);

        // Nothing was written into the plan...
        Assert.DoesNotContain("\"answer\"", File.ReadAllText(planPath), StringComparison.Ordinal);

        // ...and nothing was destroyed: the decision is still queued, recoverable either way.
        Assert.Single(work.QueuedAnswers(planPath));
    }

    [Fact]
    public void Resolve_WithApplyStaleAnswers_AppliesItAnyway_TheHumansExplicitChoice()
    {
        using var work = new Workspace();
        var planPath = work.SeedPlan(Plan(OriginalQuestion, OriginalProse));
        work.SeedAnswer(planPath, "db-choice", "Postgres");
        File.WriteAllText(planPath, Plan(ReplacementQuestion, "Every tenant gets an isolated schema."));

        var run = work.Resolve(planPath, "--apply-stale-answers");

        Assert.Equal(ReviewExitCodes.Drained, run.ExitCode);
        Assert.Contains("\"answer\": [\"Postgres\"]", File.ReadAllText(planPath), StringComparison.Ordinal);
        Assert.Empty(work.QueuedAnswers(planPath));
    }

    [Fact]
    public void Resolve_AfterAnOrdinaryEdit_StillAppliesNormally_TheLivingDocumentModel()
    {
        // The whole point of the rule being narrow: the plan around the question is rewritten wholesale, and
        // the reviewer's decision still lands, exactly as before this guard existed.
        using var work = new Workspace();
        var planPath = work.SeedPlan(Plan(OriginalQuestion, OriginalProse));
        work.SeedAnswer(planPath, "db-choice", "SQLite");
        File.WriteAllText(planPath, Plan(OriginalQuestion, EditedProse));

        var run = work.Resolve(planPath);

        Assert.Equal(ReviewExitCodes.Drained, run.ExitCode);
        Assert.Contains("\"answer\": [\"SQLite\"]", File.ReadAllText(planPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AnAnswerWithNoFingerprint_AppliesExactlyAsBefore()
    {
        // A queue written by a Charter from before this evidence existed carries no fingerprint. No evidence
        // must mean "proceed", never "refuse" — otherwise upgrading Charter would strand every queued answer.
        using var work = new Workspace();
        var planPath = work.SeedPlan(Plan(OriginalQuestion, OriginalProse));
        work.SeedUnfingerprintedAnswer(planPath, "db-choice", "Postgres");
        File.WriteAllText(planPath, Plan(ReplacementQuestion, "Every tenant gets an isolated schema."));

        var run = work.Resolve(planPath);

        Assert.Equal(ReviewExitCodes.Drained, run.ExitCode);
        Assert.Contains("\"answer\": [\"Postgres\"]", File.ReadAllText(planPath), StringComparison.Ordinal);
    }

    /// <summary>
    /// Charter #203 step 1, the <c>resolve</c> half — and a REWRITE, not an extension.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test used to be <c>Resolve_WhenTheQuestionIsGoneEntirely_StillAppliesAndIsANoOp</c>, and it asserted
    /// exit <c>0</c> on the reasoning that <i>"an absent question cannot be mis-answered — QuestionResolution
    /// leaves ids it does not find untouched — so refusing there would be a false alarm with no failure behind
    /// it."</i> The premise is true and the conclusion does not follow. The apply is a no-op in the PLAN; it is
    /// not a no-op in the QUEUE. A byte-identical write scores as a successful apply, so this verb went on to
    /// clear the sidecar and print <c>Resolved 1 answer(s)</c> — the reviewer's decision destroyed, with the
    /// failure invisible from every side. The old name asserted leniency around a degraded value, which is
    /// exactly where this repo has learned to go looking for a defect.
    /// </para>
    /// </remarks>
    [Fact]
    public void Resolve_WhenTheQuestionIsGoneEntirely_RefusesRatherThanDestroyingTheAnswer()
    {
        using var work = new Workspace();
        var planPath = work.SeedPlan(Plan(OriginalQuestion, OriginalProse));
        work.SeedAnswer(planPath, "db-choice", "Postgres");
        File.WriteAllText(planPath, "# A plan with no questions at all\n\nJust prose.\n");

        var run = work.Resolve(planPath);

        Assert.Equal(ReviewExitCodes.ApplyFailed, run.ExitCode);
        Assert.Contains("does not carry as a block", run.StdErr, StringComparison.Ordinal);
        Assert.Contains("db-choice", run.StdErr, StringComparison.Ordinal);

        Assert.DoesNotContain("\"answer\"", File.ReadAllText(planPath), StringComparison.Ordinal);
        Assert.Single(work.QueuedAnswers(planPath));
    }

    /// <summary>
    /// The nesting case #203 was filed for, through <c>charter resolve</c>: the question is right there in the
    /// plan and renders as a live form, but a <c>::::note</c> holds it, so it is not a block.
    /// </summary>
    [Fact]
    public void Resolve_WhenTheQuestionIsNestedInsideACallout_RefusesRatherThanDestroyingTheAnswer()
    {
        using var work = new Workspace();
        var planPath = work.SeedPlan(Plan(OriginalQuestion, OriginalProse));
        work.SeedAnswer(planPath, "db-choice", "Postgres");

        // The same question, moved inside a callout — a live, answerable form the block model cannot see.
        File.WriteAllText(planPath, "# A plan\n\n::::note\nA callout that asks something.\n\n" + OriginalQuestion + "::::\n");

        var run = work.Resolve(planPath);

        Assert.Equal(ReviewExitCodes.ApplyFailed, run.ExitCode);
        Assert.Contains("db-choice", run.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("\"answer\"", File.ReadAllText(planPath), StringComparison.Ordinal);
        Assert.Single(work.QueuedAnswers(planPath));
    }

    /// <summary>
    /// <c>--apply-stale-answers</c> does NOT bypass the #203 refusal. The flag means "the question changed shape,
    /// apply it anyway"; there is nothing to apply anyway to when the question is not a block, and letting it
    /// through would re-open the destruction under a flag that reads like consent to something else entirely.
    /// </summary>
    [Fact]
    public void Resolve_ApplyStaleAnswers_DoesNotBypassTheUnmatchedRefusal()
    {
        using var work = new Workspace();
        var planPath = work.SeedPlan(Plan(OriginalQuestion, OriginalProse));
        work.SeedAnswer(planPath, "db-choice", "Postgres");
        File.WriteAllText(planPath, "# A plan with no questions at all\n\nJust prose.\n");

        var run = work.Resolve(planPath, "--apply-stale-answers");

        Assert.Equal(ReviewExitCodes.ApplyFailed, run.ExitCode);
        Assert.Single(work.QueuedAnswers(planPath));
    }

    // ---- Plumbing ----------------------------------------------------------------------------------------

    /// <summary>
    /// A plan directory plus an isolated <c>CHARTER_STATE_DIR</c>, and the sidecar seeding that stands in for
    /// "a reviewer answered, then closed the browser" — the solo path <c>charter resolve</c> exists for. The
    /// fingerprint is computed the same way the server computes it at submit time
    /// (<see cref="QuestionIdentity.FingerprintOf"/>), so this seeds the real shape rather than a guess at it;
    /// that the SERVER writes that value is pinned separately by <c>AnswerFingerprintTests</c>.
    /// </summary>
    private sealed class Workspace : IDisposable
    {
        private readonly string _root = CharterCliRunner.NewTempDirectory();

        public Workspace()
        {
            PlanDirectory = Path.Combine(_root, "plans");
            StateDirectory = Path.Combine(_root, "state");
            Directory.CreateDirectory(PlanDirectory);
            Directory.CreateDirectory(StateDirectory);
        }

        public string PlanDirectory { get; }

        public string StateDirectory { get; }

        public void Dispose() => CharterCliRunner.TryDeleteDirectory(_root);

        public string SeedPlan(string markdown)
        {
            var planPath = Path.Combine(PlanDirectory, "plan.charter.md");
            File.WriteAllText(planPath, markdown);
            return planPath;
        }

        /// <summary>Queue one answer in the sidecar, fingerprinted against the plan as it is right now.</summary>
        public void SeedAnswer(string planPath, string questionId, string value)
        {
            var fingerprint = QuestionIdentity.FingerprintOf(File.ReadAllText(planPath), questionId);
            Assert.NotNull(fingerprint);
            SeedAnswerWithFingerprint(planPath, questionId, value, fingerprint);
        }

        /// <summary>Queue one answer as a pre-#75 Charter would have: no evidence at all.</summary>
        public void SeedUnfingerprintedAnswer(string planPath, string questionId, string value)
            => SeedAnswerWithFingerprint(planPath, questionId, value, fingerprint: null);

        private void SeedAnswerWithFingerprint(
            string planPath, string questionId, string value, string? fingerprint)
        {
            ReviewSidecar.WriteState(
                SidecarPath(planPath),
                planPath,
                Array.Empty<Annotation>(),
                new[] { new Answer(questionId, "single", new[] { value }, "human", fingerprint) });
        }

        public IReadOnlyList<Answer> QueuedAnswers(string planPath)
            => ReviewSidecar.Rehydrate(SidecarPath(planPath)).Answers;

        public (int ExitCode, string StdOut, string StdErr) Resolve(string planPath, params string[] flags)
        {
            var args = new List<string> { "resolve", planPath };
            args.AddRange(flags);
            return CharterCliRunner.RunWith(
                PlanDirectory,
                new Dictionary<string, string> { ["CHARTER_STATE_DIR"] = StateDirectory },
                args.ToArray());
        }

        private string SidecarPath(string planPath)
            => ReviewSidecar.PathForPlan(
                Path.Combine(StateDirectory, "sidecars"), Path.GetFullPath(planPath));
    }
}
