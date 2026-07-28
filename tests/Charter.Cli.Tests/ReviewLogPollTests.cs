using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Charter.Core;
using Charter.Server;
using Xunit;
using Xunit.Sdk;

namespace Charter.Cli.Tests;

/// <summary>
/// Step 4 of <c>docs/plans/03-git-mediated-team-review.md</c>: the SERVER-LESS <c>charter poll</c> read path.
/// Without it the design's payoff is unreachable — A's agent reading B's committed comments would require A to
/// be running <c>charter review</c>, and A is not: A is EXECUTING. These tests run the REAL binary as a child
/// process with an isolated <c>CHARTER_STATE_DIR</c>, so both the wire contract and the machine-locality of the
/// consumption ledger are exercised exactly as an agent would meet them.
/// </summary>
[Trait("Category", "Cli")]
public class ReviewLogPollTests
{
    private const string Plan =
        "# Team Review Poll Plan\n" +
        "\n" +
        "An overview paragraph introducing the plan under review.\n" +
        "\n" +
        "The paragraph a teammate comments on from another machine.\n";

    private static readonly ReviewAuthor Bob = new("Bob Chen", "bob@example.com");
    private static readonly ReviewAuthor Carol = new("Carol Diaz", "carol@example.com");

    private static readonly Regex ReadyUrl =
        new(@"https?://127\.0\.0\.1:\d+/\?key=[0-9a-f]+", RegexOptions.Compiled);

    /// <summary>
    /// The headline: no server running anywhere, and <c>poll &lt;plan&gt;</c> still hands the agent the
    /// comments a teammate committed — with their author, their actor, and their status.
    /// </summary>
    [Fact]
    public async Task Poll_WithNoServerRunning_ReportsTeammatesCommittedComments()
    {
        var stateDir = NewTempDir();
        var plan = WritePlan();
        try
        {
            var bobs = new ReviewLogWriter(plan, Bob)
                .AppendCreate(Anchor(AnchorAt(2)), "The write path needs a retry budget.");

            var result = await RunCharterAsync(stateDir, "poll", plan);

            Assert.Equal(0, result.ExitCode);
            using var envelope = JsonDocument.Parse(result.StdOut);
            var root = envelope.RootElement;

            // The envelope shape is unchanged; `source` is additive and says where this came from.
            Assert.Equal(JsonValueKind.Null, root.GetProperty("session").ValueKind);
            Assert.Equal("review-log", root.GetProperty("source").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("drainError").ValueKind);
            Assert.Equal(1, root.GetProperty("drained").GetProperty("annotations").GetInt32());
            Assert.Empty(root.GetProperty("answers").EnumerateArray());

            var annotation = Assert.Single(root.GetProperty("annotations").EnumerateArray().ToList());
            Assert.Equal(bobs.Id, annotation.GetProperty("id").GetString());
            Assert.Equal("The write path needs a retry budget.", annotation.GetProperty("note").GetString());
            Assert.Equal(AnchorAt(2), annotation.GetProperty("anchorId").GetString());
            Assert.Equal("resolved", annotation.GetProperty("anchorStatus").GetString());
            Assert.Equal(
                SourceMap.Build(Plan).LineForAnchor(AnchorAt(2)),
                annotation.GetProperty("sourceLine").GetInt32());

            var review = annotation.GetProperty("review");
            Assert.Equal("Bob Chen", review.GetProperty("authorName").GetString());
            Assert.Equal(Bob.Email, review.GetProperty("authorEmail").GetString());
            Assert.Equal("human", review.GetProperty("actor").GetString());
            Assert.Equal("open", review.GetProperty("status").GetString());
        }
        finally
        {
            Cleanup(stateDir, plan);
        }
    }

