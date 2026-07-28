using System.Security.Cryptography;
using System.Text;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// §4.3.1 of <c>docs/plans/03-git-mediated-team-review.md</c> (the resolution of Charter #74): the review log
/// is never quarantined, so the EVIDENCE travels instead — each comment says which revision of the plan it was
/// written against.
/// </summary>
/// <remarks>
/// The asymmetry these tests pin is the whole design: <c>current</c> is a SOUND POSITIVE and must never be
/// wrong; <c>different</c> proves only "not exactly this text" and is the modal state of a living document;
/// <c>unknown</c> is what an unverifiable plan or a record that does not say must produce, because
/// "the plan is not what you commented on" is a claim Charter would not have read the plan to make.
/// </remarks>
[Trait("Category", "ReviewBaseStatus")]
public class ReviewBaseStatusTests
{
    private const string Plan =
        "# A Plan\n\nAn overview paragraph.\n\nThe paragraph a reviewer commented on.\n";

    /// <summary>
    /// The sound positive: a plan byte-identical to the text the comment was written against IS that document,
    /// by every definition Charter has.
    /// </summary>
    [Fact]
    public void ABaseMintedFromThisExactPlan_IsCurrent()
    {
        Assert.Equal(
            ReviewStatusTokens.BaseCurrent,
            ReviewBaseStatus.ForPlan(Plan).Of(ReviewLogBridge.PlanHash(Plan)));
    }

    /// <summary>
    /// A base minted from any other text is <c>different</c> — and that is ALL it says. The token deliberately
    /// does not distinguish an ordinary edit from a wholesale replacement; nothing local can.
    /// </summary>
    [Fact]
    public void ABaseMintedFromOtherText_IsDifferent()
    {
        Assert.Equal(
            ReviewStatusTokens.BaseDifferent,
            ReviewBaseStatus.ForPlan(Plan).Of(ReviewLogBridge.PlanHash("# A completely different document\n")));
    }

    /// <summary>A record that does not say which revision it saw cannot be judged — it is not "different".</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ARecordWithNoBase_IsUnknown(string? recordBase)
        => Assert.Equal(ReviewStatusTokens.BaseUnknown, ReviewBaseStatus.ForPlan(Plan).Of(recordBase));

    /// <summary>
    /// A plan with no text answers <c>unknown</c> for every comment, never <c>different</c>. Claiming "the plan
    /// is not the text you commented on" without having read the plan is exactly the confident-but-unfounded
    /// answer this design refuses everywhere else.
    /// <para>
    /// <b>Empty counts as no text</b>, and that is load-bearing rather than pedantic: the reachable ways to
    /// hold an empty plan are a read that raced the drafting agent's truncate-then-write, an interrupted save,
    /// and a caller that answers an unreadable file with <c>string.Empty</c>. Without this, an entire review
    /// would read <c>different</c> — the unearned claim §4.3.1 exists to remove — at the exact moment the plan
    /// is being rewritten.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void APlanWithNoText_IsUnknown_NeverDifferent(string? markdown)
    {
        var status = ReviewBaseStatus.ForPlan(markdown).Of(ReviewLogBridge.PlanHash(Plan));

        Assert.Equal(ReviewStatusTokens.BaseUnknown, status);
        Assert.NotEqual(ReviewStatusTokens.BaseDifferent, status);
    }

    /// <summary>
    /// <b>Line endings are not content.</b> A teammate on Linux mints the base over LF; this machine's checkout
    /// has CRLF (or the reverse). Without the newline-form comparison the whole signal reads <c>different</c>
    /// on every comment at the IDENTICAL revision — dead exactly where team review lives.
    /// <c>Block.StableId</c> normalizes CRLF before hashing for the same reason.
    /// </summary>
    [Theory]
    [InlineData("\n", "\r\n")]
    [InlineData("\r\n", "\n")]
    public void ABaseMintedUnderDifferentLineEndings_IsStillCurrent(string mintedWith, string readAs)
    {
        var minted = ReviewLogBridge.PlanHash(Plan.Replace("\n", mintedWith));

        Assert.Equal(
            ReviewStatusTokens.BaseCurrent,
            ReviewBaseStatus.ForPlan(Plan.Replace("\n", readAs)).Of(minted));
    }

    /// <summary>
    /// A hex digest's case is not a difference in the document. Charter writes lowercase, but a record is a
    /// committed artifact another tool may have produced.
    /// </summary>
    [Fact]
    public void AnUppercaseHexDigest_IsStillCurrent()
    {
        Assert.Equal(
            ReviewStatusTokens.BaseCurrent,
            ReviewBaseStatus.ForPlan(Plan).Of(ReviewLogBridge.PlanHash(Plan).ToUpperInvariant()));
    }

    /// <summary>
    /// The wire format of <c>anchor.base</c>, pinned independently of the implementation that mints it: it is a
    /// committed, immutable artifact, so a change to how it is computed is a schema change and must break here
    /// rather than quietly making every existing record read <c>different</c> forever.
    /// </summary>
    [Fact]
    public void TheBaseFormat_IsSha256OfThePlanBytes_PinnedIndependently()
    {
        var expected = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Plan))).ToLowerInvariant();

        Assert.Equal(expected, ReviewLogBridge.PlanHash(Plan));
        Assert.Equal(ReviewStatusTokens.BaseCurrent, ReviewBaseStatus.ForPlan(Plan).Of(expected));
    }
}
