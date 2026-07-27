using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Fold rule 2 as a property, not an anecdote: <c>fold(records) == fold(shuffle(records))</c>.
/// <para>
/// This is load-bearing rather than theoretical. The merge spike
/// (<c>docs/plans/03-git-mediated-team-review.md</c> §3) proved that the same branches merged in two orders
/// produce different byte order, and that union merge interleaves lines within a file — so two teammates
/// with identical commits legitimately hold different files and MUST still see the same review.
/// </para>
/// <para>
/// A record's file name and line number legitimately move with it, so the projection compared here is the
/// STATE plus every diagnostic's kind, subject and message — everything except where the line happened to
/// land. <see cref="DiagnosticLocationsAreStableForAFixedInput"/> covers the locations separately.
/// </para>
/// </summary>
[Trait("Category", "ReviewLog")]
public class ReviewLogOrderIndependenceTests
{
    private const int Permutations = 200;

    [Fact]
    public void FoldIsInvariantUnderShufflingAndRedistributingAcrossFiles()
    {
        var corpus = Corpus();
        var baseline = Rec.Canonical(ReviewLog.Fold(Distribute(corpus, fileCount: 3)));
        var random = new Random(20260727);

        for (var permutation = 0; permutation < Permutations; permutation++)
        {
            var shuffled = Shuffle(corpus, random);
            var files = Distribute(shuffled, fileCount: 1 + (permutation % 4));

            Assert.Equal(baseline, Rec.Canonical(ReviewLog.Fold(files)));
        }
    }

    [Fact]
    public void FoldIsInvariantUnderReversal()
    {
        // The cheapest permutation to reason about, kept explicit so a failure is readable.
        var corpus = Corpus();
        var forward = ReviewLog.Fold(Distribute(corpus, fileCount: 2));

        var reversed = new List<string>(corpus);
        reversed.Reverse();

        Assert.Equal(Rec.Canonical(forward), Rec.Canonical(ReviewLog.Fold(Distribute(reversed, fileCount: 2))));
    }

    [Fact]
    public void TheCorpusActuallyExercisesEveryDiagnosticAndStatus()
    {
        // A property test over a corpus that happened to be trivial would pass blind. Pin what it covers.
        var state = ReviewLog.Fold(Distribute(Corpus(), fileCount: 3));

        Assert.Equal(
            new[]
            {
                ReviewCommentStatus.Open,
                ReviewCommentStatus.Resolved,
                ReviewCommentStatus.Contested,
                ReviewCommentStatus.Retracted,
            }.Order().ToArray(),
            state.Comments.Select(c => c.Status).Distinct().Order().ToArray());

        var kinds = state.Diagnostics.Select(d => d.Kind).Distinct().ToList();
        Assert.Contains(ReviewDiagnosticKind.MalformedLine, kinds);
        Assert.Contains(ReviewDiagnosticKind.DuplicateRecord, kinds);
        Assert.Contains(ReviewDiagnosticKind.ConflictingDuplicate, kinds);
        Assert.Contains(ReviewDiagnosticKind.UnknownOp, kinds);
        Assert.Contains(ReviewDiagnosticKind.UnknownVersion, kinds);
        Assert.Contains(ReviewDiagnosticKind.OrphanTarget, kinds);
        Assert.Contains(ReviewDiagnosticKind.RetractNotByAuthor, kinds);
        Assert.Contains(ReviewDiagnosticKind.ConcurrentEdit, kinds);
    }

    [Fact]
    public void DiagnosticLocationsAreStableForAFixedInput()
    {
        var files = Distribute(Corpus(), fileCount: 3);

        Assert.Equal(
            Rec.Canonical(ReviewLog.Fold(files), includeLocations: true),
            Rec.Canonical(ReviewLog.Fold(files), includeLocations: true));
    }

    [Fact]
    public void EveryDiagnosticIsCountedOnceNoMatterHowTheRecordsWereSplit()
    {
        var corpus = Corpus();
        var single = ReviewLog.Fold(Distribute(corpus, fileCount: 1)).Diagnostics;
        var spread = ReviewLog.Fold(Distribute(corpus, fileCount: 5)).Diagnostics;

        Assert.Equal(single.Count, spread.Count);
    }