    /// <summary>
    /// The ledger: a record delivered once on this machine is not delivered again. It is machine-local and
    /// deliberately NOT a log record — A's agent consuming must not mark a comment handled for B.
    /// </summary>
    [Fact]
    public async Task Poll_Twice_DoesNotRedeliver_AndTheSecondRunIsACleanEmpty()
    {
        var stateDir = NewTempDir();
        var plan = WritePlan();
        try
        {
            new ReviewLogWriter(plan, Bob).AppendCreate(Anchor(AnchorAt(2)), "a comment delivered exactly once");

            var first = await RunCharterAsync(stateDir, "poll", plan);
            Assert.Equal(0, first.ExitCode);
            Assert.Single(Annotations(first.StdOut));

            var second = await RunCharterAsync(stateDir, "poll", plan);
            Assert.Equal(2, second.ExitCode);
            Assert.Empty(Annotations(second.StdOut));
            Assert.Equal("review-log", Root(second.StdOut).GetProperty("source").GetString());
        }
        finally
        {
            Cleanup(stateDir, plan);
        }
    }

    /// <summary>
    /// Machine-locality, proved rather than asserted: the SAME committed log, read with a different per-user
    /// state directory, is delivered again — because the ledger belongs to a machine, not to the review.
    /// </summary>
    [Fact]
    public async Task TheLedgerIsMachineLocal_AnotherStateDirectoryStillReceivesTheSameComment()
    {
        var machineA = NewTempDir();
        var machineB = NewTempDir();
        var plan = WritePlan();
        try
        {
            var comment = new ReviewLogWriter(plan, Bob)
                .AppendCreate(Anchor(AnchorAt(2)), "one comment, two teammates' agents");

            var onA = await RunCharterAsync(machineA, "poll", plan);
            Assert.Equal(0, onA.ExitCode);
            Assert.Equal(comment.Id, Assert.Single(Annotations(onA.StdOut)).GetProperty("id").GetString());

            var againOnA = await RunCharterAsync(machineA, "poll", plan);
            Assert.Equal(2, againOnA.ExitCode);

            // A different state dir is a different machine's agent: it has never seen this record.
            var onB = await RunCharterAsync(machineB, "poll", plan);
            Assert.Equal(0, onB.ExitCode);
            Assert.Equal(comment.Id, Assert.Single(Annotations(onB.StdOut)).GetProperty("id").GetString());
        }
        finally
        {
            Cleanup(machineA, plan);
            TryDeleteDir(machineB);
        }
    }

    /// <summary>
    /// A comment that changed since it was delivered comes back — a later edit / resolve / reply mints a NEW
    /// record id, and the agent is being told something new about it, not the same thing twice.
    /// </summary>
    [Fact]
    public async Task Poll_RedeliversAComment_AfterItIsResolvedByATeammate()
    {
        var stateDir = NewTempDir();
        var plan = WritePlan();
        try
        {
            var bob = new ReviewLogWriter(plan, Bob);
            var comment = bob.AppendCreate(Anchor(AnchorAt(2)), "an objection the team then settles");

            Assert.Equal(0, (await RunCharterAsync(stateDir, "poll", plan)).ExitCode);
            Assert.Equal(2, (await RunCharterAsync(stateDir, "poll", plan)).ExitCode);

            new ReviewLogWriter(plan, Carol).AppendResolve(comment.Id, prev: null);

            var afterResolve = await RunCharterAsync(stateDir, "poll", plan);
            Assert.Equal(0, afterResolve.ExitCode);
            var annotation = Assert.Single(Annotations(afterResolve.StdOut));
            Assert.Equal("resolved", annotation.GetProperty("review").GetProperty("status").GetString());
        }
        finally
        {
            Cleanup(stateDir, plan);
        }
    }

