namespace Charter.Server;

/// <summary>
/// A queued set of annotations that <see cref="ReviewSidecar.IsStale"/> judged to belong to a <b>different
/// document</b> than the plan now at this path (Charter #67), and which the server therefore did NOT rehydrate.
/// Reported so the fact is surfaced to the reviewer rather than resolved silently in either direction.
/// </summary>
/// <param name="Count">How many annotations were withheld.</param>
/// <param name="PreservedAt">
/// Where they are, so the notice can name a file the reviewer can open or delete. This is the quarantine copy
/// when one could be made, and otherwise the live sidecar itself — in which case
/// <paramref name="DurabilityDisabled"/> is set, because the only safe way to leave notes Charter could not copy
/// is to stop writing over them.
/// </param>
/// <param name="DurabilityDisabled">
/// Whether this session is running with the sidecar unbound because the queue could not be preserved. New
/// annotations and answers are still served and drained; they just are not persisted across a restart.
/// </param>
public sealed record StaleAnnotationQueue(int Count, string PreservedAt, bool DurabilityDisabled);
