using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// The authorship rules of <c>docs/plans/03-git-mediated-team-review.md</c> §4.2: a retract is valid only
/// from the item's own author (otherwise a teammate can silently delete a blocking objection),
/// resolve/reopen are open to anyone and always attributed, and a retract never removes other people's
/// replies.
/// </summary>
[Trait("Category", "ReviewLog")]
public class ReviewLogAuthorshipTests
{
    [Fact]
    public void RetractByTheCommentsOwnAuthorHidesTheBodyButKeepsTheComment()
    {
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice, body: "I withdraw this."),
            Rec.Retract("x1", "c1", Rec.Alice));

        var comment = state.OnlyComment();
        Assert.Equal(ReviewCommentStatus.Retracted, comment.Status);
        Assert.True(comment.IsRetracted);
        Assert.Null(comment.Body);
        Assert.Equal("x1", comment.RetractRecord!.Id);
        Assert.Empty(state.OfKind(ReviewDiagnosticKind.RetractNotByAuthor));
    }

    [Fact]
    public void RetractByAnyoneElseIsRetainedReportedAndNotApplied()
    {
        // The failure this prevents: a teammate silently deleting a blocking objection.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice, body: "This plan will not meet the latency budget."),
            Rec.Retract("x1", "c1", Rec.Bob));

        var comment = state.OnlyComment();
        Assert.NotEqual(ReviewCommentStatus.Retracted, comment.Status);
        Assert.False(comment.IsRetracted);
        Assert.Equal("This plan will not meet the latency budget.", comment.Body);

        var rejected = Assert.Single(state.OfKind(ReviewDiagnosticKind.RetractNotByAuthor));
        Assert.Equal("x1", rejected.RecordId);
        Assert.Equal("c1", rejected.TargetId);
        Assert.Equal(Rec.Bob, rejected.Record!.Author.Email);
    }

    [Fact]
    public void ARejectedRetractStillCountsAsObservedByLaterRecords()
    {
        // Bob's retract does not apply, but Alice really did see it when she resolved on top of it — so her
        // resolve settles rather than being treated as a concurrent branch.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Resolve("r_bob", "c1", Rec.Bob),
            Rec.Retract("x_bob", "c1", Rec.Bob, prev: "r_bob"),
            Rec.Reopen("o_alice", "c1", Rec.Alice, prev: "x_bob"));

        var comment = state.OnlyComment();
        Assert.False(comment.IsRetracted);
        Assert.Equal(ReviewCommentStatus.Open, comment.Status);
        Assert.Equal("o_alice", Assert.Single(comment.ResolutionRecords).Id);
    }

    [Fact]
    public void RetractOfACommentWithRepliesKeepsTheThreadAndTheReplies()
    {
        // Replies are other people's words: they are never removed by someone else's retract, and the panel
        // renders "(comment withdrawn by author)" with the thread intact.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice, body: "Withdrawn question."),
            Rec.Reply("p1", "c1", Rec.Bob, body: "Actually this matters because…"),
            Rec.Reply("p2", "c1", Rec.Carol, body: "Agreed with Bob."),
            Rec.Retract("x1", "c1", Rec.Alice));

        var comment = state.OnlyComment();
        Assert.Equal(ReviewCommentStatus.Retracted, comment.Status);
        Assert.Null(comment.Body);
        Assert.Equal(new[] { "p1", "p2" }, comment.Replies.Select(r => r.Id));
        Assert.Equal("Actually this matters because…", comment.Replies[0].Body);
        Assert.Equal("Agreed with Bob.", comment.Replies[1].Body);
        Assert.All(comment.Replies, r => Assert.False(r.IsRetracted));
    }

    [Fact]
    public void TheWithdrawnTextIsStillOnTheRecordForToolingButNotOnTheComment()
    {
        // Retract HIDES the body; it does not (and cannot) erase it — the text is in git history forever
        // (§7). Body is null so a careless renderer cannot leak it.
        var comment = Rec.Fold(
            Rec.Create("c1", Rec.Alice, body: "Withdrawn."),
            Rec.Retract("x1", "c1", Rec.Alice)).OnlyComment();

        Assert.Null(comment.Body);
        Assert.Equal("Withdrawn.", comment.Record.Body);
    }

    [Fact]
    public void ARetractOfAReplyHidesOnlyThatReply()
    {
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice, body: "the question"),
            Rec.Reply("p1", "c1", Rec.Bob, body: "a reply Bob regrets"),
            Rec.Reply("p2", "c1", Rec.Carol, body: "Carol's reply"),
            Rec.Retract("x1", "p1", Rec.Bob));

        var comment = state.OnlyComment();
        Assert.Equal(ReviewCommentStatus.Open, comment.Status);
        Assert.Equal("the question", comment.Body);
        Assert.True(comment.Replies[0].IsRetracted);
        Assert.Null(comment.Replies[0].Body);
        Assert.Equal("Carol's reply", comment.Replies[1].Body);
    }

    [Fact]
    public void ARetractOfSomeoneElsesReplyIsRejected()
    {
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Reply("p1", "c1", Rec.Bob, body: "Bob's reply"),
            Rec.Retract("x1", "p1", Rec.Alice));

        Assert.False(Assert.Single(state.OnlyComment().Replies).IsRetracted);
        Assert.Equal("p1", Assert.Single(state.OfKind(ReviewDiagnosticKind.RetractNotByAuthor)).TargetId);
    }

    [Fact]
    public void ResolveAndReopenAreOpenToAnyoneAndAlwaysAttributed()
    {
        // Review is collaborative: anyone may resolve or reopen anyone's comment, but the panel must always
        // be able to say who did.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Resolve("r1", "c1", Rec.Carol));

        var settled = Assert.Single(state.OnlyComment().ResolutionRecords);
        Assert.Equal(Rec.Carol, settled.Author.Email);
        Assert.Equal("carol", settled.Author.Name);
        Assert.Empty(state.OfKind(ReviewDiagnosticKind.RetractNotByAuthor));
    }

    [Fact]
    public void AnEditOfSomeoneElsesCommentIsApplied()
    {
        // The design restricts only RETRACT by author. Edits are not restricted here; if that is ever wanted
        // it is a design change, not a quiet one in the fold.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice, body: "original"),
            Rec.Edit("e1", "c1", Rec.Bob, body: "Bob's edit"));

        Assert.Equal("Bob's edit", state.OnlyComment().Body);
    }

    [Fact]
    public void AuthorIdentityIsTheEmailAndIsCaseInsensitive()
    {
        // git config is not case-normalized; an author must not lose the right to withdraw their own comment
        // because their email is capitalized differently on their laptop.
        var state = Rec.Fold(
            Rec.Create("c1", "Alice@Example.com"),
            Rec.Retract("x1", "c1", "alice@example.com"));

        Assert.Equal(ReviewCommentStatus.Retracted, state.OnlyComment().Status);
    }

    [Fact]
    public void ADifferentAuthorWithTheSameNameCannotRetract()
    {
        var state = Rec.Fold(
            Rec.Create("c1", "alice@ng.example.com"),
            Rec.Retract("x1", "c1", "alice.ng@example.com"));

        Assert.False(state.OnlyComment().IsRetracted);
        Assert.Single(state.OfKind(ReviewDiagnosticKind.RetractNotByAuthor));
    }

    [Fact]
    public void ResolveAimedAtAReplyIsRetainedReportedAndNotApplied()
    {
        // Resolution is a property of a comment thread, not of one reply. Guessing which thread a reply-level
        // resolve meant is exactly the kind of inference §4.3 forbids elsewhere.
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Reply("p1", "c1", Rec.Bob),
            Rec.Resolve("r1", "p1", Rec.Bob));

        Assert.Equal(ReviewCommentStatus.Open, state.OnlyComment().Status);
        var reported = Assert.Single(state.OfKind(ReviewDiagnosticKind.UnsupportedTarget));
        Assert.Equal("r1", reported.RecordId);
        Assert.Equal("p1", reported.TargetId);
    }

    [Fact]
    public void ANestedReplyAttachesToItsRootCommentAndKeepsWhatItAnswered()
    {
        var state = Rec.Fold(
            Rec.Create("c1", Rec.Alice),
            Rec.Reply("p1", "c1", Rec.Bob),
            Rec.Reply("p2", "p1", Rec.Carol));

        var comment = state.OnlyComment();
        Assert.Equal(new[] { "p1", "p2" }, comment.Replies.Select(r => r.Id));
        Assert.Equal("c1", comment.Replies[0].InReplyTo);
        Assert.Equal("p1", comment.Replies[1].InReplyTo);
    }
}
