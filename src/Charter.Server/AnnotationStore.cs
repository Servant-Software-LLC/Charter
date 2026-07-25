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
/// <b>The wake-signal invariant: the signal is completed IFF the pending buffer is non-empty.</b> The signal is
/// a <see cref="TaskCompletionSource{TResult}"/> that <see cref="WaitForPendingAsync"/> awaits, and
/// <see cref="SyncSignalLocked"/> re-establishes the invariant under the lock after EVERY mutation — not just
/// on enqueue/drain. Stating it as a predicate over the buffer (rather than as an edge-triggered "something
/// became available" pulse) is what makes the reviewer-facing <see cref="Remove"/> safe: a delete that empties
/// the buffer must swap in a fresh, incomplete signal, or the stale completed one would make every later
/// <c>poll --wait</c> return instantly with an empty batch — a permanent hot loop. A per-item counting
/// semaphore is deliberately avoided: <see cref="Drain"/> removes many items in one atomic step, so a
/// per-enqueue release would drift out of sync with the buffer's actual state, whereas a predicate re-checked
/// on every mutation is exact by construction. The signal is created with
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, so completing it inside the lock never
/// runs a waiter's continuation inline.
/// </para>
/// </remarks>
public sealed class AnnotationStore
{
    private readonly object _gate = new();
    private readonly List<Annotation> _pending = new();

    // The wake signal for WaitForPendingAsync. Its state is a function of _pending (see SyncSignalLocked):
    // completed while annotations are pending, fresh/incomplete while the buffer is empty.
    private TaskCompletionSource<bool> _pendingSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Re-establish the wake-signal invariant — <b>completed iff <see cref="_pending"/> is non-empty</b>.
    /// MUST be called under <see cref="_gate"/> after every buffer mutation, so no mutation path can leave a
    /// stale completed signal on an empty buffer (which would spin <c>poll --wait</c>) or an incomplete one on
    /// a non-empty buffer (which would stall an outstanding wait).
    /// </summary>
    private void SyncSignalLocked()
    {
        if (_pending.Count > 0)
        {
            // Idempotent: already completed stays completed. RunContinuationsAsynchronously keeps any waiter's
            // continuation off this (lock-holding) thread.
            _pendingSignal.TrySetResult(true);
            return;
        }

        if (_pendingSignal.Task.IsCompleted)
        {
            _pendingSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

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
    public IReadOnlyList<Annotation> Drain()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                SyncSignalLocked();
                return Array.Empty<Annotation>();
            }

            var drained = _pending.ToArray();
            _pending.Clear();
            SyncSignalLocked();

            return drained;
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

            signalTask = _pendingSignal.Task;
        }

        try
        {
            // The signal only ever completes with true (via Enqueue); WaitAsync turns an elapsed timeout
            // into a TimeoutException and a cancellation into an OperationCanceledException.
            return await signalTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
