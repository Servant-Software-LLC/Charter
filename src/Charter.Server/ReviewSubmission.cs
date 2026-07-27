namespace Charter.Server;

/// <summary>
/// One reviewer round HAND-OFF: the record that the human clicked <b>Send to agent</b> in the review page and
/// considers this round of feedback complete. It is a SIGNAL, not an instruction — nothing here writes the
/// plan. The drafting agent stays the single writer of the <c>.charter.md</c> (Architecture B); a hand-off
/// only tells it "the human is done with this round" so it can distinguish an explicit hand-off from "one more
/// comment just arrived".
/// </summary>
/// <param name="Sequence">
/// Monotonic per-session hand-off number. It exists so the agent's ack is a COMPARE-and-clear: a reviewer who
/// hands off a second round while the first ack is in flight must not have that newer hand-off swallowed.
/// </param>
/// <param name="SubmittedAt">When the reviewer handed the round off (UTC).</param>
/// <param name="Annotations">How many annotations were queued at that moment.</param>
/// <param name="Answers">How many <c>:::question</c> answers were queued at that moment.</param>
public sealed record ReviewSubmission(
    long Sequence, DateTimeOffset SubmittedAt, int Annotations, int Answers);

/// <summary>
/// The per-session store for the reviewer's round hand-off: at most ONE pending
/// <see cref="ReviewSubmission"/>, plus the same edge-triggered wake signal the annotation and answer stores
/// carry, so clicking <b>Send to agent</b> wakes a waiting <c>charter poll --wait</c> even when both queues
/// are already drained.
/// </summary>
/// <remarks>
/// <b>The wake-signal invariant: the signal is completed IFF a hand-off is pending</b> — stated once in
/// <see cref="PendingSignal"/> and re-established under the lock after every mutation, including
/// <see cref="Ack(long)"/>. Miss the ack and a cleared hand-off leaves a stale completed signal, and
/// <c>poll --wait</c> spins instead of waiting.
///
/// State is in-memory only, deliberately: unlike a queued annotation or answer (the reviewer's CONTENT, which
/// the durability sidecar protects — §1.6), a hand-off is a transient signal about a live loop. Losing it to a
/// server restart costs one click, and the content it referred to is still queued and still drains.
/// </remarks>
public sealed class ReviewSubmissionStore
{
    private readonly object _gate = new();
    private readonly PendingSignal _pendingSignal = new();

    private ReviewSubmission? _pending;
    private long _sequence;

    /// <summary>
    /// Record a hand-off of the round that currently holds <paramref name="annotations"/> annotations and
    /// <paramref name="answers"/> answers, and wake any outstanding long-poll. A second hand-off before the
    /// agent acks REPLACES the first (there is one round in flight, and its counts are the newest ones).
    /// </summary>
    public ReviewSubmission Submit(int annotations, int answers)
    {
        lock (_gate)
        {
            _pending = new ReviewSubmission(++_sequence, DateTimeOffset.UtcNow, annotations, answers);
            SyncSignalLocked();
            return _pending;
        }
    }

    /// <summary>The pending hand-off, or <see langword="null"/> when the round has not been handed off.</summary>
    public ReviewSubmission? Peek()
    {
        lock (_gate)
        {
            return _pending;
        }
    }

    /// <summary>
    /// Clear the pending hand-off — the agent has been told — but ONLY when it is still
    /// <paramref name="sequence"/>. Returns <see langword="false"/> when there is nothing pending or the
    /// reviewer has since handed off a newer round, which must survive the older ack.
    /// </summary>
    public bool Ack(long sequence)
    {
        lock (_gate)
        {
            if (_pending is null || _pending.Sequence != sequence)
            {
                return false;
            }

            _pending = null;

            // Load-bearing, exactly as on the other two stores: clearing must re-arm the signal, or every
            // later `poll --wait` returns instantly on a hand-off that has already been delivered.
            SyncSignalLocked();
            return true;
        }
    }

    /// <summary>
    /// The long-poll signal the review server waits on alongside the annotation and answer stores'. Completes
    /// <c>true</c> as soon as a hand-off is pending (including one submitted while the wait is outstanding),
    /// or <c>false</c> once <paramref name="timeout"/> elapses with none.
    /// </summary>
    public async Task<bool> WaitForPendingAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task<bool> signalTask;
        lock (_gate)
        {
            if (_pending is not null)
            {
                return true;
            }

            signalTask = _pendingSignal.Pending;
        }

        return await PendingSignal.AwaitAsync(signalTask, timeout, cancellationToken).ConfigureAwait(false);
    }

    private void SyncSignalLocked() => _pendingSignal.Sync(_pending is not null);
}
