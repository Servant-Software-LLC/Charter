namespace Charter.Server;

/// <summary>
/// The result of a <see cref="ReviewClient"/> drain/peek: the drained <see cref="Items"/> on success, or a
/// non-null <see cref="Error"/> when the drain could not complete (connection refused, timeout, non-200, or an
/// unparseable body). Distinguishing "drained nothing" (<c>Items</c> empty, <c>Error</c> null) from "drain
/// FAILED" (<c>Error</c> set) is load-bearing: a swallowed transport failure looks identical to an empty
/// session, so an agent would hand off on a false "nothing queued" (§DA-weak-4). The caller surfaces
/// <see cref="Error"/> in its envelope and maps a failed drain to a distinct exit code.
/// </summary>
/// <typeparam name="T">The drained item type (<c>Annotation</c> or <c>Answer</c>).</typeparam>
public sealed record DrainOutcome<T>(IReadOnlyList<T> Items, string? Error)
{
    /// <summary>
    /// The batch sequence to acknowledge once the items have been safely emitted (#117), or 0 when the
    /// drain carried none (an empty batch, a failure, or a server predating the ack). Additive and
    /// defaulted, so an outcome constructed without it behaves exactly as before.
    /// </summary>
    public long Sequence { get; init; }

    /// <summary>True when the drain could not complete — the queue state is unknown, not empty.</summary>
    public bool Failed => Error is not null;

    /// <summary>A successful drain carrying <paramref name="items"/> (possibly empty — a genuinely empty queue).</summary>
    public static DrainOutcome<T> Success(IReadOnlyList<T> items) => new(items, null);

    /// <summary>A failed drain: no items, and <paramref name="error"/> describing why the state is unknown.</summary>
    public static DrainOutcome<T> Failure(string error) => new(Array.Empty<T>(), error);
}
