using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Charter.Server;
using Xunit;
using Xunit.Sdk;

namespace Charter.Cli.Tests;

/// <summary>
/// Process-level tests for <c>charter poll</c> and the <c>charter review</c> session-descriptor lifecycle. They
/// invoke the REAL built binary as a child process (like <see cref="CliProcessTests"/>), isolating each run in
/// its own temp state directory via the <c>CHARTER_STATE_DIR</c> override so nothing touches the developer's
/// real registry and tests never pollute one another. The load-bearing case is the end-to-end loop: a running
/// <c>review</c> server, an answer submitted to it, and <c>poll --url --apply</c> draining the envelope AND
/// writing that answer INLINE into the plan's <c>:::question</c> (the living-document write).
/// </summary>
[Trait("Category", "Cli")]
public class PollCommandTests
{
    private const string SimplePlan = "# Poll Plan\n\nAn overview paragraph for the poll tests.\n";

    private const string QuestionPlan =
        "# Poll Loop Plan\n\nAn overview paragraph.\n\n" +
        ":::question\n" +
        "{\"id\":\"q-theme\",\"title\":\"Which theme should ship?\",\"mode\":\"single\",\"options\":[\"A\",\"B\"],\"target\":\"human\"}\n" +
        ":::\n";

    // Two :::question blocks sharing id "q-theme": applying an answer would double-write, so ApplyToFile
    // REFUSES. Used to drive the deterministic, cross-platform apply-failure path (answer preserved, exit 5).
    private const string DuplicateIdPlan =
        "# Duplicate Id Plan\n\nAn overview paragraph.\n\n" +
        ":::question\n" +
        "{\"id\":\"q-theme\",\"title\":\"First\",\"mode\":\"single\",\"options\":[\"A\",\"B\"],\"target\":\"human\"}\n" +
        ":::\n\n" +
        ":::question\n" +
        "{\"id\":\"q-theme\",\"title\":\"Second\",\"mode\":\"single\",\"options\":[\"A\",\"B\"],\"target\":\"human\"}\n" +
        ":::\n";

    // Charter #49 fixture: a MULTI-LINE :::question (the canonical authored shape) ABOVE the paragraph a
    // reviewer annotates, so an apply that reflowed the question body would shift the annotated block's line.
    // Line 13 is the annotated paragraph.
    private const string AnchorMarker = "The annotated paragraph the agent must edit.";

    private const string AnchorPlan =
        "# Poll Anchor Plan\n" +
        "\n" +
        ":::question\n" +
        "{\n" +
        "  \"id\": \"q-theme\",\n" +
        "  \"title\": \"Which theme should ship?\",\n" +
        "  \"mode\": \"single\",\n" +
        "  \"options\": [\"A\", \"B\"],\n" +
        "  \"target\": \"human\"\n" +
        "}\n" +
        ":::\n" +
        "\n" +
        AnchorMarker + "\n";

    // The same shape, but the question is ALREADY resolved. Re-answering it makes QuestionResolution fall back
    // from the in-place splice to a whole-body re-serialize, which collapses the multi-line body onto one line
    // — so the --apply write itself SHRINKS the file and moves the annotated paragraph (line 14 -> line 7).
    // This is the original Charter #49 repro: the shift happens inside the very invocation that reports the line.
    private const string ReapplyPlan =
        "# Poll Reapply Plan\n" +
        "\n" +
        ":::question\n" +
        "{\n" +
        "  \"id\": \"q-theme\",\n" +
        "  \"title\": \"Which theme should ship?\",\n" +
        "  \"mode\": \"single\",\n" +
        "  \"options\": [\"A\", \"B\"],\n" +
        "  \"target\": \"human\",\n" +
        "  \"answer\": [\"A\"]\n" +
        "}\n" +
        ":::\n" +
        "\n" +
        AnchorMarker + "\n";

    private static readonly Regex ReadyUrl = new(@"https?://127\.0\.0\.1:\d+/\?key=[0-9a-f]+", RegexOptions.Compiled);

    // ---- No-session + regression --------------------------------------------------------------------------