    /// <summary>
    /// A withdrawn comment and one whose block has changed are MARKED, never silently dropped: the agent needs
    /// to know it is looking at a note the author took back, or at a note whose block moved.
    /// </summary>
    [Fact]
    public async Task Poll_MarksRetractedAndOrphanedComments_RatherThanDroppingThem()
    {
        var stateDir = NewTempDir();
        var plan = WritePlan();
        try
        {
            var bob = new ReviewLogWriter(plan, Bob);

            var withdrawn = bob.AppendCreate(Anchor(AnchorAt(2)), "a note Bob takes back");
            bob.AppendRetract(withdrawn.Id, prev: null);

            var orphan = bob.AppendCreate(
                new ReviewAnchor("b-a-block-this-plan-no-longer-has", "element", "the read path", null),
                "a note whose block changed");

            var result = await RunCharterAsync(stateDir, "poll", plan);
            Assert.Equal(0, result.ExitCode);

            var annotations = Annotations(result.StdOut);
            Assert.Equal(2, annotations.Count);

            var retracted = annotations.Single(a => a.GetProperty("id").GetString() == withdrawn.Id);
            Assert.Equal("retracted", retracted.GetProperty("review").GetProperty("status").GetString());
            Assert.Equal("(comment withdrawn by author)", retracted.GetProperty("note").GetString());

            var orphaned = annotations.Single(a => a.GetProperty("id").GetString() == orphan.Id);
            Assert.Equal("orphaned", orphaned.GetProperty("anchorStatus").GetString());
            Assert.Equal(JsonValueKind.Null, orphaned.GetProperty("sourceLine").ValueKind);
            Assert.Equal("the read path", orphaned.GetProperty("quote").GetString());
            Assert.Equal("a note whose block changed", orphaned.GetProperty("note").GetString());
        }
        finally
        {
            Cleanup(stateDir, plan);
        }
    }

