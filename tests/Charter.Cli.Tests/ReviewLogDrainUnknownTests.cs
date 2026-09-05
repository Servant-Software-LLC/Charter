using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Charter.Core;
using Charter.Server;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #221 for the consumer whose failure is SILENT: the server-less drain behind <c>charter poll</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReviewLogStore.Read(string)"/> now separates <i>the directory was read and holds no logs</i>
/// (<see cref="ReviewLogOutcome.Empty"/>) from <i>the directory was not there to read</i>
/// (<see cref="ReviewLogOutcome.Unknown"/>). The drain does not yet act on the difference: an Unknown read
/// carries no comments, so <c>fresh</c> is empty and <c>poll</c> answers with the confident vocabulary of a
/// clean queue. An agent draining a review round is told <b>the reviewer said nothing</b> — the panel's
/// version of this bug is merely visible and transient; this one is wrong and quiet.
/// </para>
/// <para>
/// Exit <c>4</c> already exists and is already documented as <i>"the drain could not complete, so the queue
/// state is UNKNOWN — never read this as 'nothing queued'"</i> (<see cref="ReviewExitCodes.DrainFailed"/>).
/// These tests make it reachable for this cause; they redefine neither it nor
/// <see cref="ReviewExitCodes.CleanEmpty"/>.
/// </para>
/// <para>
/// <b>These run the REAL binary as a child process</b> with an isolated <c>CHARTER_STATE_DIR</c>, because the
/// exit code IS the contract — it is what an agent branches on, and it exists nowhere else.
/// </para>
/// </remarks>
[Trait("Category", "ReviewLogDrainUnknown")]
public class ReviewLogDrainUnknownTests
{
    private const string Plan =
        "# Drain Unknown Plan\n" +
        "\n" +
        "An overview paragraph introducing the plan under review.\n" +
        "\n" +
        "The paragraph a teammate comments on from another machine.\n";

    private static readonly ReviewAuthor Bob = new("Bob Chen", "bob@example.com");

    /// <summary>The stderr line <c>poll</c> prints when it found no session and no log at all (exit 3).</summary>
    private const string NoSessionClaim = "no running review session";

    /// <summary>
    /// <b>The headline.</b> A review log this machine has been reading, whose directory then goes away —
    /// a branch switch, a <c>git checkout</c> replacing the tree, a pull mid-flight — must report the queue
    /// state as UNKNOWN (exit 4), never as a clean empty (exit 2). Exit 2 says <i>"a queue was found and it
    /// was EMPTY"</i>, and that is a claim about what the reviewer said; nothing here read a queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The first assertion is the CONTROL, and it is load-bearing.</b> <c>.review/</c> is created lazily on
    /// the first append (<c>docs/plans/03-git-mediated-team-review.md</c> §5.0), so a plan nobody has commented
    /// on has no directory beside it — that is the ORDINARY state of a solo review, it is already pinned as
    /// exit 3 by <c>ReviewLogPollTests.Poll_WithNoSessionAndNoLog_StillExits3_WithSessionNull</c>, and the plan's
    /// own "what must not regress" forbids it becoming a warning or an error. So the drain cannot simply route
    /// every Unknown read to exit 4: an absent directory reads Unknown either way, and the two cases are told
    /// apart by what this machine already knows. Here it knows a great deal — it has been HANDED a record from
    /// that directory, which is recorded in the consumption ledger the drain already loads
    /// (<see cref="ReviewLogLedger.Count"/>). The directory demonstrably existed; its absence now is a failed
    /// read, not a finding.
    /// </para>
    /// <para>
    /// Any evidence that satisfies both halves is a fair implementation — the ledger is simply the one the
    /// drain already has in hand.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_unknown_review_log_exits_four_not_two()
    {
        var stateDir = CharterCliRunner.NewTempDirectory();
        var plan = WritePlan();
        try
        {
            // CONTROL — a plan nobody has commented on. No directory, and nothing this machine has ever been
            // handed from one: the honest answer is "no session and no readable log", exactly as today.
            var untouched = Poll(stateDir, plan);
            Assert.Equal(ReviewExitCodes.NoSession, untouched.ExitCode);

            // Bob commits an objection and this machine is handed it.
            new ReviewLogWriter(plan, Bob).AppendCreate(
                Anchor(), "the objection an agent must not be told was never made");

            var delivered = Poll(stateDir, plan);
            Assert.Equal(ReviewExitCodes.Drained, delivered.ExitCode);
            Assert.Single(Annotations(delivered.StdOut));

            // ...and now the directory it came from is gone.
            Directory.Delete(ReviewLogPaths.DirectoryForPlan(plan), recursive: true);

            var unknown = Poll(stateDir, plan);

            // Not 2 ("a queue was found and it was EMPTY"), which is what tells an agent the reviewer said
            // nothing, and not 3 ("no session and no readable log"), which claims there is no log at all.
            Assert.Equal(ReviewExitCodes.DrainFailed, unknown.ExitCode);
        }
        finally
        {
            Cleanup(stateDir, plan);
        }
    }

