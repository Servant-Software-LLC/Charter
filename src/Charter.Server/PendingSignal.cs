namespace Charter.Server;

/// <summary>
/// The edge-triggered wake signal a review store hands to the <c>/api/poll</c> long-poll, and the ONE place
/// its invariant is stated: <b>the signal is completed IFF the owning store has pending work</b>.
/// </summary>
/// <remarks>
/// <para>
/// Stating the signal as a PREDICATE over the buffer — re-established under the owner's lock after EVERY
/// mutation, not just on enqueue — is what makes it safe. A signal left completed over an EMPTY buffer makes
/// every later <c>poll --wait</c> return instantly with nothing: a permanent hot loop rather than a wait. A
/// signal left incomplete over a NON-empty buffer strands an outstanding wait until its timeout. Both bugs are
/// mutation-path-shaped, which is why the rule is "call <see cref="Sync"/> after every mutation" rather than
/// "call it where it seems needed": the annotation store learned it on its reviewer-facing delete (Charter
/// #42), and the answer store on its ack/commit (Charter #62).
/// </para>
/// <para>
/// A per-item counting semaphore is deliberately avoided: a drain removes many items in one atomic step, so a
/// per-enqueue release would drift out of sync with the buffer's actual state, whereas a predicate re-checked
/// on every mutation is exact by construction. The underlying
/// <see cref="TaskCompletionSource{TResult}"/> is created with
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, so completing it inside the owner's lock
/// never runs a waiter's continuation inline on the lock-holding thread.
/// </para>
/// </remarks>
internal sealed class PendingSignal
{
    private TaskCompletionSource<bool> _signal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Re-establish the invariant for a store whose buffer is (<paramref name="hasPending"/>) non-empty or not.
    /// MUST be called under the owning store's lock after every buffer mutation, so no mutation path can leave
    /// a stale completed signal on an empty buffer or an incomplete one on a non-empty buffer.
    /// </summary>
    public void Sync(bool hasPending)
    {
        if (hasPending)
        {
            // Idempotent: already completed stays completed.
            _signal.TrySetResult(true);
            return;
        }

        if (_signal.Task.IsCompleted)
        {
            _signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>
    /// The task an outstanding wait awaits. MUST be read under the owning store's lock, in the same critical
    /// section as the "is the buffer empty?" fast-path check — capturing the two atomically is what stops a
    /// mutation slipping between the check and the capture.
    /// </summary>
    public Task<bool> Pending => _signal.Task;

    /// <summary>
    /// Await a captured <paramref name="signal"/>: <c>true</c> as soon as work is available, or <c>false</c>
    /// once <paramref name="timeout"/> elapses. The signal only ever completes with <c>true</c>;
    /// <see cref="TaskExtensions"/>' <c>WaitAsync</c> turns an elapsed timeout into a
    /// <see cref="TimeoutException"/> (mapped to <c>false</c> here) and a cancellation into an
    /// <see cref="OperationCanceledException"/> (propagated to the caller).
    /// </summary>
    public static async Task<bool> AwaitAsync(
        Task<bool> signal, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await signal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