    [Fact]
    public async Task Poll_NoRunningSession_Exits3_WithCleanStderr_AndSessionNull()
    {
        var stateDir = NewTempDir();
        try
        {
            // An empty registry -> no live session to auto-select.
            var result = await RunCharterAsync(stateDir, "poll");

            Assert.Equal(3, result.ExitCode);
            Assert.Contains("no running review session", result.StdErr);
            AssertSessionNull(result.StdOut);

            var combined = result.StdOut + "\n" + result.StdErr;
            Assert.DoesNotContain("Unhandled exception", combined);
            Assert.DoesNotContain("   at ", combined);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task Poll_UnknownPlan_Exits3_SessionNull()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(SimplePlan);
        try
        {
            // A plan with no descriptor registered: selects by canonical path, finds nothing -> no session.
            var result = await RunCharterAsync(stateDir, "poll", planPath);

            Assert.Equal(3, result.ExitCode);
            AssertSessionNull(result.StdOut);
        }
        finally
        {
            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task NoArgs_HelpBanner_ListsPollVerb()
    {
        var result = await RunCharterAsync(stateDir: null);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("poll", result.StdOut);
    }

    [Fact]
    public async Task UnknownVerb_Message_ListsPoll_AndExitsNonZero()
    {
        var result = await RunCharterAsync(stateDir: null, "definitely-not-a-verb");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("poll", result.StdErr);
    }

    // ---- The end-to-end loop (T5 integration) -------------------------------------------------------------

    [Fact]
    public async Task Poll_UrlDrain_ReportsAnswer_ButLeavesItRecoverable()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(QuestionPlan);
        Process? review = null;
        try
        {
            (review, var url) = await StartReviewAsync(stateDir, planPath);

            // The human submits an answer to q-theme through the running review server.
            await PostAnswerAsync(url, "q-theme", new[] { "A" });

            // A PLAIN poll --url REPORTS the answer (bypassing discovery) and emits the single stdout envelope.
            var poll = await RunCharterAsync(stateDir, "poll", "--url", url);
            Assert.Equal(0, poll.ExitCode);

            using (var envelope = JsonDocument.Parse(poll.StdOut.Trim()))
            {
                var root = envelope.RootElement;
                var session = root.GetProperty("session");
                Assert.Equal(JsonValueKind.Object, session.ValueKind);
                Assert.Equal(Path.GetFileName(planPath), session.GetProperty("sourceFile").GetString());

                var answers = root.GetProperty("answers");
                Assert.Equal(1, answers.GetArrayLength());
                Assert.Equal("q-theme", answers[0].GetProperty("questionId").GetString());

                // The capability key must never appear in the envelope.
                Assert.DoesNotContain(KeyOf(url), poll.StdOut);
            }

            // A plain poll REPORTS but does NOT remove (§1.6): the plan is still OPEN on disk (no inline write),
            // AND the answer is still queued — proven by a following --apply that STILL resolves it. The old
            // destructive drain would have stranded the answer here (removed from the store, absent from the
            // file). This is the exact data-loss the report-don't-remove semantics close.
            Assert.Null(ExtractQuestionAnswer(await File.ReadAllTextAsync(planPath), "q-theme"));

            var apply = await RunCharterAsync(stateDir, "poll", "--url", url, "--apply");
            Assert.Equal(0, apply.ExitCode);

            var resolved = ExtractQuestionAnswer(await File.ReadAllTextAsync(planPath), "q-theme");
            Assert.NotNull(resolved);
            Assert.Equal(new[] { "A" }, resolved);
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Poll_UrlDrain_Apply_WritesAnswerInlineIntoTheCharterMd()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(QuestionPlan);
        Process? review = null;
        try
        {
            (review, var url) = await StartReviewAsync(stateDir, planPath);

            // Before: the plan's :::question is OPEN (no inline answer yet).
            Assert.Null(ExtractQuestionAnswer(await File.ReadAllTextAsync(planPath), "q-theme"));

            // The human submits an answer to q-theme through the running review server.
            await PostAnswerAsync(url, "q-theme", new[] { "A" });

            // poll --url --apply drains it AND writes the answer INLINE into the plan file (living-document write).
            var poll = await RunCharterAsync(stateDir, "poll", "--url", url, "--apply");
            Assert.Equal(0, poll.ExitCode);

            // The envelope is still emitted (key omitted) — --apply is an additive effect, not a replacement.
            using (var envelope = JsonDocument.Parse(poll.StdOut.Trim()))
            {
                var answers = envelope.RootElement.GetProperty("answers");
                Assert.Equal(1, answers.GetArrayLength());
                Assert.Equal("q-theme", answers[0].GetProperty("questionId").GetString());
                Assert.DoesNotContain(KeyOf(url), poll.StdOut);
            }

            // After: the .charter.md ON DISK now carries the resolved answer inline — parse the :::question body
            // back to JSON and assert the answer array holds the submitted value. Proves the question is RESOLVED.
            var resolved = ExtractQuestionAnswer(await File.ReadAllTextAsync(planPath), "q-theme");
            Assert.NotNull(resolved);
            Assert.Equal(new[] { "A" }, resolved);
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    /// <summary>
    /// Charter #75 item 3, agent path: <c>poll --apply</c> must refuse to fold in a decision whose
    /// <c>:::question</c> is no longer the one the reviewer was asked. Unlike an orphaned annotation this write
    /// lands IN THE PLAN FILE, so it is silent and durable — and the agent gets no override, because
    /// <c>charter resolve --apply-stale-answers</c> is the human's verb for saying "apply it anyway".
    /// </summary>
    [Fact]
    public async Task Poll_Apply_QuestionReplacedUnderTheSameId_RefusesAndPreservesTheAnswer()
    {
        // The reviewer answers "Which theme should ship? A / B", then the question is rewritten under the same
        // id — a different decision entirely. `A` would still splice in cleanly, which is exactly the danger.
        const string rewritten =
            "# Poll Loop Plan\n\nAn overview paragraph.\n\n" +
            ":::question\n" +
            "{\"id\":\"q-theme\",\"title\":\"Which rollout order?\",\"mode\":\"single\"," +
            "\"options\":[\"Canary first\",\"Big bang\"],\"target\":\"human\"}\n" +
            ":::\n";

        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(QuestionPlan);
        Process? review = null;
        try
        {
            (review, var url) = await StartReviewAsync(stateDir, planPath);
            await PostAnswerAsync(url, "q-theme", new[] { "A" });

            await File.WriteAllTextAsync(planPath, rewritten);

            var apply = await RunCharterAsync(stateDir, "poll", "--url", url, "--apply");

            Assert.Equal(5, apply.ExitCode);
            Assert.Contains("DIFFERENT version of their :::question", apply.StdErr, StringComparison.Ordinal);
            Assert.Contains("q-theme", apply.StdErr, StringComparison.Ordinal);
            Assert.Contains("--apply-stale-answers", apply.StdErr, StringComparison.Ordinal);

            // Nothing was written into the plan...
            Assert.Null(ExtractQuestionAnswer(await File.ReadAllTextAsync(planPath), "q-theme"));

            // ...and the envelope still reaches stdout exactly once, reporting the answer as still queued: a
            // refused apply reports, it never drops.
            using var envelope = JsonDocument.Parse(apply.StdOut.Trim());
            Assert.Equal(1, envelope.RootElement.GetProperty("answers").GetArrayLength());
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Poll_Apply_DuplicateIds_RefusesWithClearError_AndPreservesAnswer()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(DuplicateIdPlan);
        Process? review = null;
        try
        {
            (review, var url) = await StartReviewAsync(stateDir, planPath);
            await PostAnswerAsync(url, "q-theme", new[] { "A" });

            // --apply is REFUSED: the plan's two :::question share an id, so applying would double-write. This
            // is the deterministic, cross-platform apply-failure — distinct exit 5, a clear message naming the
            // id, and (the whole point) the answer is NOT lost: peek → apply → commit never committed it.
            var apply = await RunCharterAsync(stateDir, "poll", "--url", url, "--apply");
            Assert.Equal(5, apply.ExitCode);
            Assert.Contains("apply failed", apply.StdErr);
            Assert.Contains("q-theme", apply.StdErr);

            // EXACTLY ONE envelope still reaches stdout on the failed-apply path. The envelope moved AFTER the
            // apply (so its annotation lines point into the file the write left behind — Charter #49), which
            // would be a regression if a refusal could now swallow it: the reported answers are the primary
            // contract, the write is the effect.
            using (var refused = JsonDocument.Parse(apply.StdOut.Trim()))
            {
                Assert.Equal(1, refused.RootElement.GetProperty("answers").GetArrayLength());
            }

            // The plan stayed OPEN (no partial/double write)...
            Assert.Null(ExtractQuestionAnswer(await File.ReadAllTextAsync(planPath), "q-theme"));

            // ...and the answer is PRESERVED — a following plain poll still reports it (recoverable, not lost).
            var poll = await RunCharterAsync(stateDir, "poll", "--url", url);
            Assert.Equal(0, poll.ExitCode);
            using var envelope = JsonDocument.Parse(poll.StdOut.Trim());
            Assert.Equal(1, envelope.RootElement.GetProperty("answers").GetArrayLength());
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    /// <summary>
    /// Charter #49, end to end through the REAL CLI: the <c>sourceLine</c> the envelope hands the agent must
    /// point at the annotated block IN THE FILE AS THE INVOCATION LEAVES IT. The original failure resolved the
    /// line at SUBMIT time and never re-resolved it, so an edit above the block — the agent's own revision, or
    /// the <c>--apply</c> write folding an answer into the multi-line <c>:::question</c> above it — handed the
    /// agent a line that pointed one block off. Here the plan is edited above the annotated paragraph after the
    /// note is submitted, and the same invocation also performs the <c>--apply</c> write.
    /// </summary>
    [Fact]
    public async Task Poll_Apply_DrainedAnnotationLine_PointsAtTheBlockInTheFileTheInvocationLeaves()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(AnchorPlan);
        Process? review = null;
        try
        {
            (review, var url) = await StartReviewAsync(stateDir, planPath);

            var anchorId = Charter.Core.BlockDocument.Parse(AnchorPlan).Blocks
                .Single(b => b.RawContent.Contains(AnchorMarker, StringComparison.Ordinal)).Id;
            Assert.Equal(13, Charter.Core.SourceMap.Build(AnchorPlan).LineForAnchor(anchorId));

            await PostAnnotationAsync(url, anchorId, "Spell out the acceptance criteria here.");
            await PostAnswerAsync(url, "q-theme", new[] { "A" });

            // The drafting agent edits the plan ABOVE the annotated block (two extra lines). The annotated
            // paragraph's own text is untouched, so its anchor is unchanged — only its line moved, 13 -> 15.
            var edited = AnchorPlan.Replace(
                "# Poll Anchor Plan\n\n",
                "# Poll Anchor Plan\n\nAn inserted overview paragraph.\n\n",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(planPath, edited);

            var poll = await RunCharterAsync(stateDir, "poll", "--url", url, "--apply");
            Assert.Equal(0, poll.ExitCode);

            // The line the agent was handed must resolve, in the file the invocation actually left on disk, to
            // the annotated paragraph — not to the blank line or the block above it.
            var onDisk = await File.ReadAllTextAsync(planPath);
            var expectedLine = Charter.Core.SourceMap.Build(onDisk).LineForAnchor(anchorId);
            Assert.NotNull(expectedLine);

            using var envelope = JsonDocument.Parse(poll.StdOut.Trim());
            var annotations = envelope.RootElement.GetProperty("annotations");
            Assert.Equal(1, annotations.GetArrayLength());
            var annotation = annotations[0];

            Assert.Equal(expectedLine, annotation.GetProperty("sourceLine").GetInt32());
            Assert.Equal("resolved", annotation.GetProperty("anchorStatus").GetString());

            // ...and independently of the source map: the reported line genuinely holds the annotated text,
            // and it MOVED (so this is not a vacuous pass on an unshifted document).
            var lines = onDisk.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            Assert.Equal(AnchorMarker, lines[annotation.GetProperty("sourceLine").GetInt32() - 1]);
            Assert.NotEqual(13, annotation.GetProperty("sourceLine").GetInt32());

            // The --apply write still landed (the two effects are independent).
            Assert.Equal(new[] { "A" }, ExtractQuestionAnswer(onDisk, "q-theme"));
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    /// <summary>
    /// Charter #49's original repro, end to end: the <c>--apply</c> write in THIS invocation shortens the
    /// <c>:::question</c> above the annotated block, so every line below it moves. The envelope is emitted
    /// after the write and re-resolved against the file that write left behind, so the agent is handed the
    /// block's NEW line — the failure was that it was handed the pre-apply line and edited one block off.
    /// </summary>
    [Fact]
    public async Task Poll_Apply_ThatShortensAQuestionAboveTheBlock_ReportsTheShiftedLine_NotThePreApplyLine()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(ReapplyPlan);
        Process? review = null;
        try
        {
            (review, var url) = await StartReviewAsync(stateDir, planPath);

            var anchorId = Charter.Core.BlockDocument.Parse(ReapplyPlan).Blocks
                .Single(b => b.RawContent.Contains(AnchorMarker, StringComparison.Ordinal)).Id;
            var preApplyLine = Charter.Core.SourceMap.Build(ReapplyPlan).LineForAnchor(anchorId);
            Assert.Equal(14, preApplyLine);

            await PostAnnotationAsync(url, anchorId, "Reword this once the theme is settled.");
            await PostAnswerAsync(url, "q-theme", new[] { "B" });

            var poll = await RunCharterAsync(stateDir, "poll", "--url", url, "--apply");
            Assert.Equal(0, poll.ExitCode);

            var onDisk = await File.ReadAllTextAsync(planPath);
            var postApplyLine = Charter.Core.SourceMap.Build(onDisk).LineForAnchor(anchorId);

            // The apply genuinely moved the block — otherwise this test would pass vacuously.
            Assert.NotEqual(preApplyLine, postApplyLine);

            using var envelope = JsonDocument.Parse(poll.StdOut.Trim());
            var annotation = envelope.RootElement.GetProperty("annotations")[0];
            var reported = annotation.GetProperty("sourceLine").GetInt32();

            Assert.Equal(postApplyLine, reported);
            Assert.Equal("resolved", annotation.GetProperty("anchorStatus").GetString());

            // Independently of the source map: the reported line really holds the annotated text.
            var lines = onDisk.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            Assert.Equal(AnchorMarker, lines[reported - 1]);

            Assert.Equal(new[] { "B" }, ExtractQuestionAnswer(onDisk, "q-theme"));
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    /// <summary>
    /// Charter #49's orphan half, end to end: when the annotated block's OWN content changed, the envelope
    /// reports <c>sourceLine: null</c> plus an explicit <c>anchorStatus: "orphaned"</c> — "the block you
    /// commented on has changed" — instead of a confidently wrong number, with the note preserved.
    /// </summary>
    [Fact]
    public async Task Poll_DrainedAnnotation_OnARewrittenBlock_ReportsOrphanedWithNullLine()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(AnchorPlan);
        Process? review = null;
        try
        {
            (review, var url) = await StartReviewAsync(stateDir, planPath);

            var anchorId = Charter.Core.BlockDocument.Parse(AnchorPlan).Blocks
                .Single(b => b.RawContent.Contains(AnchorMarker, StringComparison.Ordinal)).Id;

            await PostAnnotationAsync(url, anchorId, "This paragraph needs acceptance criteria.");

            // The agent rewrites the very block the note points at: its content-derived anchor ceases to exist.
            var edited = AnchorPlan.Replace(
                AnchorMarker, "The rewritten paragraph, now with acceptance criteria.", StringComparison.Ordinal);
            await File.WriteAllTextAsync(planPath, edited);

            var poll = await RunCharterAsync(stateDir, "poll", "--url", url);
            Assert.Equal(0, poll.ExitCode);

            using var envelope = JsonDocument.Parse(poll.StdOut.Trim());
            var annotation = envelope.RootElement.GetProperty("annotations")[0];

            Assert.Equal(JsonValueKind.Null, annotation.GetProperty("sourceLine").ValueKind);
            Assert.Equal("orphaned", annotation.GetProperty("anchorStatus").GetString());
            Assert.Equal("This paragraph needs acceptance criteria.", annotation.GetProperty("note").GetString());
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    // ---- The reviewer's round hand-off ("Send to agent") in the envelope -----------------------------------

    /// <summary>
    /// The in-page <b>Send to agent</b> click reaches the agent as an ADDITIVE envelope marker —
    /// <c>reviewSubmitted: true</c> plus the hand-off record — which lets it tell "the human explicitly handed
    /// me this round" apart from "I woke because one more comment arrived". It is reported ONCE: the poll that
    /// reports it also acks it, so a later poll does not keep re-announcing a round the agent already took.
    /// </summary>
    [Fact]
    public async Task Poll_ReviewHandoff_RidesTheEnvelopeOnce_AndClearsAfterTheDrain()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(QuestionPlan);
        Process? review = null;
        try
        {
            (review, var url) = await StartReviewAsync(stateDir, planPath);

            // Before the hand-off the marker is present but FALSE — additive and always parseable.
            var idle = await RunCharterAsync(stateDir, "poll", "--url", url);
            using (var envelope = JsonDocument.Parse(idle.StdOut.Trim()))
            {
                Assert.False(envelope.RootElement.GetProperty("reviewSubmitted").GetBoolean());
                Assert.Equal(
                    JsonValueKind.Null, envelope.RootElement.GetProperty("reviewSubmission").ValueKind);
            }

            // The reviewer answers, then clicks "Send to agent".
            await PostAnswerAsync(url, "q-theme", new[] { "A" });
            await PostReviewSubmitAsync(url);

            var handed = await RunCharterAsync(stateDir, "poll", "--url", url);
            Assert.Equal(0, handed.ExitCode);

            using (var envelope = JsonDocument.Parse(handed.StdOut.Trim()))
            {
                var root = envelope.RootElement;
                Assert.True(root.GetProperty("reviewSubmitted").GetBoolean());

                var submission = root.GetProperty("reviewSubmission");
                Assert.Equal(JsonValueKind.Object, submission.ValueKind);
                Assert.Equal(1, submission.GetProperty("answers").GetInt32());
                Assert.Equal(0, submission.GetProperty("annotations").GetInt32());
                Assert.True(submission.GetProperty("sequence").GetInt64() > 0);
                Assert.NotEqual(default, submission.GetProperty("submittedAt").GetDateTimeOffset());

                // The key never rides the envelope, marker or not.
                Assert.DoesNotContain(KeyOf(url), handed.StdOut);
            }

            // Consumed by the drain that reported it: the NEXT poll is not still shouting "the human is done".
            // The answer itself is untouched by the ack — it is still peekable (peek → apply → commit).
            var again = await RunCharterAsync(stateDir, "poll", "--url", url);
            using (var envelope = JsonDocument.Parse(again.StdOut.Trim()))
            {
                Assert.False(envelope.RootElement.GetProperty("reviewSubmitted").GetBoolean());
                Assert.Equal(1, envelope.RootElement.GetProperty("answers").GetArrayLength());
            }
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    /// <summary>
    /// A hand-off with both queues already drained is still something arriving — the human said "this round is
    /// done" — so <c>poll</c> reports it and exits 0 (drained), never 2 (clean-empty, "nothing happened").
    /// </summary>
    [Fact]
    public async Task Poll_ReviewHandoff_WithEmptyQueues_Exits0_NotCleanEmpty()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(SimplePlan);
        Process? review = null;
        try
        {
            (review, var url) = await StartReviewAsync(stateDir, planPath);

            // Nothing queued at all: a plain poll is a clean-empty (exit 2).
            Assert.Equal(2, (await RunCharterAsync(stateDir, "poll", "--url", url)).ExitCode);

            await PostReviewSubmitAsync(url);

            var handed = await RunCharterAsync(stateDir, "poll", "--url", url);
            Assert.Equal(0, handed.ExitCode);
            using var envelope = JsonDocument.Parse(handed.StdOut.Trim());
            Assert.True(envelope.RootElement.GetProperty("reviewSubmitted").GetBoolean());
            Assert.Equal(0, envelope.RootElement.GetProperty("annotations").GetArrayLength());
            Assert.Equal(0, envelope.RootElement.GetProperty("answers").GetArrayLength());
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    /// <summary>
    /// <c>poll --wait</c> must RETURN PROMPTLY when the reviewer answers a <c>:::question</c> while the poll is
    /// outstanding (Charter #62). The server long-polls ~30s, so completing well inside that proves the answer
    /// woke the wait rather than the timeout expiring.
    /// </summary>
    [Fact]
    public async Task PollWait_ReturnsPromptly_WhenAnAnswerArrivesDuringTheWait()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(QuestionPlan);
        Process? review = null;
        try
        {
            (review, var url) = await StartReviewAsync(stateDir, planPath);

            var stopwatch = Stopwatch.StartNew();
            var pollTask = RunCharterAsync(stateDir, "poll", "--url", url, "--wait");

            // Give the child time to start and reach its long-poll, then answer.
            await Task.Delay(TimeSpan.FromSeconds(2));
            await PostAnswerAsync(url, "q-theme", new[] { "A" });

            var poll = await pollTask;
            stopwatch.Stop();

            Assert.Equal(0, poll.ExitCode);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(25),
                $"--wait must wake on a submitted answer, not on the ~30s timeout (took {stopwatch.Elapsed}).");

            using var envelope = JsonDocument.Parse(poll.StdOut.Trim());
            Assert.Equal(1, envelope.RootElement.GetProperty("answers").GetArrayLength());
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Poll_DrainTransportFailure_Exits4_WithDrainError_NotCleanEmpty()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(QuestionPlan);
        HttpListener? stub = null;
        try
        {
            // A stub that PROVES LIVE (GET /api/sessions → 200) but FAILS every drain (poll/answers → 500):
            // exactly the "transport failed mid-drain" case a swallowed error would misreport as empty.
            (stub, var url) = StartFailingDrainStub(planPath);

            var poll = await RunCharterAsync(stateDir, "poll", "--url", url);

            // Distinct exit 4 (NOT 2=clean-empty): the queue state is unknown, so an agent must not hand off on
            // a false "nothing queued". The envelope surfaces a non-null drainError alongside the live session.
            Assert.Equal(4, poll.ExitCode);
            Assert.Contains("queue state is unknown", poll.StdErr);

            using var envelope = JsonDocument.Parse(poll.StdOut.Trim());
            var root = envelope.RootElement;
            Assert.Equal(JsonValueKind.Object, root.GetProperty("session").ValueKind); // probe succeeded
            Assert.Equal(JsonValueKind.String, root.GetProperty("drainError").ValueKind); // failure surfaced
        }
        finally
        {
            stub?.Close();
            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    // ---- charter resolve (§1.6 solo-review companion) -----------------------------------------------------

    [Fact]
    public async Task Resolve_FromLiveServer_AppliesQueuedAnswerInline()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(QuestionPlan);
        Process? review = null;
        try
        {
            (review, var url) = await StartReviewAsync(stateDir, planPath);
            await PostAnswerAsync(url, "q-theme", new[] { "A" });
            Assert.Null(ExtractQuestionAnswer(await File.ReadAllTextAsync(planPath), "q-theme"));

            // resolve discovers the live session by plan path and applies inline (peek → apply → commit).
            var resolve = await RunCharterAsync(stateDir, "resolve", planPath);
            Assert.Equal(0, resolve.ExitCode);

            var resolved = ExtractQuestionAnswer(await File.ReadAllTextAsync(planPath), "q-theme");
            Assert.Equal(new[] { "A" }, resolved);
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Resolve_FromSidecar_NoLiveServer_AppliesQueuedAnswerInline()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(QuestionPlan);
        Process? review = null;
        try
        {
            (review, var url) = await StartReviewAsync(stateDir, planPath);
            await PostAnswerAsync(url, "q-theme", new[] { "A" }); // server persists the durable sidecar

            // Kill the review (NOT clean): the sidecar survives on disk; the descriptor is orphaned pointing at
            // a dead port — the solo case where a human answered then the review process is gone.
            TryKill(review);
            review = null;

            // resolve finds no LIVE server (probe fails), falls back to the durable sidecar, and applies inline.
            var resolve = await RunCharterAsync(stateDir, "resolve", planPath);
            Assert.Equal(0, resolve.ExitCode);

            var resolved = ExtractQuestionAnswer(await File.ReadAllTextAsync(planPath), "q-theme");
            Assert.Equal(new[] { "A" }, resolved);
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Resolve_DuplicateIds_RefusesWithClearError_AndPreservesAnswer()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(DuplicateIdPlan);
        Process? review = null;
        try
        {
            (review, var url) = await StartReviewAsync(stateDir, planPath);
            await PostAnswerAsync(url, "q-theme", new[] { "A" });

            var resolve = await RunCharterAsync(stateDir, "resolve", planPath);
            Assert.Equal(5, resolve.ExitCode);
            Assert.Contains("apply failed", resolve.StdErr);
            Assert.Contains("q-theme", resolve.StdErr);

            // Preserved: the answer stays queued (a plain poll still reports it).
            var poll = await RunCharterAsync(stateDir, "poll", "--url", url);
            using var envelope = JsonDocument.Parse(poll.StdOut.Trim());
            Assert.Equal(1, envelope.RootElement.GetProperty("answers").GetArrayLength());
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Resolve_NoQueuedAnswers_Exits2()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(QuestionPlan);
        Process? review = null;
        try
        {
            (review, _) = await StartReviewAsync(stateDir, planPath);

            // A live session but nothing answered: resolve is a clean no-op (exit 2), not a failure.
            var resolve = await RunCharterAsync(stateDir, "resolve", planPath);
            Assert.Equal(2, resolve.ExitCode);
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    // ---- Descriptor lifecycle (T6) ------------------------------------------------------------------------

    [Fact]
    public async Task Review_WritesDescriptor_AtExpectedPath_WithCorrectFields()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(SimplePlan);
        Process? review = null;
        try
        {
            (review, var url) = await StartReviewAsync(stateDir, planPath);

            var descriptorPath = SessionRegistry.PathForPlan(stateDir, planPath);
            Assert.True(File.Exists(descriptorPath), "review should register a descriptor at the plan's registry path.");

            var descriptor = SessionRegistry.Read(descriptorPath);
            Assert.NotNull(descriptor);
            Assert.Equal(Path.GetFullPath(planPath), Path.GetFullPath(descriptor!.SourcePath));
            Assert.Equal(KeyOf(url), descriptor.Key);
            Assert.Equal(AuthorityOf(url) + "/", descriptor.Address);
            Assert.Equal(review.Id, descriptor.Pid);
            Assert.Equal(SessionDescriptor.CurrentSchema, descriptor.Schema);
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task KilledReview_LeavesDescriptor_Poll_TreatsStale_ExitsNoSession_AndPrunes()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(SimplePlan);
        Process? review = null;
        try
        {
            (review, _) = await StartReviewAsync(stateDir, planPath);
            var descriptorPath = SessionRegistry.PathForPlan(stateDir, planPath);
            Assert.True(File.Exists(descriptorPath));

            // Kill (not clean): the finally-delete never runs, so the descriptor is orphaned on disk.
            TryKill(review);
            review = null;
            Assert.True(File.Exists(descriptorPath), "a killed server should leave its descriptor behind.");

            // poll <plan> proves liveness against the dead port, reports no-session, AND prunes the stale hint.
            var result = await RunCharterAsync(stateDir, "poll", planPath);
            Assert.Equal(3, result.ExitCode);
            Assert.Contains("no running review session", result.StdErr);
            AssertSessionNull(result.StdOut);
            Assert.False(File.Exists(descriptorPath), "poll should prune the stale descriptor.");
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Review_CleanExit_RemovesDescriptor()
    {
        if (OperatingSystem.IsWindows())
        {
            // Delivering a clean interrupt (SIGINT -> Console.CancelKeyPress) to a child is POSIX territory;
            // the removal call itself is exercised cross-platform by the SessionRegistry.Delete unit tests.
            return;
        }

        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(SimplePlan);
        Process? review = null;
        try
        {
            (review, _) = await StartReviewAsync(stateDir, planPath);
            var descriptorPath = SessionRegistry.PathForPlan(stateDir, planPath);
            Assert.True(File.Exists(descriptorPath));

            // SIGINT -> the review handler sets Cancel=true and stops -> the finally removes the descriptor.
            using (var kill = Process.Start("kill", $"-INT {review.Id}"))
            {
                kill?.WaitForExit(5000);
            }

            Assert.True(review.WaitForExit(15000), "review should exit cleanly after SIGINT.");
            Assert.False(File.Exists(descriptorPath), "a clean exit should remove the descriptor.");
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private static void AssertSessionNull(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout.Trim());
        Assert.True(doc.RootElement.TryGetProperty("session", out var session), "envelope should carry a session field.");
        Assert.Equal(JsonValueKind.Null, session.ValueKind);
    }

    /// <summary>
    /// The inline <c>answer</c> array of the <c>:::question</c> whose JSON body carries <paramref name="questionId"/>,
    /// or <c>null</c> when that question is still OPEN (no <c>answer</c> key). It locates the block through the
    /// SAME block model the product uses, so it reads a single-line and a multi-line authored body alike — and
    /// it proves the living-document write landed on disk by re-parsing the ACTUAL file, not the poll stdout.
    /// </summary>
    private static string[]? ExtractQuestionAnswer(string markdown, string questionId)
    {
        foreach (var block in Charter.Core.BlockDocument.Parse(markdown).Blocks)
        {
            if (block.Kind != Charter.Core.BlockKind.Question)
            {
                continue;
            }

            var body = QuestionBody(block.RawContent);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("id", out var id) || id.GetString() != questionId)
            {
                continue;
            }

            if (!doc.RootElement.TryGetProperty("answer", out var answer) || answer.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var values = new List<string>();
            foreach (var element in answer.EnumerateArray())
            {
                values.Add(element.GetString() ?? string.Empty);
            }

            return values.ToArray();
        }

        return null;
    }

    /// <summary>The JSON body of a <c>:::question</c> block's raw content — everything between its fences.</summary>
    private static string QuestionBody(string rawContent)
    {
        var lines = new List<string>(rawContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        lines.RemoveAll(l => l.Trim().StartsWith(":::", StringComparison.Ordinal));
        return string.Join("\n", lines).Trim();
    }

    /// <summary>
    /// Submit an annotation to the running review server via its capability URL — a same-origin POST to
    /// <c>/api/{key}/prompts</c>, exactly as the browser SDK would when the reviewer comments on a block.
    /// </summary>
    private static async Task PostAnnotationAsync(string capabilityUrl, string anchorId, string note)
    {
        var baseUri = new Uri(AuthorityOf(capabilityUrl) + "/");
        var promptsUri = new Uri(baseUri, $"api/{Uri.EscapeDataString(KeyOf(capabilityUrl))}/prompts");

        using var client = new HttpClient();
        var payload = JsonSerializer.Serialize(new { kind = "element", anchorId, note });
        using var request = new HttpRequestMessage(HttpMethod.Post, promptsUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", AuthorityOf(capabilityUrl));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"seed annotation POST should succeed, got {(int)response.StatusCode}.");
    }

    /// <summary>
    /// Start a stub loopback server that PROVES LIVE — <c>GET /api/sessions</c> returns 200 with the plan's
    /// sourcePath so <c>poll</c>'s liveness probe succeeds — but FAILS every drain (<c>/api/poll</c> and
    /// <c>/api/answers</c> return 500). This reproduces a transport failure mid-drain deterministically, so
    /// the test can assert <c>poll</c> surfaces a drainError and exits 4 (not a false clean-empty). The caller
    /// closes the returned listener.
    /// </summary>
    private static (HttpListener Listener, string Url) StartFailingDrainStub(string sourcePath)
    {
        const string key = "stubkey0000000000000";
        var port = ReserveEphemeralPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    break; // listener closed
                }

                _ = Task.Run(() => RespondStub(context, sourcePath));
            }
        });

        return (listener, $"http://127.0.0.1:{port}/?key={key}");
    }

    private static void RespondStub(HttpListenerContext context, string sourcePath)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path.Contains("sessions", StringComparison.Ordinal))
            {
                var json = JsonSerializer.Serialize(
                    new { sourcePath, sourceFile = Path.GetFileName(sourcePath) });
                var payload = Encoding.UTF8.GetBytes(json);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.OutputStream.Write(payload, 0, payload.Length);
            }
            else
            {
                // Every drain fails: the exact transport failure a swallow-to-empty would misreport as clean.
                context.Response.StatusCode = 500;
            }
        }
        catch (Exception)
        {
            // Best-effort stub.
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (Exception)
            {
            }
        }
    }

    private static int ReserveEphemeralPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static string KeyOf(string capabilityUrl)
        => Regex.Match(capabilityUrl, "key=([0-9a-f]+)").Groups[1].Value;

    private static string AuthorityOf(string capabilityUrl)
        => new Uri(capabilityUrl).GetLeftPart(UriPartial.Authority);

    /// <summary>
    /// Submit an answer to the running review server via its capability URL — a same-origin POST to
    /// <c>/api/{key}/answers</c>, exactly as the browser SDK would.
    /// </summary>
    private static async Task PostAnswerAsync(string capabilityUrl, string questionId, string[] values)
    {
        var baseUri = new Uri(AuthorityOf(capabilityUrl) + "/");
        var answersUri = new Uri(baseUri, $"api/{Uri.EscapeDataString(KeyOf(capabilityUrl))}/answers");

        using var client = new HttpClient();
        var payload = JsonSerializer.Serialize(new { questionId, mode = "single", values, target = "human" });
        using var request = new HttpRequestMessage(HttpMethod.Post, answersUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", AuthorityOf(capabilityUrl));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"seed answer POST should succeed, got {(int)response.StatusCode}.");
    }

    /// <summary>
    /// Click <b>Send to agent</b> — a same-origin POST to <c>/api/{key}/review/submit</c>, exactly as the
    /// review panel's button does when the reviewer hands the round off.
    /// </summary>
    private static async Task PostReviewSubmitAsync(string capabilityUrl)
    {
        var baseUri = new Uri(AuthorityOf(capabilityUrl) + "/");
        var submitUri = new Uri(baseUri, $"api/{Uri.EscapeDataString(KeyOf(capabilityUrl))}/review/submit");

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, submitUri)
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", AuthorityOf(capabilityUrl));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"review submit POST should succeed, got {(int)response.StatusCode}.");
    }

    /// <summary>
    /// Start <c>charter review &lt;plan&gt; --no-open</c> as a child process and read stdout until it prints the
    /// ready URL. stderr is drained in the background so a warning line can never fill the pipe and stall the
    /// child. The caller owns killing the returned process.
    /// </summary>
    private static async Task<(Process Process, string Url)> StartReviewAsync(string stateDir, string planPath)
    {
        var process = Process.Start(MakeStartInfo(stateDir, "review", planPath, "--no-open"))
            ?? throw new XunitException("Failed to start charter review.");

        // Drain stderr in the background (fire-and-forget) so the child never blocks writing to a full pipe.
        _ = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break; // EOF: the process exited without a ready line.
                }

                var match = ReadyUrl.Match(line);
                if (match.Success)
                {
                    return (process, match.Value);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fell through to the failure below.
        }

        TryKill(process);
        throw new XunitException("charter review did not print a ready URL in time.");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCharterAsync(
        string? stateDir, params string[] args)
    {
        using var process = Process.Start(MakeStartInfo(stateDir, args))
            ?? throw new XunitException("Failed to start the charter CLI process.");

        // Read both streams concurrently before waiting so a full pipe buffer cannot deadlock the child.
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new XunitException($"charter {string.Join(' ', args)} did not exit within the deadline.");
        }

        return (process.ExitCode, await stdOutTask, await stdErrTask);
    }

    private static ProcessStartInfo MakeStartInfo(string? stateDir, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(CharterCliDllPath());
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (stateDir is not null)
        {
            startInfo.Environment["CHARTER_STATE_DIR"] = stateDir;
        }

        return startInfo;
    }

    private static string CharterCliDllPath()
    {
        AssemblyMetadataAttribute? metadata = System.Linq.Enumerable.FirstOrDefault(
            typeof(PollCommandTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
            attribute => attribute.Key == "CharterCliPath");

        string? path = metadata?.Value;
        Assert.False(string.IsNullOrEmpty(path), "The build did not set the CharterCliPath assembly metadata.");
        Assert.True(File.Exists(path), $"Built Charter.Cli.dll not found at '{path}'.");
        return path!;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best-effort teardown.
        }

        try
        {
            process.WaitForExit(5000);
        }
        catch (Exception)
        {
            // Ignore.
        }
    }

    private static string WriteTempPlan(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "charter-poll-plan-" + Guid.NewGuid().ToString("N") + ".charter.md");
        File.WriteAllText(path, content);
        return path;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-poll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup.
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup.
        }
    }
}
