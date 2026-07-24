namespace Charter.Server;

/// <summary>
/// The on-disk record a running <c>charter review</c> server writes so <c>charter poll</c> can discover it:
/// the loopback address, the per-session capability key, and the source plan under review. It is a discovery
/// HINT, never a source of truth — <c>poll</c> always re-proves liveness against the live server before
/// trusting it, so a stale or recycled descriptor degrades to "no session" rather than misdirecting a drain.
/// </summary>
/// <param name="Schema">Descriptor schema version (<see cref="CurrentSchema"/>).</param>
/// <param name="Address">The keyless loopback base address, e.g. <c>http://127.0.0.1:PORT/</c>.</param>
/// <param name="Key">The session's capability key (the secret <c>poll</c> presents; never printed to stdout).</param>
/// <param name="SourcePath">The canonical (<see cref="Path.GetFullPath(string)"/>) path of the plan under review.</param>
/// <param name="SourceFile">The plan's file name, for human-readable listings.</param>
/// <param name="Pid">The review process id — informational only; NOT used for liveness (pid reuse).</param>
/// <param name="CreatedAt">When the descriptor was written (UTC), informational.</param>
public sealed record SessionDescriptor(
    int Schema,
    string Address,
    string Key,
    string SourcePath,
    string SourceFile,
    int Pid,
    DateTimeOffset CreatedAt)
{
    /// <summary>The schema version this build writes.</summary>
    public const int CurrentSchema = 1;

    /// <summary>
    /// Build the descriptor for a live <paramref name="session"/> served at <paramref name="address"/>: the
    /// keyless base address, the session's key and canonical source path, this process id, and now (UTC).
    /// </summary>
    public static SessionDescriptor ForSession(ReviewSession session, Uri address)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(address);

        return new SessionDescriptor(
            Schema: CurrentSchema,
            Address: address.GetLeftPart(UriPartial.Authority) + "/",
            Key: session.Key.Value,
            SourcePath: session.SourcePath,
            SourceFile: Path.GetFileName(session.SourcePath),
            Pid: Environment.ProcessId,
            CreatedAt: DateTimeOffset.UtcNow);
    }
}
