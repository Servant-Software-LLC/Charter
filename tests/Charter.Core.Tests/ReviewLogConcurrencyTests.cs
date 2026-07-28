using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// State settlement (<c>docs/plans/03-git-mediated-team-review.md</c> §4.2): concurrency is DETECTED, never
/// silently ordered. Last-writer-wins by timestamp is rejected — it imposes a total order on genuinely
/// concurrent events, never reports that it did, and (the design's worked example) discards an offline
/// reviewer's explicit reopen with no trace. The only causality signal is <c>prev</c>: the record its author
/// had observed.
/// </summary>
[Trait("Category", "ReviewLog")]
public class ReviewLogConcurrencyTests
{
    [Fact]
    public void AnObservedChainSettles()
    {
        // Bob resolves; Alice sees his resolve and reopens. She observed him, so her reopen settles.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Resolve("r1", "c1", Rec.Bob),
            Rec.Reopen("o1", "c1", Rec.Alice, prev: "r1"));

        var comment = state.OnlyComment();
        Assert.Equal(ReviewResolution.Open, comment.Resolution);
        Assert.Equal("o1", Assert.Single(comment.ResolutionRecords).Id);
    }

    [Fact]
    public void ALongObservedChainSettlesToItsHead()
    {
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Resolve("s1", "c1", Rec.Bob),
            Rec.Reopen("s2", "c1", Rec.Alice, prev: "s1"),
            Rec.Resolve("s3", "c1", Rec.Carol, prev: "s2"),
            Rec.Reopen("s4", "c1", Rec.Bob, prev: "s3"),
            Rec.Resolve("s5", "c1", Rec.Alice, prev: "s4"));

        var comment = state.OnlyComment();
        Assert.Equal(ReviewCommentStatus.Resolved, comment.Status);
        Assert.Equal("s5", Assert.Single(comment.ResolutionRecords).Id);
        Assert.Equal(Rec.Alice, Assert.Single(comment.ResolutionRecords).Author.Email);
    }

    [Fact]
    public void TwoConcurrentResolvesConverge()
    {
        // Neither observed the other, but they AGREE. Agreement is not a conflict: the comment is resolved,
        // and both claimants are attributed.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Resolve("r_bob", "c1", Rec.Bob),
            Rec.Resolve("r_carol", "c1", Rec.Carol));

        var comment = state.OnlyComment();
        Assert.Equal(ReviewCommentStatus.Resolved, comment.Status);
        Assert.Equal(ReviewResolution.Resolved, comment.Resolution);
        Assert.Equal(new[] { "r_bob", "r_carol" }, comment.ResolutionRecords.Select(r => r.Id));
    }

    [Fact]
    public void AConcurrentResolveAndReopenAreContestedAndBothSidesAreReported()
    {
        // The case the whole design exists for: Alice reopens offline while Bob resolves. Neither observed
        // the other, so the fold refuses to pick — and a contested comment is NOT resolved.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Resolve("r_bob", "c1", Rec.Bob, ts: "2026-07-26T13:50:00Z"),
            Rec.Reopen("o_alice", "c1", Rec.Alice, ts: "2026-07-26T13:40:00Z"));

        var comment = state.OnlyComment();
        Assert.Equal(ReviewCommentStatus.Contested, comment.Status);
        Assert.Equal(ReviewResolution.Contested, comment.Resolution);
        Assert.NotEqual(ReviewCommentStatus.Resolved, comment.Status);

        Assert.Equal(new[] { "o_alice", "r_bob" }, comment.ResolutionRecords.Select(r => r.Id));
        Assert.Contains(comment.ResolutionRecords, r => r.OpKind == ReviewOpKind.Reopen && r.Author.Email == Rec.Alice);
        Assert.Contains(comment.ResolutionRecords, r => r.OpKind == ReviewOpKind.Resolve && r.Author.Email == Rec.Bob);
    }

    [Fact]
    public void ContestedIsNotBrokenByReorderingOrByTimestamps()
    {
        // Swapping the records, and swapping which side has the later stamp, must not turn a contested
        // comment into a resolved one — that is exactly the silent LWW behaviour the design rejects.
        var later = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Reopen("o_alice", "c1", Rec.Alice, ts: "2026-07-26T23:00:00Z"),
            Rec.Resolve("r_bob", "c1", Rec.Bob, ts: "2026-07-26T08:00:00Z"));

        var reordered = Rec.Fold(
            Rec.Resolve("r_bob", "c1", Rec.Bob, ts: "2026-07-26T08:00:00Z"),
            Rec.Reopen("o_alice", "c1", Rec.Alice, ts: "2026-07-26T23:00:00Z"),
            Rec.Create("c1", Rec.Alice));

        Assert.Equal(ReviewCommentStatus.Contested, later.OnlyComment().Status);
        Assert.Equal(Rec.Canonical(later), Rec.Canonical(reordered));
    }

    [Fact]
    public void APrevPointingAtARecordThatHasNotArrivedIsTreatedAsUnobserved()
    {
        // Alice's reopen observed a resolve that lives on an unmerged branch. It cannot be proven to have
        // observed Bob's resolve, so the two are concurrent — contested, not silently settled.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Resolve("r_bob", "c1", Rec.Bob),
            Rec.Reopen("o_alice", "c1", Rec.Alice, prev: "r_unmerged"));

        Assert.Equal(ReviewCommentStatus.Contested, state.OnlyComment().Status);
    }

    [Fact]
    public void AResolveOnABranchThatObservedTheReopenSettlesItAgain()
    {
        // The contested state is recoverable exactly as the design intends: someone observes both and acts.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Resolve("r_bob", "c1", Rec.Bob),
            Rec.Reopen("o_alice", "c1", Rec.Alice),
            Rec.Resolve("r_final", "c1", Rec.Carol, prev: "o_alice"));

        var comment = state.OnlyComment();

        // r_bob is still unobserved by anyone, so it remains a live claim — and it AGREES with r_final, so
        // the comment settles resolved rather than staying contested.
        Assert.Equal(ReviewCommentStatus.Resolved, comment.Status);
        Assert.Equal(new[] { "r_bob", "r_final" }, comment.ResolutionRecords.Select(r => r.Id));
    }

    [Fact]
    public void AnEditOnAnObservedChainReplacesTheBody()
    {
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice, body: "Is Postgres right here?"),
            Rec.Edit("e1", "c1", Rec.Alice, body: "Is Postgres right here, given the latency budget?"),
            Rec.Edit("e2", "c1", Rec.Alice, body: "Is Postgres right here, given the 50ms budget?", prev: "e1"));

        Assert.Equal("Is Postgres right here, given the 50ms budget?", state.OnlyComment().Body);
    }

    [Fact]
    public void AnEditIsNotContestedByAnUnrelatedResolveThatNeverSawIt()
    {
        // Bob's resolve says nothing about the text, so it must not cost Alice her edit.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice, body: "original"),
            Rec.Edit("e1", "c1", Rec.Alice, body: "edited"),
            Rec.Resolve("r1", "c1", Rec.Bob));

        Assert.Equal("edited", state.OnlyComment().Body);
        Assert.Equal(ReviewCommentStatus.Resolved, state.OnlyComment().Status);
        Assert.Empty(state.OfKind(ReviewDiagnosticKind.ConcurrentEdit));
    }

    [Fact]
    public void ConcurrentEditsKeepTheLastAgreedBodyAndAreReported()
    {
        // Two edits neither of which observed the other. Picking one by timestamp would silently discard the
        // other, so the fold keeps the last body both branches agreed on and says what happened.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice, body: "original"),
            Rec.Edit("e_agreed", "c1", Rec.Alice, body: "agreed"),
            Rec.Edit("e_alice", "c1", Rec.Alice, body: "Alice's later text", prev: "e_agreed"),
            Rec.Edit("e_bob", "c1", Rec.Alice, body: "Bob's later text", prev: "e_agreed"));

        Assert.Equal("agreed", state.OnlyComment().Body);
        var reported = Assert.Single(state.OfKind(ReviewDiagnosticKind.ConcurrentEdit));
        Assert.Equal("c1", reported.TargetId);
        Assert.Contains("e_alice", reported.Message, StringComparison.Ordinal);
        Assert.Contains("e_bob", reported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentEditsWithNoSharedHistoryFallBackToTheOriginalBody()
    {
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice, body: "original"),
            Rec.Edit("e_alice", "c1", Rec.Alice, body: "Alice's text"),
            Rec.Edit("e_bob", "c1", Rec.Alice, body: "Bob's text"));

        Assert.Equal("original", state.OnlyComment().Body);
        Assert.Single(state.OfKind(ReviewDiagnosticKind.ConcurrentEdit));
    }

    [Fact]
    public void AnEditAndAResolveOnOneChainBothApply()
    {
        // The chain mixes ops: the head is an edit, but the resolve it observed still holds the state.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice, body: "original"),
            Rec.Resolve("r1", "c1", Rec.Bob),
            Rec.Edit("e1", "c1", Rec.Alice, body: "edited", prev: "r1"));

        var comment = state.OnlyComment();
        Assert.Equal("edited", comment.Body);
        Assert.Equal(ReviewCommentStatus.Resolved, comment.Status);
        Assert.Equal("r1", Assert.Single(comment.ResolutionRecords).Id);
    }

    [Fact]
    public void APrevChainThatLoopsIsReportedAndDoesNotHang()
    {
        // Impossible from Charter's writer, so it means tampering or a bug: report it and trust nothing about
        // the order rather than looping forever.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Resolve("a1", "c1", Rec.Bob, prev: "b1"),
            Rec.Reopen("b1", "c1", Rec.Bob, prev: "a1"));

        Assert.Single(state.OfKind(ReviewDiagnosticKind.ChainCycle));
        Assert.Equal(ReviewCommentStatus.Contested, state.OnlyComment().Status);
    }

    [Fact]
    public void ARecordWhosePrevIsItselfIsTreatedAsUnobserved()
    {
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Resolve("r1", "c1", Rec.Bob, prev: "r1"));

        Assert.Equal(ReviewCommentStatus.Resolved, state.OnlyComment().Status);
        Assert.Empty(state.OfKind(ReviewDiagnosticKind.ChainCycle));
    }

    // ---- StateHeads: the WRITER's half of the prev contract --------------------------------------------

    /// <summary>
    /// A comment nobody has acted on has no heads, so a first state record honestly claims <c>prev: null</c>.
    /// </summary>
    [Fact]
    public void AnUntouchedCommentHasNoStateHeads()
        => Assert.Empty(Rec.Fold(Rec.Create("c1", Rec.Alice)).OnlyComment().StateHeads);

    /// <summary>
    /// The heads are the records NOTHING else observed — exactly what a new state record must point its
    /// <c>prev</c> at. An observed chain collapses to its single head, across every dimension: the fold votes
    /// per-dimension, but the chain is one.
    /// </summary>
    [Fact]
    public void AnObservedChainHasExactlyOneStateHead()
    {
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Edit("e1", "c1", Rec.Alice, "second body"),
            Rec.Edit("e2", "c1", Rec.Alice, "third body", prev: "e1"),
            Rec.Resolve("r1", "c1", Rec.Bob, prev: "e2"));

        Assert.Equal("r1", Assert.Single(state.OnlyComment().StateHeads).Id);
    }

    /// <summary>
    /// Genuine concurrency yields several heads, ordered by id so the writer's choice among them — and
    /// therefore the record it appends — never depends on which order git merged the logs in.
    /// </summary>
    [Fact]
    public void ConcurrentBranchesEachYieldAStateHead_OrderedById()
    {
        var forward = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Resolve("r1", "c1", Rec.Bob),
            Rec.Reopen("o1", "c1", Rec.Carol));
        var reversed = Rec.Fold(
            Rec.Reopen("o1", "c1", Rec.Carol),
            Rec.Resolve("r1", "c1", Rec.Bob),
            Rec.Create("c1", Rec.Alice));

        Assert.Equal(new[] { "o1", "r1" }, forward.OnlyComment().StateHeads.Select(r => r.Id));
        Assert.Equal(new[] { "o1", "r1" }, reversed.OnlyComment().StateHeads.Select(r => r.Id));
    }

    /// <summary>
    /// Why <c>StateHeads</c> exists: a writer that could only send <c>prev: null</c> would make a second edit
    /// by ONE author read as a concurrent edit — reverting the body to the last agreed one and reporting a
    /// disagreement that never happened.
    /// </summary>
    [Fact]
    public void WithoutObservingTheFirstEdit_ASecondEditByTheSameAuthorLooksConcurrent()
    {
        var unobserved = Rec.Fold(
            Rec.Create("c1", Rec.Alice, body: "first body"),
            Rec.Edit("e1", "c1", Rec.Alice, "second body"),
            Rec.Edit("e2", "c1", Rec.Alice, "third body"));

        Assert.Equal("first body", unobserved.OnlyComment().Body);
        Assert.Single(unobserved.OfKind(ReviewDiagnosticKind.ConcurrentEdit));

        var observed = Rec.Fold(
            Rec.Create("c1", Rec.Alice, body: "first body"),
            Rec.Edit("e1", "c1", Rec.Alice, "second body"),
            Rec.Edit("e2", "c1", Rec.Alice, "third body", prev: "e1"));

        Assert.Equal("third body", observed.OnlyComment().Body);
        Assert.Empty(observed.OfKind(ReviewDiagnosticKind.ConcurrentEdit));
    }

    [Fact]
    public void EachCommentSettlesIndependently()
    {
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Create("c2", Rec.Alice),
            Rec.Resolve("r1", "c1", Rec.Bob),
            Rec.Resolve("r2", "c2", Rec.Bob),
            Rec.Reopen("o2", "c2", Rec.Carol));

        Assert.Equal(ReviewCommentStatus.Resolved, state.Comments.Single(c => c.Id == "c1").Status);
        Assert.Equal(ReviewCommentStatus.Contested, state.Comments.Single(c => c.Id == "c2").Status);
    }
}