    /// <summary>No session AND no readable log is still exit 3 — an agent branching on 3 behaves as today.</summary>
    [Fact]
    public async Task Poll_WithNoSessionAndNoLog_StillExits3_WithSessionNull()
    {
        var stateDir = NewTempDir();
        var plan = WritePlan();
        try
        {
            var result = await RunCharterAsync(stateDir, "poll", plan);

            Assert.Equal(3, result.ExitCode);
            Assert.Contains("no running review session", result.StdErr, StringComparison.Ordinal);

            var root = Root(result.StdOut);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("session").ValueKind);
            Assert.Equal("session", root.GetProperty("source").GetString());
            Assert.Empty(root.GetProperty("annotations").EnumerateArray());
        }
        finally
        {
            Cleanup(stateDir, plan);
        }
    }

    /// <summary>
    /// A log that cannot be READ means the review state is UNKNOWN, not empty: exit 4, with the reason on the
    /// envelope, so an agent never proceeds on a false "nothing queued".
    /// </summary>
    [Fact]
    public async Task Poll_WithAnUnreadableLog_Exits4_AndSaysWhy()
    {
        var stateDir = NewTempDir();
        var plan = WritePlan();
        try
        {
            var bob = new ReviewLogWriter(plan, Bob);
            bob.AppendCreate(Anchor(AnchorAt(2)), "a comment behind a lock");

            // Hold the log with FileShare.None, exactly as another Charter mid-append would.
            using (new FileStream(bob.LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = await RunCharterAsync(stateDir, "poll", plan);

                Assert.Equal(4, result.ExitCode);
                var root = Root(result.StdOut);
                Assert.Equal("review-log", root.GetProperty("source").GetString());
                Assert.NotEqual(JsonValueKind.Null, root.GetProperty("drainError").ValueKind);
                Assert.Contains("could not read", root.GetProperty("drainError").GetString()!, StringComparison.Ordinal);
                Assert.Contains("not reporting 'nothing queued'", result.StdErr, StringComparison.Ordinal);
            }
        }
        finally
        {
            Cleanup(stateDir, plan);
        }
    }

    /// <summary>
    /// A LIVE session still takes precedence — the committed log is the fallback, never a competitor. The
    /// running server's own (empty) queue answers, and the committed comment is NOT reported.
    /// </summary>
    [Fact]
    public async Task ALiveSession_TakesPrecedenceOverTheCommittedLog()
    {
        var stateDir = NewTempDir();
        var plan = WritePlan();
        Process? review = null;
        try
        {
            new ReviewLogWriter(plan, Bob).AppendCreate(Anchor(AnchorAt(2)), "a committed comment poll must NOT use");

            review = await StartReviewAsync(stateDir, plan);
            var result = await RunCharterAsync(stateDir, "poll", plan);

            // The live server's pre-drain queue is empty, so this is a clean empty FROM THE SESSION — the
            // envelope names a live session and the source is the session, not the log.
            Assert.Equal(2, result.ExitCode);
            var root = Root(result.StdOut);
            Assert.Equal("session", root.GetProperty("source").GetString());
            Assert.NotEqual(JsonValueKind.Null, root.GetProperty("session").ValueKind);
            Assert.Empty(root.GetProperty("annotations").EnumerateArray());
        }
        finally
        {
            TryKill(review);
            Cleanup(stateDir, plan);
        }
    }

    /// <summary>
    /// The capability key must never reach stdout or stderr — the review-log path adds no new place for it to
    /// leak, and this asserts it directly on a run that also had a live session.
    /// </summary>
    [Fact]
    public async Task Poll_NeverPrintsTheCapabilityKey()
    {
        var stateDir = NewTempDir();
        var plan = WritePlan();
        Process? review = null;
        try
        {
            review = await StartReviewAsync(stateDir, plan);
            var result = await RunCharterAsync(stateDir, "poll", plan);
            var combined = result.StdOut + "\n" + result.StdErr;

            Assert.DoesNotContain("key=", combined, StringComparison.Ordinal);
            Assert.DoesNotContain("\"key\"", combined, StringComparison.Ordinal);
        }
        finally
        {
            TryKill(review);
            Cleanup(stateDir, plan);
        }
    }

    // ---- #74: staleness LABELS, it never withholds (§4.3.1) ------------------------------------------------

    /// <summary>
    /// <b>The normative guard.</b> This is the #67 scenario exactly — a plan deleted and a completely different
    /// document authored at the same path — reproduced on the COMMITTED review log, where the sidecar's
    /// quarantine rule fires on every clause: there is at least one comment, the plan is not byte-identical to
    /// the revision they were written against, and NOT ONE anchor resolves.
    /// <para>
    /// Every comment is delivered anyway. That is the resolution of Charter #74 (§4.3.1): the quarantine does
    /// not cross over to a shared, permanent, git-tracked log, because its remedy (copy aside, rewrite the live
    /// file) does not exist there and its evidence has a high benign base rate — a fresh clone or a second
    /// machine has an empty consumption ledger, so a mature plan's whole review history arrives at once and
    /// nearly all of it is legitimately orphaned. A rule that fired there would silence a committed objection
    /// with no local remedy.
    /// </para>
    /// <para>
    /// <b>If this test ever fails by delivering fewer comments, the fix is not to relax the assertion.</b>
    /// </para>
    /// </summary>
    [Fact]
    public async Task Poll_WhenTheDocumentAtThisPathWasREPLACED_StillDeliversEveryComment_Labelled()
    {
        var stateDir = NewTempDir();
        var plan = WritePlan(ReplacedOldPlan);
        try
        {
            var bob = new ReviewLogWriter(plan, Bob);
            var written = OldPlanAnchorsNotSharedWithTheReplacement()
                .Select((anchor, i) => bob.AppendCreate(
                    new ReviewAnchor(anchor, "element", "quoted from the old document", BaseOf(ReplacedOldPlan)),
                    "objection " + i + " about a document that is no longer here"))
                .ToList();

            Assert.True(written.Count >= 2, "the fixture must carry more than one doomed anchor");

            // The plan is deleted and a different document is authored at the same path.
            File.WriteAllText(plan, ReplacedNewPlan);

            var result = await RunCharterAsync(stateDir, "poll", plan);

            Assert.Equal(0, result.ExitCode);
            var annotations = Annotations(result.StdOut);

            // NOTHING is withheld: every comment the fold holds is delivered...
            Assert.Equal(written.Count, annotations.Count);
            Assert.All(written, record => Assert.Contains(
                annotations, a => a.GetProperty("id").GetString() == record.Id));

            // ...and the quarantine's own trigger condition genuinely held: not one anchor resolves.
            Assert.All(annotations, a => Assert.Equal("orphaned", a.GetProperty("anchorStatus").GetString()));

            // The evidence travels instead of the suppression: each says which revision it was written against.
            Assert.All(annotations, a =>
            {
                Assert.Equal("different", a.GetProperty("baseStatus").GetString());
                Assert.Equal(BaseOf(ReplacedOldPlan), a.GetProperty("base").GetString());
            });
        }
        finally
        {
            Cleanup(stateDir, plan);
        }
    }

    /// <summary>
    /// #67's <b>worst</b> finding, which orphan-labelling alone never addressed: a comment from the deleted
    /// document whose content-derived anchor COLLIDES with a block carried into the replacement (an identical
    /// boilerplate line) arrives looking exactly like fresh feedback — <c>anchorStatus: "resolved"</c>, a
    /// plausible source line — with no way for an agent to tell it apart.
    /// <para>
    /// It still arrives (nothing is withheld), but it now names the revision it was actually written against.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Poll_AColludingAnchorFromTheReplacedDocument_ResolvesButSaysWhichRevisionItSaw()
    {
        var stateDir = NewTempDir();
        var plan = WritePlan(ReplacedOldPlan);
        try
        {
            var shared = SharedAnchor();
            var comment = new ReviewLogWriter(plan, Bob).AppendCreate(
                new ReviewAnchor(shared, "element", "Status: Draft for review", BaseOf(ReplacedOldPlan)),
                "Please add line breaks here as appropriate");

            File.WriteAllText(plan, ReplacedNewPlan);

            var result = await RunCharterAsync(stateDir, "poll", plan);

            Assert.Equal(0, result.ExitCode);
            var annotation = Assert.Single(Annotations(result.StdOut));
            Assert.Equal(comment.Id, annotation.GetProperty("id").GetString());

            // The collision is real: the anchor resolves in a document the comment was never written about.
            Assert.Equal("resolved", annotation.GetProperty("anchorStatus").GetString());
            Assert.NotEqual(JsonValueKind.Null, annotation.GetProperty("sourceLine").ValueKind);

            // ...and THIS is what now distinguishes it from genuinely fresh feedback.
            Assert.Equal("different", annotation.GetProperty("baseStatus").GetString());
            Assert.Equal(BaseOf(ReplacedOldPlan), annotation.GetProperty("base").GetString());
        }
        finally
        {
            Cleanup(stateDir, plan);
        }
    }

    /// <summary>
    /// The sound positive: a comment written against the plan as it stands reads <c>current</c>. This is the
    /// only <c>baseStatus</c> value that asserts anything — byte-identical text at this path IS this document.
    /// </summary>
    [Fact]
    public async Task Poll_ACommentWrittenAgainstThePlanAsItStands_ReadsCurrent()
    {
        var stateDir = NewTempDir();
        var plan = WritePlan();
        try
        {
            new ReviewLogWriter(plan, Bob).AppendCreate(
                new ReviewAnchor(AnchorAt(2), "element", "the write path", BaseOf(Plan)),
                "a comment on the plan exactly as it is now");

            var result = await RunCharterAsync(stateDir, "poll", plan);

            Assert.Equal(0, result.ExitCode);
            var annotation = Assert.Single(Annotations(result.StdOut));
            Assert.Equal("current", annotation.GetProperty("baseStatus").GetString());
            Assert.Equal("resolved", annotation.GetProperty("anchorStatus").GetString());
        }
        finally
        {
            Cleanup(stateDir, plan);
        }
    }

    /// <summary>
    /// A record that never recorded which revision it saw reads <c>unknown</c> — never <c>different</c>, which
    /// would be a claim, and never omitted, which would leave an agent to guess. The raw <c>base</c> is simply
    /// absent from the wire, exactly as <c>review</c> is absent from a pending-queue annotation.
    /// </summary>
    [Fact]
    public async Task Poll_ACommentWithNoRecordedRevision_ReadsUnknown_AndOmitsBase()
    {
        var stateDir = NewTempDir();
        var plan = WritePlan();
        try
        {
            new ReviewLogWriter(plan, Bob).AppendCreate(Anchor(AnchorAt(2)), "a comment from before base was stamped");

            var result = await RunCharterAsync(stateDir, "poll", plan);

            Assert.Equal(0, result.ExitCode);
            var annotation = Assert.Single(Annotations(result.StdOut));
            Assert.Equal("unknown", annotation.GetProperty("baseStatus").GetString());
            Assert.False(annotation.TryGetProperty("base", out _));
        }
        finally
        {
            Cleanup(stateDir, plan);
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    // The #67 fixture: one document, then a COMPLETELY different one at the same path. The "Status: Draft"
    // line is byte-identical and unique in both, so it keeps its pure content-derived id in each — which is
    // exactly how an anchor from the dead document collides with a block in the replacement.
    private const string ReplacedOldPlan =
        "# Launcher Policy Plan\n" +
        "\n" +
        "Status: Draft for review\n" +
        "\n" +
        "The rollout uses PinnedVersion to hold a fleet on a known build.\n" +
        "\n" +
        "Precedence between the machine policy and the user policy is undefined.\n";

    private const string ReplacedNewPlan =
        "# Tenant Rate Limit Plan\n" +
        "\n" +
        "Status: Draft for review\n" +
        "\n" +
        "Each tenant gets a token bucket sized from its contracted throughput.\n" +
        "\n" +
        "Buckets refill on a wall clock, never on request arrival.\n";

    /// <summary>The anchor the two documents share, which is what makes the collision reachable.</summary>
    private static string SharedAnchor()
    {
        var shared = AnchorsOf(ReplacedOldPlan).Intersect(AnchorsOf(ReplacedNewPlan), StringComparer.Ordinal).ToList();
        return Assert.Single(shared);
    }

    /// <summary>Every anchor of the old document that the replacement does NOT carry — all of them doomed.</summary>
    private static IReadOnlyList<string> OldPlanAnchorsNotSharedWithTheReplacement()
        => AnchorsOf(ReplacedOldPlan).Except(AnchorsOf(ReplacedNewPlan), StringComparer.Ordinal).ToList();

    private static IReadOnlyList<string> AnchorsOf(string markdown)
        => SourceMap.Build(markdown).Anchors.OrderBy(a => a, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The plan's content hash in the <c>anchor.base</c> wire format, computed here independently of the
    /// implementation that mints it — <c>base</c> is a committed, immutable artifact, so a change to how it is
    /// derived must break a test rather than quietly making every existing record read <c>different</c>.
    /// </summary>
    private static string BaseOf(string markdown)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(markdown))).ToLowerInvariant();

    private static string AnchorAt(int index)
        => SourceMap.Build(Plan).Anchors.OrderBy(a => a, StringComparer.Ordinal).ElementAt(index);

    private static ReviewAnchor Anchor(string blockId)
        => new(blockId, "element", "the write path", null);

    private static JsonElement Root(string stdout)
    {
        using var document = JsonDocument.Parse(stdout);
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<JsonElement> Annotations(string stdout)
        => Root(stdout).GetProperty("annotations").EnumerateArray().Select(e => e.Clone()).ToList();

    private static string WritePlan(string? markdown = null)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "charter-review-poll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "team.charter.md");
        File.WriteAllText(path, markdown ?? Plan);
        return path;
    }

    private static void Cleanup(string stateDir, string plan)
    {
        TryDeleteDir(stateDir);
        TryDeleteDir(Path.GetDirectoryName(plan)!);
    }

    private static async Task<Process> StartReviewAsync(string stateDir, string plan)
    {
        var process = Process.Start(MakeStartInfo(stateDir, "review", plan, "--no-open"))
            ?? throw new XunitException("Failed to start the charter review process.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break;
                }

                if (ReadyUrl.IsMatch(line))
                {
                    return process;
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
        string stateDir, params string[] args)
    {
        using var process = Process.Start(MakeStartInfo(stateDir, args))
            ?? throw new XunitException("Failed to start the charter CLI process.");

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

    private static ProcessStartInfo MakeStartInfo(string stateDir, params string[] args)
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

        startInfo.Environment["CHARTER_STATE_DIR"] = stateDir;
        return startInfo;
    }

    private static string CharterCliDllPath()
    {
        var metadata = typeof(ReviewLogPollTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "CharterCliPath");

        var path = metadata?.Value;
        Assert.False(string.IsNullOrEmpty(path), "The build did not set the CharterCliPath assembly metadata.");
        Assert.True(File.Exists(path), $"Built Charter.Cli.dll not found at '{path}'.");
        return path!;
    }

    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.WaitForExit(5000);
        }
        catch (Exception)
        {
            // Best-effort teardown.
        }

        process.Dispose();
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-review-poll-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
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
