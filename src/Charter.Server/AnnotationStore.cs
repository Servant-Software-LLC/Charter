namespace Charter.Server;

/// <summary>
/// The annotation session store: a thread-safe, single-writer / locked buffer of reviewer
/// <see cref="Annotation"/>s awaiting handoff. It is the plan's flagged store-concurrency open item —
/// unlike Lavish's whole-file read-modify-write of the session JSON (where a concurrent long-poll and a
/// prompts write can race and lose annotations), Charter serializes all access so a concurrent poll +
/// prompts loses no annotation and duplicates none.
/// </summary>
/// <remarks>
/// <para>
/// All buffer mutation happens under a single <see cref="_gate"/> lock, so <see cref="Enqueue(Annotation)"/>,
/// <see cref="Drain"/>, <see cref="Update"/>, <see cref="Remove"/> and <see cref="Requeue"/> can never tear or
/// interleave partially.
/// </para>
/// <para>
/// <b>The wake-signal invariant: the signal is completed IFF the pending buffer is non-empty.</b> The signal
/// itself — and the statement of that invariant — lives in <see cref="PendingSignal"/>, shared with the
/// <see cref="AnswerStore"/> and the <see cref="ReviewSubmissionStore"/>; <see cref="SyncSignalLocked"/>
/// re-establishes it under the lock after EVERY mutation, not just on enqueue/drain. That is what makes the
/// reviewer-facing <see cref="Remove"/> safe: a delete that empties the buffer must swap in a fresh,
/// incomplete signal, or the stale completed one would make every later <c>poll --wait</c> return instantly
/// with an empty batch — a permanent hot loop.
/// </para>
/// </remarks>
public sealed class AnnotationStore
{
    private readonly object _gate = new();
    private readonly List<Annotation> _pending = new();

    // #117 — the batch a caller has been HANDED but has not yet acknowledged. A drain used to clear the
    // buffer outright, which made delivery at-most-once past the socket write: for the few-KB payloads
    // Charter produces, Stream.Write lands in the kernel send buffer and returns success whether or not the
    // peer ever reads it, so a Ctrl-C, a harness-killed shell loop, or a laptop sleep in that window lost the
    // reviewer's annotations with no error and no trace. Answers (`/answers/ack`) and the round hand-off
    // (`/review/ack`) both had an ack; annotations — the payload that exists nowhere else once cleared — did
    // not.
    //
    // In-flight is DELIBERATELY excluded from SyncSignalLocked: it must not keep the wake signal hot, or a
    // caller that never acks would spin every later `poll --wait`.
    private readonly List<Annotation> _inFlight = new();
    private long _sequence;
    private DateTimeOffset _inFlightSince;

    // How long a handed-over batch is left alone before it is presumed abandoned and re-offered.
    //
    // Without a window, reclaiming on EVERY drain makes concurrent callers thrash: two polls microseconds
    // apart both take the same batch, because the second reclaims what the first is still about to
    // acknowledge. That turns a safety net into a duplicate generator. With one, delivery stays effectively
    // exactly-once for healthy callers (they ack in milliseconds) and falls back to at-least-once only for a
    // caller that genuinely died. Recovery latency is irrelevant next to the 30s poll cycle.
    private readonly TimeSpan _visibility;

    /// <summary>Default visibility window for a handed-over batch.</summary>
    public static readonly TimeSpan DefaultVisibility = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <paramref name="visibility"/> is how long a drained-but-unacknowledged batch is withheld from other
    /// callers before being re-offered. <see cref="TimeSpan.Zero"/> re-offers immediately, which is what a
    /// test wanting to exercise the redelivery path passes.
    /// </summary>
    public AnnotationStore(TimeSpan? visibility = null) => _visibility = visibility ?? DefaultVisibility;

    // The wake signal for WaitForPendingAsync. Its state is a function of _pending (see SyncSignalLocked):
    // completed while annotations are pending, fresh/incomplete while the buffer is empty.
    private readonly PendingSignal _pendingSignal = new();

    /// <summary>
    /// Re-establish the wake-signal invariant — <b>completed iff <see cref="_pending"/> is non-empty</b>.
    /// MUST be called under <see cref="_gate"/> after every buffer mutation, so no mutation path can leave a
    /// stale completed signal on an empty buffer (which would spin <c>poll --wait</c>) or an incomplete one on
    /// a non-empty buffer (which would stall an outstanding wait).
    /// </summary>
    private void SyncSignalLocked() => _pendingSignal.Sync(_pending.Count > 0);

