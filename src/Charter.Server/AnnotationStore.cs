namespace Charter.Server;

/// <summary>
/// The annotation session store: a thread-safe, single-writer / locked buffer of reviewer
/// <see cref="Annotation"/>s awaiting handoff. It is the plan's flagged store-concurrency open item —
/// unlike Lavish's whole-file read-modify-write of the session JSON (where a concurrent long-poll and a
/// prompts write can race and lose annotations), Charter serializes all access so a concurrent poll +
/// prompts loses no annotation and duplicates none.
/// </summary>
/// <remarks>
/// This is the TDD-red behavioral stub: every member throws <see cref="NotImplementedException"/>. The real
/// single-writer implementation lands in the <c>02-implement-session-store</c> task.
/// </remarks>
public sealed class AnnotationStore
{
    /// <summary>
    /// Add an annotation to the pending buffer. Safe to call concurrently with <see cref="Drain"/> and other
    /// <see cref="Enqueue(Annotation)"/> calls; no enqueued annotation may be lost.
    /// </summary>
    public void Enqueue(Annotation annotation) => throw new NotImplementedException();

    /// <summary>
    /// Atomically return the currently-pending annotations and clear the buffer, so a subsequent
    /// <see cref="Drain"/> that observes no further <see cref="Enqueue(Annotation)"/> returns empty. Safe to
    /// call concurrently with <see cref="Enqueue(Annotation)"/> and other <see cref="Drain"/> calls.
    /// </summary>
    public IReadOnlyList<Annotation> Drain() => throw new NotImplementedException();

    /// <summary>
    /// The long-poll signal the annotation API waits on. Completes <c>true</c> as soon as an annotation is
    /// available (including one enqueued while the wait is outstanding), or <c>false</c> once
    /// <paramref name="timeout"/> elapses with the buffer still empty.
    /// </summary>
    /// <param name="timeout">How long to wait before completing <c>false</c> on an empty store.</param>
    /// <param name="cancellationToken">Cancels the outstanding wait.</param>
    public Task<bool> WaitForPendingAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
