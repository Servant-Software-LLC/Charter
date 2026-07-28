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

/// <summary>
/// The BROWSER-facing projection of a <see cref="StaleAnnotationQueue"/>, carried on <c>GET /api/review</c> so
/// the in-page panel can tell the reviewer their earlier notes were set aside (Charter #75 item 2).
/// </summary>
/// <remarks>
/// <para>
/// The quarantine notice used to exist only on <b>stderr</b>, and <c>charter review</c> is frequently launched
/// BY an agent — so the stream that carries "your notes are safe, here is how to get them back" is often one no
/// human ever reads, while the panel, which is where the reviewer actually is, said nothing at all. That made
/// "surface it, never destroy it" true in mechanism and weak in practice.
/// </para>
/// <para>
/// It deliberately carries the quarantine file's <b>name</b>, never <see cref="StaleAnnotationQueue.PreservedAt"/>
/// — a local absolute path has no business in page DOM, and the actionable instruction is the command
/// (<c>charter review &lt;plan&gt; --keep-annotations</c>) rather than the path. The full path stays on stderr,
/// where it is a local-console fact.
/// </para>
/// </remarks>
/// <param name="Count">How many annotations were withheld.</param>
/// <param name="FileName">The set-aside queue's file name (no directory), so the reviewer can find it if they want it.</param>
/// <param name="DurabilityDisabled">Whether this session is running without the sidecar because the copy failed.</param>
public sealed record StaleQueueNotice(int Count, string FileName, bool DurabilityDisabled)
{
    /// <summary>The notice for <paramref name="stale"/>, or <see langword="null"/> when nothing was set aside.</summary>
    public static StaleQueueNotice? For(StaleAnnotationQueue? stale)
        => stale is null
            ? null
            : new StaleQueueNotice(
                stale.Count, Path.GetFileName(stale.PreservedAt) ?? string.Empty, stale.DurabilityDisabled);
}
