using System;
using System.Threading;

namespace Charter.Server;

/// <summary>
/// Whether anything is actually draining this review session — the difference between "handed over and being
/// worked" and "handed over to nobody" (#107).
/// <para>
/// Before this, the panel knew <c>submitted</c> and the pending counts but had no idea whether an agent
/// existed. A reviewer who clicked <b>Send to agent</b> could not distinguish an agent thinking from no agent
/// at all: both are silence, and the second is indistinguishable from patience until far too much of it has
/// passed.
/// </para>
/// <para>
/// <b>Purely observational.</b> Nothing registers, nothing heartbeats, and no agent has to opt in — presence
/// is inferred from drains the server already handles. That keeps the invariant intact: the server never
/// writes the plan and never invokes an agent, it only reports what it has seen.
/// </para>
/// <para>
/// Two distinct facts, because they answer different questions. <see cref="Waiting"/> is <b>certain</b> — an
/// agent is blocked in a long poll on this session right now. <see cref="LastSeenUtc"/> is <b>evidence</b> —
/// something drained at that moment, which covers the agent that polls in a loop and is therefore absent
/// between polls. A one-shot poller is every bit as live as a blocked one and must not read as dead.
/// </para>
/// </summary>
public sealed class AgentPresence
{
    private long _waiting;
    private long _lastSeenTicks;

    /// <summary>An agent is blocked in a long poll on this session <b>right now</b>.</summary>
    public bool Waiting => Interlocked.Read(ref _waiting) > 0;

    /// <summary>When an agent last drained or waited, or <see langword="null"/> if one never has.</summary>
    public DateTimeOffset? LastSeenUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSeenTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Record agent contact — any drain, long-polling or not.</summary>
    public void Seen() => Interlocked.Exchange(ref _lastSeenTicks, DateTimeOffset.UtcNow.UtcTicks);

    /// <summary>
    /// Scope an agent's long poll. Counted rather than flagged so concurrent waits cannot have the first to
    /// finish clear presence while others are still blocked; disposal is in a <c>finally</c> at the call site,
    /// so an abandoned or cancelled wait releases too — a crashed agent must not leave the panel claiming
    /// someone is home.
    /// </summary>
    public IDisposable BeginWait()
    {
        Interlocked.Increment(ref _waiting);
        Seen();
        return new WaitScope(this);
    }

    private sealed class WaitScope(AgentPresence presence) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Interlocked.Decrement(ref presence._waiting);
                presence.Seen();
            }
        }
    }
}