    /// <summary>
    /// <b>The discriminator, and why this is not a one-line change.</b> A <c>.review/</c> that is THERE and
    /// holds no logs is a POSITIVE finding — the queue was read and nobody has commented — so it keeps the
    /// clean-empty exit 2 that exit 2 was always right about. Collapse this into the Unknown branch and the
    /// fix would trade a silent false negative for a permanent false alarm on every plan awaiting its first
    /// comment.
    /// </summary>
    [Fact]
    public void A_genuinely_empty_review_log_still_exits_two()
    {
        var stateDir = CharterCliRunner.NewTempDirectory();
        var plan = WritePlan();
        try
        {
            // The directory exists and holds no *.jsonl — read, and found empty. This is a finding, not a gap.
            Directory.CreateDirectory(ReviewLogPaths.DirectoryForPlan(plan));

            var result = Poll(stateDir, plan);

            Assert.Equal(ReviewExitCodes.CleanEmpty, result.ExitCode);

            // ...and the envelope says so structurally: the LOG answered, it just had nothing in it.
            var root = Root(result.StdOut);
            Assert.Equal("review-log", root.GetProperty("source").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("drainError").ValueKind);
            Assert.Empty(root.GetProperty("annotations").EnumerateArray());
        }
        finally
        {
            Cleanup(stateDir, plan);
        }
    }

    /// <summary>
    /// The exit code is what an agent branches on, but the stderr line is what a human reads when the branch
    /// surprises them — and it must not repeat the very claim the exit code exists to withhold. On exit 4 it
    /// says the state is UNKNOWN; it must not fall through to <c>poll</c>'s no-session line, which asserts
    /// there was nothing beside the plan to read.
    /// </summary>
    [Fact]
    public void The_unknown_exit_says_unknown_not_nothing_queued()
    {
        var stateDir = CharterCliRunner.NewTempDirectory();
        var plan = WritePlan();
        try
        {
            var unknown = PollAnUnreadableReviewLog(stateDir, plan);

            Assert.Equal(ReviewExitCodes.DrainFailed, unknown.ExitCode);
            Assert.Contains("unknown", unknown.StdErr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(NoSessionClaim, unknown.StdErr, StringComparison.Ordinal);

            // The envelope carries the reason too, so a program reading stdout alone can see WHY it came back
            // with no comments rather than inferring that there were none.
            var root = Root(unknown.StdOut);
            Assert.Equal("review-log", root.GetProperty("source").GetString());
            Assert.NotEqual(JsonValueKind.Null, root.GetProperty("drainError").ValueKind);
            Assert.Empty(root.GetProperty("annotations").EnumerateArray());
        }
        finally
        {
            Cleanup(stateDir, plan);
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    /// <summary>
    /// Drive the plan into the state this whole file is about: a review log this machine has read and been
    /// handed a record from, whose directory is then no longer there. See
    /// <see cref="An_unknown_review_log_exits_four_not_two"/> for why the delivery is part of the arrangement
    /// and not incidental setup.
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) PollAnUnreadableReviewLog(
        string stateDir, string plan)
    {
        new ReviewLogWriter(plan, Bob).AppendCreate(Anchor(), "a comment the drain has already handed over");

        var delivered = Poll(stateDir, plan);
        Assert.Equal(ReviewExitCodes.Drained, delivered.ExitCode);

        Directory.Delete(ReviewLogPaths.DirectoryForPlan(plan), recursive: true);

        return Poll(stateDir, plan);
    }

    private static (int ExitCode, string StdOut, string StdErr) Poll(string stateDir, string plan)
        => CharterCliRunner.RunWith(
            workingDirectory: null,
            environment: new Dictionary<string, string> { ["CHARTER_STATE_DIR"] = stateDir },
            "poll", plan);

    private static ReviewAnchor Anchor()
        => new(AnchorAt(2), "element", "the read path", null);

    private static string AnchorAt(int index)
        => SourceMap.Build(Plan).Anchors.OrderBy(anchor => anchor, StringComparer.Ordinal).ElementAt(index);

    private static JsonElement Root(string stdout)
    {
        using var document = JsonDocument.Parse(stdout);
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<JsonElement> Annotations(string stdout)
        => Root(stdout).GetProperty("annotations").EnumerateArray().Select(item => item.Clone()).ToList();

    private static string WritePlan()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "charter-drain-unknown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "team.charter.md");
        File.WriteAllText(path, Plan);
        return path;
    }

    private static void Cleanup(string stateDir, string plan)
    {
        CharterCliRunner.TryDeleteDirectory(stateDir);
        CharterCliRunner.TryDeleteDirectory(Path.GetDirectoryName(plan)!);
    }
}
