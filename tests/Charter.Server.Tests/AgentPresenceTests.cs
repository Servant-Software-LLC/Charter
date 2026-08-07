using System;
using System.Threading;
using System.Threading.Tasks;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Whether anything is draining a review session (#107). The panel knew <c>submitted</c> and the pending
/// counts but nothing about whether an agent existed, so a reviewer who handed a round over could not tell a
/// working agent from no agent — both are silence, and the second is indistinguishable from patience.
/// </summary>
[Trait("Category", "AgentPresence")]
public class AgentPresenceTests
{
    [Fact]
    public void AFreshSession_HasSeenNoAgent()
    {
        var presence = new AgentPresence();

        Assert.False(presence.Waiting);
        Assert.Null(presence.LastSeenUtc);
    }

    /// <summary>
    /// A one-shot poller in a shell loop is every bit as live as a blocked one, and is absent between polls.
    /// Evidence of contact must therefore survive the drain that produced it.
    /// </summary>
    [Fact]
    public void ADrainCountsAsContact_EvenWithoutALongPoll()
    {
        var presence = new AgentPresence();

        presence.Seen();

        Assert.False(presence.Waiting);
        Assert.NotNull(presence.LastSeenUtc);
    }

    /// <summary>While an agent is blocked in a long poll, presence is CERTAIN rather than inferred.</summary>
    [Fact]
    public void AWaitingAgentIsListening_AndStopsBeingSoWhenTheWaitEnds()
    {
        var presence = new AgentPresence();

        using (presence.BeginWait())
        {
            Assert.True(presence.Waiting);
        }

        Assert.False(presence.Waiting);
        Assert.NotNull(presence.LastSeenUtc);
    }

    /// <summary>
    /// Counted, not flagged: with concurrent waits the first to finish must not clear presence while others
    /// are still blocked, or the panel would report nobody home while an agent is demonstrably waiting.
    /// </summary>
    [Fact]
    public void ConcurrentWaits_StayListeningUntilTheLastOneEnds()
    {
        var presence = new AgentPresence();

        var first = presence.BeginWait();
        var second = presence.BeginWait();

        first.Dispose();
        Assert.True(presence.Waiting, "one wait ending must not clear presence while another is blocked");

        second.Dispose();
        Assert.False(presence.Waiting);
    }

    /// <summary>
    /// A crashed or cancelled agent must not leave the panel claiming someone is home. Disposal is idempotent
    /// so a double release cannot drive the count negative and strand presence in a permanently-true state.
    /// </summary>
    [Fact]
    public void ReleasingTwice_IsIdempotent_AndCannotStrandPresence()
    {
        var presence = new AgentPresence();

        var scope = presence.BeginWait();
        scope.Dispose();
        scope.Dispose();

        Assert.False(presence.Waiting);

        using (presence.BeginWait())
        {
            Assert.True(presence.Waiting, "a stale double-release must not have driven the count below zero");
        }

        Assert.False(presence.Waiting);
    }

    [Fact]
    public async Task LastSeen_MovesForwardOnEachContact()
    {
        var presence = new AgentPresence();
        presence.Seen();
        var first = presence.LastSeenUtc;

        await Task.Delay(20);
        presence.Seen();

        Assert.True(presence.LastSeenUtc >= first);
    }
}