    /// <summary>
    /// A review that exercises every fold rule at once: threads, an observed chain, a contested comment, a
    /// valid and an invalid retract, concurrent edits, an orphan, a duplicate, a conflicting duplicate, an
    /// unknown op, a future version and a malformed line.
    /// </summary>
    private static List<string> Corpus()
    {
        var duplicated = Rec.Reply("p_dup", "c_open", Rec.Carol, body: "duplicated by a merge");

        return
        [
            // An open comment with a thread, one reply of a reply, and an agent's voice.
            Rec.Create("c_open", Rec.Alice, body: "Is Postgres right here?", blockId: "b1"),
            Rec.Reply("p1", "c_open", Rec.Bob, body: "The latency budget is 50ms."),
            Rec.Reply("p2", "p1", Rec.Alice, body: "Then it is not.", actor: ReviewActors.Agent),
            duplicated,
            duplicated,

            // Resolved through an observed chain, with an edit on top of the resolve.
            Rec.Create("c_resolved", Rec.Bob, body: "Rename the module?", blockId: "b2"),
            Rec.Resolve("s1", "c_resolved", Rec.Alice),
            Rec.Reopen("s2", "c_resolved", Rec.Carol, prev: "s1"),
            Rec.Resolve("s3", "c_resolved", Rec.Bob, prev: "s2"),
            Rec.Edit("e_resolved", "c_resolved", Rec.Bob, body: "Rename the module before the cut?", prev: "s3"),

            // Contested: a resolve and a reopen that never observed each other.
            Rec.Create("c_contested", Rec.Carol, body: "This misses the write path.", blockId: "b3"),
            Rec.Resolve("r_bob", "c_contested", Rec.Bob, ts: "2026-07-26T13:50:00Z"),
            Rec.Reopen("o_alice", "c_contested", Rec.Alice, ts: "2026-07-26T13:40:00Z"),

            // Retracted by its author, with a reply that survives, plus a retract by the wrong person.
            Rec.Create("c_retracted", Rec.Alice, body: "Never mind.", blockId: "b4"),
            Rec.Reply("p_kept", "c_retracted", Rec.Bob, body: "It was a fair question."),
            Rec.Retract("x_valid", "c_retracted", Rec.Alice),
            Rec.Retract("x_invalid", "c_open", Rec.Bob),

            // Concurrent edits to the open comment: neither observed the other.
            Rec.Edit("e_a", "c_open", Rec.Alice, body: "Is Postgres right here, given the budget?"),
            Rec.Edit("e_b", "c_open", Rec.Alice, body: "Is Postgres right here, given the cost?"),

            // Retained but never applied: an orphan, an unknown op, a future version, a malformed line, and
            // two different records that claim one id.
            Rec.Reply("p_orphan", "c_unmerged", Rec.Bob, body: "from a branch nobody merged"),
            Rec.Record("endorse", "u1", Rec.Carol, target: "c_open"),
            Rec.Resolve("v_future", "c_open", Rec.Bob, v: 9),
            "{ this line is not a record",
            Rec.Create("c_conflict", Rec.Alice, body: "one version of the text", blockId: "b5"),
            Rec.Create("c_conflict", Rec.Bob, body: "another version of the text", blockId: "b5"),
        ];
    }

    /// <summary>A Fisher-Yates shuffle with a seeded generator, so a failure is reproducible.</summary>
    private static List<string> Shuffle(List<string> lines, Random random)
    {
        var shuffled = new List<string>(lines);
        for (var i = shuffled.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        return shuffled;
    }

    /// <summary>Deal the lines round-robin into <paramref name="fileCount"/> per-author logs.</summary>
    private static List<ReviewLogSource> Distribute(List<string> lines, int fileCount)
    {
        var files = Enumerable.Range(0, fileCount).Select(_ => new List<string>()).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            files[i % fileCount].Add(lines[i]);
        }

        return files.Select((file, index) => new ReviewLogSource($"author{index}.jsonl", file)).ToList();
    }
}
