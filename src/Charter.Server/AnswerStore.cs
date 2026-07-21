namespace Charter.Server;

/// <summary>
/// The answer session store: a thread-safe, single-writer / locked buffer of reviewer <see cref="Answer"/>s
/// awaiting the author/agent handoff. It mirrors <see cref="AnnotationStore"/>'s serialize-all-access design
/// so a concurrent submit and drain lose no answer and duplicate none — but where the annotation store also
/// carries a long-poll wake signal for <c>/api/poll</c>, the answer drain is a plain <c>GET /api/answers</c>,
/// so no signal is needed here.
/// </summary>
/// <remarks>
/// All buffer mutation happens under a single <see cref="_gate"/> lock, so <see cref="Enqueue(Answer)"/> and
/// <see cref="Drain"/> can never tear or interleave partially: <see cref="Drain"/> removes every pending
/// answer in one atomic step, and a submit that races it is serialized behind the same lock.
/// </remarks>
public sealed class AnswerStore
{
    private readonly object _gate = new();
    private readonly List<Answer> _pending = new();

    /// <summary>
    /// Add an answer to the pending buffer. Safe to call concurrently with <see cref="Drain"/> and other
    /// <see cref="Enqueue(Answer)"/> calls; no enqueued answer may be lost.
    /// </summary>
    public void Enqueue(Answer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        lock (_gate)
        {
            _pending.Add(answer);
        }
    }

    /// <summary>
    /// Re-add <paramref name="answers"/> that were drained but never delivered — the drain write failed
    /// (client disconnected) — to the FRONT of the pending buffer under the same lock, so a subsequent
    /// <see cref="Drain"/> re-fetches them. This is the at-least-once guarantee: a drained batch that could
    /// not be written is not lost. Front insertion keeps the un-delivered answers ahead of any that arrived
    /// after the failed drain, preserving submit order.
    /// </summary>
    public void Requeue(IReadOnlyList<Answer> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        if (answers.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            _pending.InsertRange(0, answers);
        }
    }

    /// <summary>
    /// Atomically return the currently-pending answers and clear the buffer, so a subsequent
    /// <see cref="Drain"/> that observes no further <see cref="Enqueue(Answer)"/> returns empty. Safe to call
    /// concurrently with <see cref="Enqueue(Answer)"/> and other <see cref="Drain"/> calls.
    /// </summary>
    public IReadOnlyList<Answer> Drain()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return Array.Empty<Answer>();
            }

            var drained = _pending.ToArray();
            _pending.Clear();
            return drained;
        }
    }
}
