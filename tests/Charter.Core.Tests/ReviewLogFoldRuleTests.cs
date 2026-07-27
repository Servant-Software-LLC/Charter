using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// The eight fold rules of <c>docs/plans/03-git-mediated-team-review.md</c> §3, one test each. Every rule is
/// there because the merge spike reproduced the failure it prevents, so each test names that failure rather
/// than merely asserting the current behaviour.
/// </summary>
[Trait("Category", "ReviewLog")]
public class ReviewLogFoldRuleTests
{
    [Fact]
    public void Rule1_IdenticalRecordIsDedupedToOne()
    {
        // The spike: duplication is REACHABLE when an identical record's position differs relative to each
        // side's other records. A duplicated create must not become two comments.
        var create = Rec.Create("c1", Rec.Alice);

        var state = Rec.Fold(create, create, create);

        Assert.Equal("c1", state.OnlyComment().Id);
        Assert.Equal(2, state.OfKind(ReviewDiagnosticKind.DuplicateRecord).Count);
    }

    [Fact]
    public void Rule1_DuplicateResolveIsAppliedOnceNotTwice()
    {
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Resolve("r1", "c1", Rec.Bob),
            Rec.Resolve("r1", "c1", Rec.Bob));

        var comment = state.OnlyComment();
        Assert.Equal(ReviewCommentStatus.Resolved, comment.Status);
        Assert.Equal("r1", Assert.Single(comment.ResolutionRecords).Id);
    }

    [Fact]
    public void Rule1And2_ConflictingDuplicateIsReportedAndSettledWithoutUsingInputOrder()
    {
        // Two DIFFERENT records sharing an id: "first wins" would make the result depend on merge order,
        // which rule 2 forbids. The fold must pick the same one either way — and say so.
        var alice = Rec.Create("c1", Rec.Alice, body: "Alice's text");
        var bob = Rec.Create("c1", Rec.Bob, body: "Bob's text");

        var forward = Rec.Fold(alice, bob);
        var backward = Rec.Fold(bob, alice);

        Assert.Equal(forward.OnlyComment().Body, backward.OnlyComment().Body);
        Assert.Single(forward.OfKind(ReviewDiagnosticKind.ConflictingDuplicate));
        Assert.Single(backward.OfKind(ReviewDiagnosticKind.ConflictingDuplicate));
    }

    [Fact]
    public void Rule2_SameRecordsInDifferentFileOrderFoldIdentically()
    {
        // The spike: the same branches merged in two orders produce different byte order, so two teammates
        // with identical commits legitimately hold different files.
        var aliceLog = Rec.Source("alice.jsonl", Rec.Create("c1", Rec.Alice), Rec.Reply("p1", "c1", Rec.Alice));
        var bobLog = Rec.Source("bob.jsonl", Rec.Reply("p2", "c1", Rec.Bob), Rec.Resolve("r1", "c1", Rec.Bob));

        var forward = ReviewLog.Fold([aliceLog, bobLog]);
        var backward = ReviewLog.Fold([bobLog, aliceLog]);

        Assert.Equal(Rec.Canonical(forward), Rec.Canonical(backward));
        Assert.Equal(ReviewCommentStatus.Resolved, forward.OnlyComment().Status);
        Assert.Equal(new[] { "p1", "p2" }, forward.OnlyComment().Replies.Select(r => r.Id));
    }

    [Fact]
    public void Rule3_ATimestampNeverDecidesCausality()
    {
        // The design's own failure case: Alice is offline with a 20-minute-slow clock. Her reopen OBSERVED
        // Bob's resolve, so it settles — even though it is stamped 10 minutes EARLIER.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice, ts: "2026-07-26T13:00:00Z"),
            Rec.Resolve("r1", "c1", Rec.Bob, ts: "2026-07-26T13:50:00Z"),
            Rec.Reopen("o1", "c1", Rec.Alice, prev: "r1", ts: "2026-07-26T13:40:00Z"));

        var comment = state.OnlyComment();
        Assert.Equal(ReviewCommentStatus.Open, comment.Status);
        Assert.Equal("o1", Assert.Single(comment.ResolutionRecords).Id);
    }

    [Fact]
    public void Rule4_OrphanTargetIsRetainedAndReported()
    {
        // A reply and a resolve whose comment lives on a branch that has not been merged yet. Not corruption
        // — and never dropped.
        var state = Rec.Fold(
            Rec.Reply("p1", "c_missing", Rec.Bob),
            Rec.Resolve("r1", "c_missing", Rec.Bob));

        Assert.Empty(state.Comments);
        var orphans = state.OfKind(ReviewDiagnosticKind.OrphanTarget);
        Assert.Equal(2, orphans.Count);
        Assert.All(orphans, d => Assert.Equal("c_missing", d.TargetId));
        Assert.All(orphans, d => Assert.NotNull(d.Record));
        Assert.Contains(orphans, d => d.Record!.Body == "Good point.");
    }

    [Fact]
    public void Rule4_OrphanReattachesWhenTheTargetArrivesInAnotherSource()
    {
        // The same records, plus the branch that was missing: the two-pass fold indexes every source before
        // applying anything, so the reply and the resolve simply find their comment.
        var state = ReviewLog.Fold(
        [
            Rec.Source("bob.jsonl", Rec.Reply("p1", "c1", Rec.Bob), Rec.Resolve("r1", "c1", Rec.Bob)),
            Rec.Source("alice.jsonl", Rec.Create("c1", Rec.Alice)),
        ]);

        var comment = state.OnlyComment();
        Assert.Equal("p1", Assert.Single(comment.Replies).Id);
        Assert.Equal(ReviewCommentStatus.Resolved, comment.Status);
        Assert.Empty(state.OfKind(ReviewDiagnosticKind.OrphanTarget));
    }

    [Fact]
    public void Rule5_MalformedLineIsReportedWithLineNumberAndRawTextAndFoldingContinues()
    {
        // A conflict marker, a fused record, or a hand-edit. The rest of the review must survive it.
        var state = ReviewLog.Fold(
        [
            Rec.Source(
                "alice.jsonl",
                Rec.Create("c1", Rec.Alice),
                "<<<<<<< HEAD",
                Rec.Reply("p1", "c1", Rec.Bob)),
        ]);

        Assert.Equal("c1", state.OnlyComment().Id);
        Assert.Single(state.OnlyComment().Replies);

        var malformed = Assert.Single(state.OfKind(ReviewDiagnosticKind.MalformedLine));
        Assert.Equal("alice.jsonl", malformed.FileName);
        Assert.Equal(2, malformed.LineNumber);
        Assert.Equal("<<<<<<< HEAD", malformed.RawLine);
    }

    [Fact]
    public void Rule5_MalformedLineFromFromTextKeepsItsOriginalLineNumber()
    {
        var lines = new[] { Rec.Create("c1", Rec.Alice), "", "{ oops", Rec.Reply("p1", "c1", Rec.Bob) };
        var text = string.Join("\r\n", lines) + "\r\n";

        var state = ReviewLog.Fold([ReviewLogSource.FromText("alice.jsonl", text)]);

        Assert.Single(state.OnlyComment().Replies);
        Assert.Equal(3, Assert.Single(state.OfKind(ReviewDiagnosticKind.MalformedLine)).LineNumber);
    }

    [Fact]
    public void Rule6_UnknownOpIsRetainedIgnoredForStateAndCounted()
    {
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Record("endorse", "e1", Rec.Bob, target: "c1"));

        Assert.Equal(ReviewCommentStatus.Open, state.OnlyComment().Status);
        var unknown = Assert.Single(state.OfKind(ReviewDiagnosticKind.UnknownOp));
        Assert.Equal("e1", unknown.RecordId);
        Assert.Equal("endorse", unknown.Record!.Op);
    }

    [Fact]
    public void Rule7_KnownOpAtAnUnknownVersionIsRetainedReportedAndNotApplied()
    {
        // Without this a v1 fold applies a v2 resolve under v1 semantics, and two teammates silently hold
        // different state. The comment must stay OPEN and the record must still be visible.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Resolve("r1", "c1", Rec.Bob, v: 2));

        var comment = state.OnlyComment();
        Assert.Equal(ReviewCommentStatus.Open, comment.Status);
        Assert.Empty(comment.ResolutionRecords);

        var retained = Assert.Single(state.OfKind(ReviewDiagnosticKind.UnknownVersion));
        Assert.Equal("r1", retained.RecordId);
        Assert.Equal(2, retained.Record!.Version);
    }

    [Fact]
    public void Rule7_ACreateAtAnUnknownVersionYieldsNoCommentAndOrphansItsThread()
    {
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice, v: 2),
            Rec.Reply("p1", "c1", Rec.Bob));

        Assert.Empty(state.Comments);
        Assert.Single(state.OfKind(ReviewDiagnosticKind.UnknownVersion));
        Assert.Equal("c1", Assert.Single(state.OfKind(ReviewDiagnosticKind.OrphanTarget)).TargetId);
    }

    [Fact]
    public void Rule8_VersionIsPerRecordNotPerFile()
    {
        // One file legitimately mixes versions: a v2 record must not condemn the v1 records around it.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Resolve("r_future", "c1", Rec.Bob, v: 7),
            Rec.Reply("p1", "c1", Rec.Carol),
            Rec.Resolve("r1", "c1", Rec.Carol));

        var comment = state.OnlyComment();
        Assert.Equal(ReviewCommentStatus.Resolved, comment.Status);
        Assert.Equal("r1", Assert.Single(comment.ResolutionRecords).Id);
        Assert.Single(comment.Replies);
        Assert.Single(state.OfKind(ReviewDiagnosticKind.UnknownVersion));
    }

    [Fact]
    public void BlankLinesAndAnEmptyLogAreNotErrors()
    {
        var state = ReviewLog.Fold([Rec.Source("alice.jsonl", "", "   ", Rec.Create("c1", Rec.Alice), "")]);

        Assert.Equal("c1", state.OnlyComment().Id);
        Assert.Empty(state.Diagnostics);
        Assert.Empty(ReviewLog.Fold([]).Comments);
    }

    [Fact]
    public void AgentAuthoredRecordsKeepTheirActor()
    {
        // §4: the owner's decision that the agent gets a voice, rather than answering by silently editing.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Reply("p1", "c1", Rec.Bob, actor: ReviewActors.Agent));

        Assert.Equal(ReviewActors.Human, state.OnlyComment().Actor);
        Assert.Equal(ReviewActors.Agent, Assert.Single(state.OnlyComment().Replies).Actor);
    }

    [Fact]
    public void CommentsAndRepliesAreOrderedForDisplayByTimestampThenId()
    {
        var state = Rec.Fold(
            Rec.Create("c_second", Rec.Alice, ts: "2026-07-26T12:00:00Z"),
            Rec.Create("c_first", Rec.Alice, ts: "2026-07-26T09:00:00Z"),
            Rec.Reply("p_late", "c_first", Rec.Bob, ts: "2026-07-26T15:00:00Z"),
            Rec.Reply("p_early", "c_first", Rec.Bob, ts: "2026-07-26T10:00:00Z"));

        Assert.Equal(new[] { "c_first", "c_second" }, state.Comments.Select(c => c.Id));
        Assert.Equal(new[] { "p_early", "p_late" }, state.Comments[0].Replies.Select(r => r.Id));
    }
}
