using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Charter.Core;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// The per-author review-log WRITER (step 2 of <c>docs/plans/03-git-mediated-team-review.md</c>). Every
/// assertion here corresponds to a failure the merge spike reproduced, so these are regression tests for
/// known-real corruption, not hypotheticals: fused records from a missing trailing newline, a NUL that makes
/// git treat the log as binary, colliding ids from a counter, and two people sharing one file because their
/// addresses happen to slug alike.
/// </summary>
[Trait("Category", "ReviewLogWriter")]
public class ReviewLogWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "charter-review-log-" + Guid.NewGuid().ToString("N"));

    private static readonly ReviewAuthor Alice = new("Alice Ng", "alice@example.com");
    private static readonly ReviewAuthor Bob = new("Bob Chen", "bob@example.com");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory that outlives the test run is harmless.
        }

        GC.SuppressFinalize(this);
    }

    // ---- path derivation ---------------------------------------------------------------------------------

    [Fact]
    public void ReviewDirectory_IsThePlanNameMinusMd_PlusDotReview()
    {
        var plan = PlanPath("tenant-rate-limit.charter.md");

        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(plan)!, "tenant-rate-limit.charter.review"),
            ReviewLogPaths.DirectoryForPlan(plan));
    }

    [Fact]
    public void ReviewDirectory_ForAPlanWithoutAnMdExtension_KeepsTheWholeName()
    {
        var plan = PlanPath("plan-without-extension");

        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(plan)!, "plan-without-extension.review"),
            ReviewLogPaths.DirectoryForPlan(plan));
    }

    /// <summary>
    /// Both halves of the file name come from the LOWERCASED address. The fold compares identity
    /// case-insensitively, so if the file name did not agree, a capitalisation change in <c>git config</c>
    /// would silently give one person a second log — and their own comments would stop being retractable.
    /// </summary>
    [Fact]
    public void FileName_IsComputedFromTheLowercasedEmail_SoACapitalisationChangeCannotForkAnAuthorsLog()
    {
        Assert.Equal(
            ReviewLogPaths.FileNameForAuthor("alice@example.com"),
            ReviewLogPaths.FileNameForAuthor("Alice@Example.COM"));

        // ...and the hash really is of the lowercased form, not merely lowercased after the fact.
        Assert.Equal(
            ReviewLogPaths.Hash("alice@example.com"),
            ReviewLogPaths.Hash("ALICE@EXAMPLE.COM"));
        Assert.Equal("alice-example-com", ReviewLogPaths.Slug("Alice@Example.COM"));
    }

    /// <summary>
    /// The hash is identity; the slug is only legibility. With one file per author for the LIFE of the plan,
    /// two people whose addresses slug alike would otherwise share a file — interleaving their records and
    /// conflicting on every parallel review.
    /// </summary>
    [Fact]
    public void TwoEmailsThatSlugAlike_StillGetDistinctFiles()
    {
        const string First = "alice@ng.example.com";
        const string Second = "alice.ng@example.com";

        Assert.Equal(ReviewLogPaths.Slug(First), ReviewLogPaths.Slug(Second));
        Assert.NotEqual(ReviewLogPaths.Hash(First), ReviewLogPaths.Hash(Second));
        Assert.NotEqual(ReviewLogPaths.FileNameForAuthor(First), ReviewLogPaths.FileNameForAuthor(Second));
    }

    [Fact]
    public void FileName_IsSlugDotHashDotJsonl()
    {
        var name = ReviewLogPaths.FileNameForAuthor("alice@example.com");

        Assert.StartsWith("alice-example-com.", name, StringComparison.Ordinal);
        Assert.EndsWith(".jsonl", name, StringComparison.Ordinal);
        Assert.Equal(8, name["alice-example-com.".Length..^".jsonl".Length].Length);
    }

    [Fact]
    public void Slug_ForAnAddressWithNoAsciiAlphanumerics_FallsBackWithoutLosingIdentity()
    {
        Assert.Equal("author", ReviewLogPaths.Slug("你好@例.中国"));
        Assert.NotEqual(
            ReviewLogPaths.FileNameForAuthor("你好@例.中国"),
            ReviewLogPaths.FileNameForAuthor("こんにちは@例.日本"));
    }

    // ---- agent replies (#106) ----------------------------------------------------------------------------

    /// <summary>
    /// The agent's voice in a review thread. Before this the feedback channel was one-way: a reviewer
    /// commented, and the agent either revised the plan or did not — two outcomes that are indistinguishable
    /// from the browser, so "I disagree with this note" had no way to reach the human at all.
    /// </summary>
    [Fact]
    public void AppendReply_RecordsTheAgentAsTheActor_TargetingTheCommentItAnswers()
    {
        var writer = WriterFor("plan.charter.md", Alice);
        var comment = writer.AppendCreate(Anchor("b1"), "Use Postgres here.");

        var reply = writer.AppendReply(comment.Id, "Disagree — the read path is append-only.", ReviewActors.Agent);

        Assert.Equal(ReviewOpKind.Reply, reply.OpKind);
        Assert.Equal(comment.Id, reply.Target);
        Assert.Equal(ReviewActors.Agent, reply.Actor);
        Assert.Equal("Disagree — the read path is append-only.", reply.Body);

        // A reply ADDS to the thread; it does not settle the item. Only the four state ops carry `prev`
        // (§4.2), so a reply carrying one would claim to have observed — and therefore settled — a state it
        // never changed. Deciding a comment is done stays a separate, deliberate `resolve`.
        Assert.Null(reply.Prev);
        Assert.False(ReviewOps.IsStateOp(reply.OpKind));
    }

    /// <summary>
    /// The default is the HUMAN, matching every other <c>Append*</c>. `charter reply` opts into
    /// <see cref="ReviewActors.Agent"/> explicitly — a default of "agent" would silently attribute a
    /// reviewer's own words to a machine everywhere else in the writer.
    /// </summary>
    [Fact]
    public void AppendReply_DefaultsToTheHumanActor()
    {
        var writer = WriterFor("plan.charter.md", Alice);
        var comment = writer.AppendCreate(Anchor("b1"), "Why this datastore?");

        Assert.Equal(ReviewActors.Human, writer.AppendReply(comment.Id, "Because of the write volume.").Actor);
    }

    /// <summary>
    /// The point of the feature, end to end: a reply the AGENT wrote is a real line in the durable log that
    /// parses back with its actor intact. That is what the fold attaches to the comment's thread and what the
    /// SDK renders beside the reviewer's own note — no server involvement, and the plan file untouched.
    /// </summary>
    [Fact]
    public void AppendReply_RoundTripsThroughTheDurableLog_WithItsActorPreserved()
    {
        var writer = WriterFor("plan.charter.md", Alice);
        var comment = writer.AppendCreate(Anchor("b1"), "This retry loop looks unbounded.");
        writer.AppendReply(comment.Id, "It is bounded by maxAttempts; want me to name it in the diagram?", ReviewActors.Agent);

        var lines = ReadLines(writer.LogPath);
        Assert.Equal(2, lines.Count);

        var parsed = ReviewRecord.Parse(lines[1]);
        Assert.Equal(ReviewOpKind.Reply, parsed.OpKind);
        Assert.Equal(comment.Id, parsed.Target);
        Assert.Equal(ReviewActors.Agent, parsed.Actor);
        Assert.Contains("maxAttempts", parsed.Body, StringComparison.Ordinal);
    }

    /// <summary>An empty target has no thread to join — refuse it rather than write an orphan the fold reports.</summary>
    [Fact]
    public void AppendReply_RefusesAnEmptyTarget()
    {
        var writer = WriterFor("plan.charter.md", Alice);
        Assert.ThrowsAny<ArgumentException>(() => writer.AppendReply(string.Empty, "orphaned"));
    }

    // ---- append discipline -------------------------------------------------------------------------------

    [Fact]
    public void Append_CreatesTheReviewDirectoryOnDemand_AndWritesOneLinePerRecord()
    {
        var writer = WriterFor("plan.charter.md", Alice);
        Assert.False(Directory.Exists(writer.ReviewDirectory));

        writer.AppendCreate(Anchor("b1"), "Is Postgres right here?");
        writer.AppendCreate(Anchor("b2"), "And the latency budget?");

        Assert.True(Directory.Exists(writer.ReviewDirectory));
        var lines = ReadLines(writer.LogPath);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.NotNull(ReviewRecord.Parse(line)));
    }

    /// <summary>
    /// A record is NEVER pretty-printed: under a union merge, pretty JSON fuses records — silently, on a
    /// single-object <c>.json</c>. One object per line is what makes the fusion loud instead.
    /// </summary>
    [Fact]
    public void Append_NeverPrettyPrints_SoOneRecordIsExactlyOneLine()
    {
        var writer = WriterFor("plan.charter.md", Alice);

        writer.AppendCreate(Anchor("b1"), "a body\nwith an embedded newline\nand\ttabs");

        var text = File.ReadAllText(writer.LogPath);
        Assert.Equal(1, text.Count(c => c == '\n'));
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reproduced chain: a merge can leave the file WITHOUT a trailing newline, and the NEXT append then
    /// fuses two records. The rule is "read the last byte and supply one if missing" — not "always write a
    /// trailing newline", which a reviewer did and still corrupted the log.
    /// </summary>
    [Fact]
    public void Append_ToAFileAMergeLeftWithoutATrailingNewline_RepairsItInsteadOfFusingTwoRecords()
    {
        var writer = WriterFor("plan.charter.md", Alice);
        var first = writer.AppendCreate(Anchor("b1"), "the first comment");

        // Simulate the merge outcome: the trailing newline is gone.
        var mangled = File.ReadAllText(writer.LogPath).TrimEnd('\n');
        File.WriteAllText(writer.LogPath, mangled);
        Assert.DoesNotContain('\n', File.ReadAllText(writer.LogPath));

        var second = writer.AppendCreate(Anchor("b2"), "the second comment");

        var lines = ReadLines(writer.LogPath);
        Assert.Equal(2, lines.Count);
        Assert.Equal(first.Id, ReviewRecord.Parse(lines[0]).Id);
        Assert.Equal(second.Id, ReviewRecord.Parse(lines[1]).Id);
    }

    [Fact]
    public void Append_ToAFileThatAlreadyEndsInANewline_AddsNoBlankLine()
    {
        var writer = WriterFor("plan.charter.md", Alice);

        writer.AppendCreate(Anchor("b1"), "first");
        writer.AppendCreate(Anchor("b2"), "second");

        Assert.DoesNotContain("\n\n", File.ReadAllText(writer.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Append_ToAnEmptyOrAbsentLog_WritesNoLeadingBlankLine()
    {
        var absent = WriterFor("absent.charter.md", Alice);
        absent.AppendCreate(Anchor("b1"), "the very first comment on this plan");
        Assert.StartsWith("{", File.ReadAllText(absent.LogPath), StringComparison.Ordinal);

        var empty = WriterFor("empty.charter.md", Alice);
        Directory.CreateDirectory(empty.ReviewDirectory);
        File.WriteAllBytes(empty.LogPath, Array.Empty<byte>());
        empty.AppendCreate(Anchor("b1"), "the first comment into a zero-byte log");
        Assert.StartsWith("{", File.ReadAllText(empty.LogPath), StringComparison.Ordinal);
        Assert.Single(ReadLines(empty.LogPath));
    }

    /// <summary>
    /// A raw NUL makes git treat the file as BINARY, which bypasses the merge driver entirely. Control bytes
    /// must therefore be escaped, not merely tolerated — the log has to stay text for git.
    /// </summary>
    [Fact]
    public void Append_EscapesEveryControlCharacter_SoALiteralNulNeverReachesTheFile()
    {
        var writer = WriterFor("plan.charter.md", Alice);

        writer.AppendCreate(Anchor("b1"), "before\0after\r\nanda bell");

        var bytes = File.ReadAllBytes(writer.LogPath);
        Assert.DoesNotContain((byte)0x00, bytes);
        Assert.DoesNotContain((byte)0x0D, bytes);
        Assert.DoesNotContain((byte)0x07, bytes);
        Assert.Equal(1, bytes.Count(b => b == (byte)'\n'));   // only the record terminator

        // ...and the text survives the round trip intact, escaped rather than stripped.
        var record = ReviewRecord.Parse(Assert.Single(ReadLines(writer.LogPath)));
        Assert.Equal("before\0after\r\nanda bell", record.Body);
    }

    /// <summary>
    /// Ids must be globally-unique and RANDOM: a counter lets two teammates working offline mint the same id,
    /// and the fold dedupes by id — so a counter silently turns two different comments into one.
    /// </summary>
    [Fact]
    public void Append_MintsAGloballyUniqueIdPerRecord()
    {
        var writer = WriterFor("plan.charter.md", Alice);

        var ids = Enumerable.Range(0, 400)
            .Select(i => writer.AppendCreate(Anchor("b" + i), "note " + i).Id)
            .ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.StartsWith("cmt_", id, StringComparison.Ordinal));

        // The ids that landed in the FILE are the same ones the writer reported.
        var written = ReadLines(writer.LogPath).Select(l => ReviewRecord.Parse(l).Id).ToList();
        Assert.Equal(ids, written);
    }

    [Fact]
    public void Append_PerOp_UsesADistinctIdPrefixSoAPrDiffIsLegible()
    {
        var writer = WriterFor("plan.charter.md", Alice);

        var create = writer.AppendCreate(Anchor("b1"), "the comment");
        var edit = writer.AppendEdit(create.Id, "the edited comment", prev: null);
        var resolve = writer.AppendResolve(create.Id, prev: edit.Id);
        var retract = writer.AppendRetract(create.Id, prev: resolve.Id);

        Assert.StartsWith("cmt_", create.Id, StringComparison.Ordinal);
        Assert.StartsWith("edt_", edit.Id, StringComparison.Ordinal);
        Assert.StartsWith("rsv_", resolve.Id, StringComparison.Ordinal);
        Assert.StartsWith("rtr_", retract.Id, StringComparison.Ordinal);
    }

    // ---- round trip through the fold ---------------------------------------------------------------------

    /// <summary>
    /// The writer's whole purpose: what it appends must fold back into the state the reviewer expressed —
    /// including <c>prev</c>, so a second edit by one author is an OBSERVED edit rather than a concurrent one.
    /// </summary>
    [Fact]
    public void WrittenRecords_FoldBackIntoTheStateTheReviewerExpressed()
    {
        var plan = PlanPath("plan.charter.md");
        var alice = new ReviewLogWriter(plan, Alice);
        var bob = new ReviewLogWriter(plan, Bob);

        var comment = alice.AppendCreate(Anchor("b1"), "Is Postgres right here?");
        var edit = alice.AppendEdit(comment.Id, "Is Postgres right here, given the latency budget?", prev: null);
        var second = alice.AppendEdit(comment.Id, "Is Postgres right here, really?", prev: edit.Id);
        bob.AppendResolve(comment.Id, prev: second.Id);
        var bobs = bob.AppendCreate(Anchor("b2"), "The write path needs a retry budget.");

        var read = ReviewLogStore.ReadForPlan(plan);
        Assert.Empty(read.Unreadable);
        Assert.Empty(read.State.Diagnostics);
        Assert.Equal(2, read.State.Comments.Count);

        var folded = read.State.Comments.Single(c => c.Id == comment.Id);
        Assert.Equal("Is Postgres right here, really?", folded.Body);
        Assert.Equal(ReviewCommentStatus.Resolved, folded.Status);
        Assert.Equal(Bob.Email, Assert.Single(folded.ResolutionRecords).Author.Email);

        var other = read.State.Comments.Single(c => c.Id == bobs.Id);
        Assert.Equal(Bob.Email, other.Author.Email);
        Assert.Equal(ReviewCommentStatus.Open, other.Status);
    }

    [Fact]
    public void TwoAuthors_WriteTwoDistinctFilesBesideThePlan()
    {
        var plan = PlanPath("plan.charter.md");
        var alice = new ReviewLogWriter(plan, Alice);
        var bob = new ReviewLogWriter(plan, Bob);

        alice.AppendCreate(Anchor("b1"), "from alice");
        bob.AppendCreate(Anchor("b1"), "from bob");

        Assert.NotEqual(alice.LogPath, bob.LogPath);
        Assert.Equal(2, ReviewLogPaths.EnumerateLogs(ReviewLogPaths.DirectoryForPlan(plan)).Count);
    }

    /// <summary>
    /// <c>StateHeads</c> is what lets a writer set <c>prev</c> honestly. Without it every state record would
    /// have to claim it observed nothing, and a second edit by one author would read as a CONCURRENT edit —
    /// reverting the body to the last agreed one and reporting a disagreement that never happened.
    /// </summary>
    [Fact]
    public void PrevFromStateHeads_KeepsASecondEditFromReadingAsConcurrent()
    {
        var plan = PlanPath("plan.charter.md");
        var alice = new ReviewLogWriter(plan, Alice);
        var comment = alice.AppendCreate(Anchor("b1"), "first body");

        alice.AppendEdit(comment.Id, "second body", prev: null);
        var head = Assert.Single(ReviewLogStore.ReadForPlan(plan).State.Comments).StateHeads;
        alice.AppendEdit(comment.Id, "third body", prev: Assert.Single(head).Id);

        var folded = Assert.Single(ReviewLogStore.ReadForPlan(plan).State.Comments);
        Assert.Equal("third body", folded.Body);
        Assert.DoesNotContain(folded.Id, string.Join(
            " ", ReviewLogStore.ReadForPlan(plan).State.Diagnostics.Select(d => d.Message)));
    }

    // ---- concurrency -------------------------------------------------------------------------------------

    /// <summary>
    /// Two Charter processes appending to ONE author's log (the same human running two review sessions) must
    /// lose nothing and tear no line. The writer holds the log with <c>FileShare.None</c> for the whole
    /// read-last-byte → append → flush sequence, so the interleaving that would fuse two half-records cannot
    /// happen; a contended append retries rather than failing.
    /// </summary>
    [Fact]
    public async Task ConcurrentWriters_OnOneAuthorsLog_LoseNothingAndTearNoLine()
    {
        const int PerWriter = 60;
        var plan = PlanPath("plan.charter.md");

        // Two SEPARATE writer instances for the same author + plan, exactly as two processes would be: they
        // share no in-process lock, so only the file lock stands between them.
        var writers = new[] { new ReviewLogWriter(plan, Alice), new ReviewLogWriter(plan, Alice) };
        Assert.Equal(writers[0].LogPath, writers[1].LogPath);

        var minted = new ConcurrentBag<string>();
        await Task.WhenAll(writers.Select((writer, w) => Task.Run(() =>
        {
            for (var i = 0; i < PerWriter; i++)
            {
                minted.Add(writer.AppendCreate(Anchor($"b{w}-{i}"), $"writer {w} note {i}").Id);
            }
        })));

        var lines = ReadLines(writers[0].LogPath);
        Assert.Equal(2 * PerWriter, lines.Count);

        // Every line is a WHOLE record (a torn line would not parse), and every minted id is present exactly
        // once — nothing was lost and nothing was written twice.
        var ids = lines.Select(l => ReviewRecord.Parse(l).Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(minted.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            ids.OrderBy(id => id, StringComparer.Ordinal).ToList());

        // ...and the fold agrees: no malformed line, no duplicate.
        var read = ReviewLogStore.ReadForPlan(plan);
        Assert.Empty(read.State.Diagnostics);
        Assert.Equal(2 * PerWriter, read.State.Comments.Count);
    }

    // ---- identity ----------------------------------------------------------------------------------------

    /// <summary>
    /// A machine with no git identity must still be able to review — local-only single-reviewer use is a
    /// supported mode (§7's opt-out is exactly that). The fallback is CLEARLY marked so the caller can warn.
    /// </summary>
    [Fact]
    public void LocalIdentity_IsUsableAndClearlyMarked()
    {
        var identity = GitIdentity.Local("git did not report a user.email");

        Assert.False(identity.FromGit);
        Assert.NotNull(identity.Reason);
        Assert.EndsWith("@" + GitIdentity.LocalDomain, identity.Author.Email, StringComparison.Ordinal);
        Assert.Contains("local", identity.Author.Name, StringComparison.OrdinalIgnoreCase);

        // It is a usable identity: a writer built on it writes a well-formed, foldable log.
        var plan = PlanPath("plan.charter.md");
        var writer = new ReviewLogWriter(plan, identity.Author);
        writer.AppendCreate(Anchor("b1"), "a note written without a git identity");
        Assert.Single(ReviewLogStore.ReadForPlan(plan).State.Comments);
    }

    /// <summary>
    /// Resolving an identity is a READ of git state (§5.1 permits reads and forbids mutation) and must never
    /// throw, whatever the directory is.
    /// </summary>
    [Fact]
    public void ResolveIdentity_NeverThrows_AndAlwaysYieldsAUsableAuthor()
    {
        foreach (var directory in new[] { _root, Path.Combine(_root, "does-not-exist"), string.Empty })
        {
            var identity = GitIdentity.Resolve(directory);
            Assert.NotNull(identity);
            Assert.False(string.IsNullOrEmpty(identity.Author.Email));
            Assert.Equal(identity.FromGit, identity.Reason is null);
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private string PlanPath(string fileName)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "# Plan\n\nA paragraph.\n");
        }

        return path;
    }

    private ReviewLogWriter WriterFor(string planFileName, ReviewAuthor author)
        => new(PlanPath(planFileName), author);

    private static ReviewAnchor Anchor(string blockId)
        => new(blockId, "element", "the read path will be built after", "sha256:1f4c");

    /// <summary>The log's non-empty lines, read as raw bytes so a stray CR or NUL cannot hide behind a decoder.</summary>
    private static IReadOnlyList<string> ReadLines(string path)
        => Encoding.UTF8.GetString(File.ReadAllBytes(path))
            .Split('\n')
            .Where(line => line.Length > 0)
            .ToList();
}