    /// <summary>
    /// Add an annotation to the pending buffer. Safe to call concurrently with <see cref="Drain"/> and other
    /// <see cref="Enqueue(Annotation)"/> calls; no enqueued annotation may be lost.
    /// </summary>
    public void Enqueue(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);

        lock (_gate)
        {
            _pending.Add(annotation);
            SyncSignalLocked();
        }
    }

    /// <summary>
    /// Atomically return the currently-pending annotations and clear the buffer, so a subsequent
    /// <see cref="Drain"/> that observes no further <see cref="Enqueue(Annotation)"/> returns empty. Safe to
    /// call concurrently with <see cref="Enqueue(Annotation)"/> and other <see cref="Drain"/> calls.
    /// </summary>
    public IReadOnlyList<Annotation> Drain() => DrainBatch().Annotations;

    /// <summary>
    /// Hand over the pending annotations together with the <b>sequence</b> that acknowledges them (#117).
    /// <para>
    /// Anything still in flight from a previous drain was never acknowledged — the caller died, or its
    /// envelope never reached it — so it is put back at the FRONT and handed over again. That makes delivery
    /// AT-LEAST-ONCE: a duplicate is recoverable, a silently dropped comment is not, and the same asymmetry is
    /// already the documented posture elsewhere in the review path.
    /// </para>
    /// </summary>
    public AnnotationBatch DrainBatch()
    {
        lock (_gate)
        {
            if (_inFlight.Count > 0)
            {
                // Still inside the window: the batch is with a caller who has not had time to acknowledge.
                // Hand back NOTHING rather than a duplicate — this drain has nothing of its own to give.
                if (DateTimeOffset.UtcNow - _inFlightSince < _visibility)
                {
                    return AnnotationBatch.Empty;
                }

                // Presumed abandoned: put it back at the FRONT and offer it again.
                _pending.InsertRange(0, _inFlight);
                _inFlight.Clear();
            }

            if (_pending.Count == 0)
            {
                SyncSignalLocked();
                return AnnotationBatch.Empty;
            }

            _inFlight.AddRange(_pending);
            _inFlightSince = DateTimeOffset.UtcNow;
            _pending.Clear();

            // Syncs on _pending only — the batch is in flight, not queued, so an unacked hand-over leaves the
            // signal cold and a later `poll --wait` blocks normally instead of spinning.
            SyncSignalLocked();
            return new AnnotationBatch(++_sequence, _inFlight.ToArray());
        }
    }

    /// <summary>
    /// Acknowledge the batch identified by <paramref name="sequence"/> — the caller has the envelope and it is
    /// safe to forget. A stale or unknown sequence commits nothing: an ack for a batch that was already
    /// superseded must never discard the newer one. Returns how many were released.
    /// </summary>
    public int Ack(long sequence)
    {
        lock (_gate)
        {
            if (sequence != _sequence || _inFlight.Count == 0)
            {
                return 0;
            }

            var released = _inFlight.Count;
            _inFlight.Clear();
            return released;
        }
    }

    /// <summary>
    /// Everything that must survive a crash: the in-flight batch FIRST (it was handed over but never
    /// acknowledged, so on restart it is owed to the next caller) followed by what is still queued. This is
    /// what the durability sidecar persists — <see cref="Snapshot"/> stays pending-only because the panel must
    /// keep showing the reviewer their PRE-DRAIN queue.
    /// </summary>
    public IReadOnlyList<Annotation> DurableSnapshot()
    {
        lock (_gate)
        {
            if (_inFlight.Count == 0)
            {
                return _pending.Count == 0 ? Array.Empty<Annotation>() : _pending.ToArray();
            }

            var all = new List<Annotation>(_inFlight.Count + _pending.Count);
            all.AddRange(_inFlight);
            all.AddRange(_pending);
            return all;
        }
    }

    /// <summary>
    /// Return a snapshot of the currently-pending annotations WITHOUT removing any. Backs both the durability
    /// sidecar's persist and the in-page review panel's <c>GET /api/annotations</c> list (Charter #42): the
    /// list is the PRE-DRAIN queue, so a reviewer reading it can never consume what the agent has yet to
    /// receive. Unlike <see cref="Drain"/> it neither clears the buffer nor disturbs the wake signal.
    /// </summary>
    public IReadOnlyList<Annotation> Snapshot()
    {
        lock (_gate)
        {
            return _pending.Count == 0 ? Array.Empty<Annotation>() : _pending.ToArray();
        }
    }

    /// <summary>
    /// Replace the <see cref="Annotation.Note"/> of the still-pending annotation with <paramref name="id"/>,
    /// IN PLACE (its queue position, identity, anchor and resolved source line are preserved). Returns
    /// <see langword="false"/> when no pending annotation carries that id — it was never created, was already
    /// removed, or the agent has already drained it, in which case editing is no longer meaningful and the API
    /// answers 404. The reviewer-facing half of Charter #42, alongside <see cref="Remove"/>.
    /// </summary>
    public bool Update(string id, string note)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(note);

        lock (_gate)
        {
            for (var i = 0; i < _pending.Count; i++)
            {
                if (!string.Equals(_pending[i].Id, id, StringComparison.Ordinal))
                {
                    continue;
                }

                // `record with` produces a complete new value — never a half-written record a concurrent
                // Drain could observe torn.
                _pending[i] = _pending[i] with { Note = note };
                SyncSignalLocked();
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Remove the still-pending annotation with <paramref name="id"/> from the buffer — the reviewer
    /// RETRACTING a note before the agent ever sees it. Returns <see langword="false"/> when no pending
    /// annotation carries that id (already drained, already removed, or never created), which the API answers
    /// as 404. Nothing is removed after a drain: once handed off, the annotation belongs to the agent.
    /// </summary>
    public bool Remove(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        lock (_gate)
        {
            for (var i = 0; i < _pending.Count; i++)
            {
                if (!string.Equals(_pending[i].Id, id, StringComparison.Ordinal))
                {
                    continue;
                }

                _pending.RemoveAt(i);

                // Load-bearing: a delete that empties the buffer must re-arm the wake signal, or every later
                // `poll --wait` returns instantly with [] (a permanent hot loop).
                SyncSignalLocked();
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Re-add <paramref name="annotations"/> that were drained but never delivered — the poll write failed
    /// (client disconnected) — to the FRONT of the pending buffer under the same lock, and re-arm the pending
    /// signal so an outstanding or subsequent <see cref="WaitForPendingAsync"/> re-fetches them. This is the
    /// at-least-once guarantee: a drained batch that could not be written is not lost. Front insertion keeps
    /// the un-delivered items ahead of any that arrived after the failed drain, preserving submit order.
    /// </summary>
    public void Requeue(IReadOnlyList<Annotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        if (annotations.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            // An explicit requeue supersedes whatever is in flight — this is the write-failed path, and the
            // caller demonstrably did NOT get the envelope. Restoring to _pending (rather than leaving it in
            // flight) re-arms the wake signal, so the next `poll --wait` re-delivers immediately instead of
            // waiting out a full 30s window.
            _inFlight.Clear();
            _pending.InsertRange(0, annotations);
            SyncSignalLocked();
        }
    }

    /// <summary>
    /// The long-poll signal the annotation API waits on. Completes <c>true</c> as soon as an annotation is
    /// available (including one enqueued while the wait is outstanding), or <c>false</c> once
    /// <paramref name="timeout"/> elapses with the buffer still empty. Correct by the class invariant: the
    /// fast path reads the buffer directly, and the awaited signal is completed only while the buffer is
    /// non-empty, so an emptying <see cref="Remove"/> or <see cref="Drain"/> makes the next wait BLOCK.
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

/// <summary>
/// One handed-over batch of annotations and the sequence that acknowledges it (#117). The sequence is what
/// makes the ack safe: it names the batch being released, so an ack that arrives late — after a newer batch
/// has been handed out — commits nothing rather than discarding work the caller never saw.
/// </summary>
public sealed record AnnotationBatch(long Sequence, IReadOnlyList<Annotation> Annotations)
{
    /// <summary>Nothing to hand over. Sequence 0 is never issued, so it can never be acknowledged.</summary>
    public static AnnotationBatch Empty { get; } = new(0, Array.Empty<Annotation>());
}
