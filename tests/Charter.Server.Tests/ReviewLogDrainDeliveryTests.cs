using System;
using System.IO;
using System.Linq;
using Charter.Core;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// The server-less review-log read as a DELIVERY mechanism: what it reports, and when it is allowed to write
/// that down. §4.3.1 of <c>docs/plans/03-git-mediated-team-review.md</c> makes "every comment the fold holds is
/// delivered" normative — which is only worth anything if a delivery that never reached the agent is not
/// recorded as one.
/// </summary>
[Trait("Category", "ReviewLogDrain")]
public class ReviewLogDrainDeliveryTests : IDisposable
{
    private const string Plan =
        "# Delivery Plan\n\nAn overview paragraph.\n\nThe paragraph a teammate comments on.\n";

    private static readonly ReviewAuthor Bob = new("Bob Chen", "bob@example.com");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "charter-drain-delivery-" + Guid.NewGuid().ToString("N"));

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
            // A leftover temp directory is harmless.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// <b>At-least-once, proved by the split.</b> Reading does not consume; only an explicit
    /// <see cref="ReviewLogDrain.ConfirmDelivered"/> does — so a crash, a broken pipe, or a killed process
    /// between the read and the envelope write costs a repeat, never a committed objection.
    /// <para>
    /// Recording consumption inside the read would make this at-MOST-once, and a lost comment would be
    /// invisible: every later poll would report a clean empty.
    /// </para>
    /// </summary>
    [Fact]
    public void Drain_DoesNotConsume_UntilDeliveryIsConfirmed()
    {
        var plan = WritePlan();
        var consumed = Path.Combine(_root, "consumed");
        new ReviewLogWriter(plan, Bob).AppendCreate(Anchor(), "an objection that must not evaporate");

        var first = ReviewLogDrain.Drain(plan, consumed);
        Assert.Single(first.Annotations);
        Assert.NotEmpty(first.Delivered);

        // The envelope was never written — so the comment is still owed to this machine's agent.
        var second = ReviewLogDrain.Drain(plan, consumed);
        Assert.Single(second.Annotations);

        ReviewLogDrain.ConfirmDelivered(plan, consumed, second.Delivered);

        Assert.Empty(ReviewLogDrain.Drain(plan, consumed).Annotations);
    }

    /// <summary>
    /// A plan with no readable text answers <c>unknown</c> for every comment — never <c>different</c>, which
    /// would tell a whole review "the plan is not what you commented on" on the strength of a read that raced
    /// the drafting agent's truncate-then-write. The anchors orphan, which is the existing honest answer; the
    /// revision claim is simply not made.
    /// </summary>
    [Fact]
    public void AnEmptyPlan_ReportsUnknown_ForEveryComment_NeverDifferent()
    {
        var plan = WritePlan();
        new ReviewLogWriter(plan, Bob).AppendCreate(
            new ReviewAnchor(AnchorId(), "element", "a quote", ReviewLogBridge.PlanHash(Plan)),
            "a comment read while the plan was mid-write");

        File.WriteAllText(plan, string.Empty);

        var annotation = Assert.Single(ReviewLogDrain.Drain(plan, Path.Combine(_root, "consumed")).Annotations);

        Assert.Equal(ReviewStatusTokens.BaseUnknown, annotation.BaseStatus);
        Assert.Equal(AnchorStatus.Orphaned, annotation.AnchorStatus);
    }

    private string WritePlan()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "team.charter.md");
        File.WriteAllText(path, Plan);
        return path;
    }

    private static string AnchorId()
        => SourceMap.Build(Plan).Anchors.OrderBy(a => a, StringComparer.Ordinal).ElementAt(1);

    private static ReviewAnchor Anchor() => new(AnchorId(), "element", "a quote", null);
}
