namespace Charter.Server;

/// <summary>
/// The answer session store: a thread-safe, single-writer / locked buffer of reviewer <see cref="Answer"/>s
/// awaiting the author/agent handoff. Unlike the <see cref="AnnotationStore"/> (whose ephemeral notes are
/// drained destructively), answers follow a <b>peek → apply → commit</b> discipline: a plain <c>charter poll</c>
/// (and the apply step) <see cref="Peek"/>s the queue WITHOUT removing anything, and answers leave the store
/// only after the caller has durably written them into the plan and calls <see cref="CommitFront"/>. This is
/// what stops a plain poll from stranding an answer with no durable home, and stops <c>--apply</c> from losing
/// an answer when the inline write fails (§1.6) — a failed apply simply never commits, so the answer stays.
/// </summary>
/// <remarks>
/// <para>
/// All buffer mutation happens under a single <see cref="_gate"/> lock, so <see cref="Enqueue(Answer)"/>,
/// <see cref="Peek"/>, and <see cref="CommitFront(int)"/> can never tear or interleave partially.
/// <see cref="Enqueue(Answer)"/> only ever APPENDS, so the answers a caller peeked are always the current
/// FRONT of the buffer: <see cref="CommitFront(int)"/> removes exactly that peeked prefix, and any answer that
/// arrived after the peek (appended to the back) survives for the next cycle.
/// </para>
/// <para>
/// <b>The wake-signal invariant (Charter #62): the signal is completed IFF the pending buffer is non-empty</b>
/// — stated once in <see cref="PendingSignal"/> and re-established by <see cref="SyncSignalLocked"/> under the
/// lock after EVERY mutation. The store used to carry no signal at all, so a submitted answer — the reviewer's
/// highest-value output — woke no waiting agent and sat in the queue until the long-poll's ~30s timeout. The
/// mutation that must not be missed is <see cref="CommitFront(int)"/>, the ACK: a drained-then-acked queue
/// that left a stale completed signal would make every later <c>poll --wait</c> return instantly with nothing
/// — a hot loop, not a wait. <see cref="Peek"/> mutates nothing and so deliberately leaves the signal alone.
/// </para>
/// </remarks>
public sealed class AnswerStore
{
    private readonly object _gate = new();
    private readonly List<Answer> _pending = new();

    // The wake signal for WaitForPendingAsync. Its state is a function of _pending (see SyncSignalLocked):
    // completed while answers are pending, fresh/incomplete while the buffer is empty.
    private readonly PendingSignal _pendingSignal = new();

    /// <summary>
    /// Re-establish the wake-signal invariant — <b>completed iff <see cref="_pending"/> is non-empty</b>.
    /// MUST be called under <see cref="_gate"/> after every buffer mutation.
    /// </summary>
    private void SyncSignalLocked() => _pendingSignal.Sync(_pending.Count > 0);

    /// <summary>
    /// Append an answer to the pending buffer. Safe to call concurrently with <see cref="Peek"/> and
    /// <see cref="CommitFront(int)"/>; no enqueued answer may be lost.
    /// </summary>
    public void Enqueue(Answer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        lock (_gate)
        {
            _pending.Add(answer);
            SyncSignalLocked();
        }
    }

    /// <summary>
    /// Return a snapshot of the currently-pending answers WITHOUT removing any (a report/peek). A subsequent
    /// <see cref="Peek"/> with no intervening <see cref="CommitFront(int)"/> returns the same answers, so a
    /// plain poll can report queued answers while leaving them recoverable. Safe under concurrent access.
    /// </summary>
    public IReadOnlyList<Answer> Peek()
    {
        lock (_gate)
        {
            return _pending.Count == 0 ? Array.Empty<Answer>() : _pending.ToArray();
        }
    }

    /// <summary>
    /// Remove up to <paramref name="count"/> answers from the FRONT of the buffer — the prefix a caller
    /// previously <see cref="Peek"/>ed and has now durably applied — and return the removed answers. Because
    /// <see cref="Enqueue(Answer)"/> only appends, the front prefix is exactly the peeked set; answers that
    /// arrived after the peek sit behind it and are untouched. A <paramref name="count"/> beyond the current
    /// buffer size removes only what is present. This is the ONLY path that removes an answer, so an answer is
    /// gone from the store solely after a successful commit. Safe under concurrent access.
    /// </summary>
    public IReadOnlyList<Answer> CommitFront(int count)
    {
        if (count <= 0)
        {
            return Array.Empty<Answer>();
        }

        lock (_gate)
        {
            var take = Math.Min(count, _pending.Count);
            if (take == 0)
            {
                return Array.Empty<Answer>();
            }

            var removed = new Answer[take];
            _pending.CopyTo(0, removed, 0, take);
            _pending.RemoveRange(0, take);

            // Load-bearing: the ack that empties the buffer must re-arm the wake signal, or every later
            // `poll --wait` returns instantly with nothing queued (a permanent hot loop).
            SyncSignalLocked();
            return removed;
        }
    }

    /// <summary>
    /// The long-poll signal the review server waits on alongside the annotation store's. Completes
    /// <c>true</c> as soon as an answer is available (including one enqueued while the wait is outstanding),
    /// or <c>false</c> once <paramref name="timeout"/> elapses with the buffer still empty. Correct by the
    /// class invariant: the fast path reads the buffer directly, and the awaited signal is completed only
    /// while the buffer is non-empty, so an emptying <see cref="CommitFront(int)"/> makes the next wait BLOCK.
    /// </summary>
    /// <param name="timeout">How long to wait before completing <c>false</c> on an empty store.</param>
    /// <param name="cancellationToken">Cancels the outstanding wait.</param>
    public async Task<bool> WaitForPendingAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task<bool> signalTask;
        lock (_gate)
        {
            // Fast path: something is already queued, so no need to wait at all.
            if (_pending.Count > 0)
            {
                return true;
            }

            signalTask = _pendingSignal.Pending;
        }

        return await PendingSignal.AwaitAsync(signalTask, timeout, cancellationToken).ConfigureAwait(false);
    }
}
