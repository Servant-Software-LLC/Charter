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
/// All buffer mutation happens under a single <see cref="_gate"/> lock, so <see cref="Enqueue(Answer)"/>,
/// <see cref="Peek"/>, and <see cref="CommitFront(int)"/> can never tear or interleave partially.
/// <see cref="Enqueue(Answer)"/> only ever APPENDS, so the answers a caller peeked are always the current
/// FRONT of the buffer: <see cref="CommitFront(int)"/> removes exactly that peeked prefix, and any answer that
/// arrived after the peek (appended to the back) survives for the next cycle.
/// </remarks>
public sealed class AnswerStore
{
    private readonly object _gate = new();
    private readonly List<Answer> _pending = new();

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
            return removed;
        }
    }
}
