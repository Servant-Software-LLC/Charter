using System;
using System.Collections.Generic;
using System.IO;
using Charter.Core;
using Charter.Server;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// <c>charter resolve</c> refuses to WRITE an answer carrying a control character into the plan
/// (Charter #202), through the REAL binary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the answer route's gate is not enough on its own.</b> <c>charter resolve</c> reads the durability
/// sidecar directly — there is no HTTP route in the picture at all — and <c>ReviewSidecar.Rehydrate</c>
/// restores whatever a PREVIOUS Charter queued. A sidecar written by 0.24.0 can hold a CR-carrying answer that
/// no gate has ever seen, and the write it feeds is the one that puts the character into the
/// <c>.charter.md</c> permanently. So the refusal has to sit at the write, not only at the wire.
/// </para>
/// <para>
/// <b>Exit 5, answers preserved</b> — the same treatment as a duplicate-id plan and a stale batch. It already
/// means exactly the right thing: <i>the inline apply did not happen; the answers are preserved, never
/// committed</i>. Reporting success and committing the batch away is Charter #203's destruction, one channel
/// over.
/// </para>
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","ResolveControlCharacter")].
/// </remarks>
[Trait("Category", "ResolveControlCharacter")]
public class ResolveControlCharacterTests
{
    private const string FreeTextPlan =
        "# A plan\n\nThe read path stays Postgres-only until the write path is proven.\n\n"
        + ":::question\n"
        + "{ \"id\": \"why\", \"title\": \"Why this approach?\", \"mode\": \"free-text\", "
        + "\"target\": \"human\" }\n"
        + ":::\n";

    [Fact]
    public void AQueuedAnswerCarryingACarriageReturn_IsRefused_AndTheAnswerIsKept()
    {
        using var work = new Workspace();
        var planPath = work.SeedPlan(FreeTextPlan);
        work.SeedAnswer(planPath, "why", "alpha\rbeta");

        var run = work.Resolve(planPath);

        Assert.Equal(ReviewExitCodes.ApplyFailed, run.ExitCode);
        Assert.Contains("why", run.StdErr, StringComparison.Ordinal);
        Assert.Contains("U+000D", run.StdErr, StringComparison.Ordinal);

        // Nothing was written into the plan, and nothing was discarded from the sidecar.
        Assert.Equal(FreeTextPlan, File.ReadAllText(planPath));
        Assert.Single(work.QueuedAnswers(planPath));
    }

    [Fact]
    public void TheRefusedAnswer_NeverReachesTheFlattenedPlan()
    {
        using var work = new Workspace();
        var planPath = work.SeedPlan(FreeTextPlan);
        work.SeedAnswer(planPath, "why", "alpha\rbeta");

        work.Resolve(planPath);

        var flatten = HandoffMarkdown.Emit(File.ReadAllText(planPath));

        Assert.DoesNotContain("\r", flatten, StringComparison.Ordinal);
        Assert.Contains(HandoffMarkdown.OpenQuestionMarker, flatten, StringComparison.Ordinal);
    }

    [Fact]
    public void AQueuedMultiLineFreeTextAnswer_IsSTILLApplied()
    {
        // The boundary, at the write path: a reviewer's two-sentence answer out of the page's <textarea> is a
        // legitimate answer and must fold into the plan exactly as before.
        using var work = new Workspace();
        var planPath = work.SeedPlan(FreeTextPlan);
        work.SeedAnswer(planPath, "why", "one\ntwo");

        var run = work.Resolve(planPath);

        Assert.Equal(ReviewExitCodes.Drained, run.ExitCode);
        Assert.Contains("\"answer\": [\"one\\ntwo\"]", File.ReadAllText(planPath), StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

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

            ReviewSidecar.WriteState(
                SidecarPath(planPath),
                planPath,
                Array.Empty<Annotation>(),
                new[] { new Answer(questionId, "free-text", new[] { value }, "human", fingerprint) });
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
