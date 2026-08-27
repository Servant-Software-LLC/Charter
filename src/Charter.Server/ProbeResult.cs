namespace Charter.Server;

/// <summary>
/// What a probe LEARNED — which is not the same as what it found (Charter #217).
/// </summary>
/// <remarks>
/// The distinction that matters is between the two non-live outcomes. <see cref="Absent"/> is a POSITIVE
/// finding: something answered, or nothing was listening, and either way the probe knows. <see cref="Unknown"/>
/// is the absence of a finding. Collapsing them is what let a timeout against a live server be reported as
/// "no session" and prune that session's descriptor.
/// </remarks>
public enum ProbeOutcome
{
    /// <summary>A session answered and its identity matched. The only outcome carrying a session.</summary>
    Live,

    /// <summary>
    /// Positive evidence that no drainable session is at this address: nothing is listening (connection
    /// refused), the key was rejected, the body was not a session descriptor, or a live server confirmed it
    /// serves a DIFFERENT source than the descriptor claimed. <b>This is the only outcome on which pruning a
    /// descriptor is sound.</b>
    /// </summary>
    Absent,

    /// <summary>
    /// The probe could not complete — it timed out, or was cancelled. <b>Not evidence of absence.</b> A caller
    /// must not report "no session", must not prune a descriptor, and must not let a pipeline proceed as
    /// though it had looked and found nothing.
    /// </summary>
    Unknown,
}

/// <summary>One probe's outcome, and the session when there is one.</summary>
/// <remarks>
/// <see cref="Session"/> is non-null <b>iff</b> <see cref="Outcome"/> is <see cref="ProbeOutcome.Live"/>; the
/// factories below are the only way to build one, so that pairing cannot be got wrong at a call site.
/// </remarks>
public readonly record struct ProbeResult(ProbeOutcome Outcome, PollSession? Session)
{
    /// <summary>Positive evidence that no drainable session is here.</summary>
    public static readonly ProbeResult Absent = new(ProbeOutcome.Absent, null);

    /// <summary>The probe could not complete. Nothing was learned.</summary>
    public static readonly ProbeResult Unknown = new(ProbeOutcome.Unknown, null);

    /// <summary>A live session answered.</summary>
    public static ProbeResult Live(PollSession session) => new(ProbeOutcome.Live, session);

    /// <summary>True when a session answered — the only case that carries one.</summary>
    public bool IsLive => Outcome == ProbeOutcome.Live;

    /// <summary>
    /// True only when the probe positively established that nothing drainable is here. <b>Read this rather
    /// than <c>!IsLive</c></b> before pruning a descriptor or reporting "no session": the difference between
    /// the two spellings is the whole of Charter #217.
    /// </summary>
    public bool IsAbsent => Outcome == ProbeOutcome.Absent;
}
