namespace Charter.Server;

/// <summary>
/// The annotation session store: a thread-safe, single-writer / locked buffer of reviewer
/// <see cref="Annotation"/>s awaiting handoff. It is the plan's flagged store-concurrency open item —
/// unlike Lavish's whole-file read-modify-write of the session JSON (where a concurrent long-poll and a
/// prompts write can race and lose annotations), Charter serializes all access so a concurrent poll +
/// prompts loses no annotation and duplicates none.
/// </summary>
/// <remarks>
/// All buffer mutation happens under a single <see cref="_gate"/> lock, so <see cref="Enqueue(Annotation)"/>
/// and <see cref="Drain"/> can never tear or interleave partially. The long-poll signal is a
/// <see cref="TaskCompletionSource{TResult}"/> that <see cref="Enqueue(Annotation)"/> completes and
/// <see cref="Drain"/> resets — an edge-triggered "something became available" primitive, never a
/// busy-wait or fixed sleep. A per-item counting semaphore is deliberately avoided: because
/// <see cref="Drain"/> removes many items in one atomic step, a per-enqueue release would drift out of sync
/// with the buffer's actual state, whereas resetting the completion source on drain keeps the signal exact.
/// </remarks>
public sealed class AnnotationStore
{
    private readonly object _gate = new();
    private readonly List<Annotation> _pending = new();

    // Edge-triggered wake signal for WaitForPendingAsync. Enqueue completes the current instance; Drain
    // swaps in a fresh (incomplete) one so the next wait blocks until the *next* Enqueue. Continuations run
    // asynchronously so completing it never runs a waiter's continuation inline while a lock is held.
    private TaskCompletionSource<bool> _pendingSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Add an annotation to the pending buffer. Safe to call concurrently with <see cref="Drain"/> and other
    /// <see cref="Enqueue(Annotation)"/> calls; no enqueued annotation may be lost.
    /// </summary>
    public void Enqueue(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);

        TaskCompletionSource<bool> signal;
        lock (_gate)
        {
            _pending.Add(annotation);
            signal = _pendingSignal;
        }

        // Wake any outstanding wait. Done outside the lock; TrySetResult is idempotent, so a signal already
        // completed (and about to be reset by a racing Drain) is a harmless no-op.
        signal.TrySetResult(true);
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
                return Array.Empty<Annotation>();
            }

            var drained = _pending.ToArray();
            _pending.Clear();

            // The items that completed the current signal have now been taken, so reset it: the next
            // WaitForPendingAsync must block until a fresh Enqueue rather than return on a stale completion.
            _pendingSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            return drained;
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

        TaskCompletionSource<bool> signal;
        lock (_gate)
        {
            _pending.InsertRange(0, annotations);
            signal = _pendingSignal;
        }

        // Wake any outstanding wait, exactly as Enqueue does — the re-added items are now pending.
        signal.TrySetResult(true);
    }

    /// <summary>
    /// The long-poll signal the annotation API waits on. Completes <c>true</c> as soon as an annotation is
    /// available (including one enqueued while the wait is outstanding), or <c>false</c> once
    /// <paramref name="timeout"/> elapses with the buffer still empty.
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
