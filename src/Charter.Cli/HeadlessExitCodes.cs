namespace Charter.Cli;

/// <summary>
/// The UNATTENDED-PATH exit codes, so a harness can branch deterministically on the outcome — never by
/// inspecting the output files and guessing. (This repo has already been bitten once by an agent branching on
/// an empty array instead of an exit code; the review-drain verbs got <see cref="ReviewExitCodes"/> for the
/// same reason.)
/// </summary>
/// <remarks>
/// <para>
/// Shared by <c>charter headless</c> and by <c>charter handoff --fail-if-needs-human</c> (Charter #172).
/// Sharing is the point rather than an economy: both verbs mean exactly the same thing by a 2 — <b>the output
/// is on disk and something needs a human</b> — and so do Guardrails' two 2s
/// (<c>BreakdownCommand.NotCleanExitCode</c>, commented "a 2 means READ THE FOLDER", and
/// <c>ExitCodes.TaskFailed</c>, "the run completed but at least one task needs a human"). A harness that
/// treats every 2 in this pipeline alike is doing the right thing.
/// </para>
/// <para>
/// Deliberately still a SEPARATE contract from <see cref="ReviewExitCodes"/>: those are the review-DRAIN codes
/// (<c>poll</c>/<c>resolve</c>), whose 2 means "a queue was found and it was empty" — the one 2 in the whole
/// pipeline that does NOT mean go and read the output. Reusing that class here would imply the two
/// vocabularies are one. Exit <c>1</c> is absent from both: it is the generic verb-error code the top-level
/// <c>RunVerb</c> wrapper maps any unexpected exception to.
/// </para>
/// </remarks>
internal static class HeadlessExitCodes
{
    /// <summary>
    /// The outputs are on disk and nothing is outstanding — the crew may proceed. For <c>headless</c> that is
    /// the artifact plus the forensic record; for <c>handoff</c>, the flattened CommonMark.
    /// </summary>
    public const int Complete = 0;

    /// <summary>
    /// The outputs are on disk, AND the plan carries something a human must decide or fix. This is an
    /// ESCALATION, not a failure: everything was persisted BEFORE the code was decided.
    /// <para>
    /// The two verbs judge by deliberately different predicates — <c>headless</c> by
    /// <c>Charter.Core.HeadlessRecord.NeedsHuman</c> (three conditions, a pure function of the plan text),
    /// <c>handoff --fail-if-needs-human</c> by <c>Charter.Core.HandoffGate</c> (stricter, and evaluated after
    /// <c>--answers</c> is merged). What they share is this post-condition, which is the only thing a caller
    /// branching on <c>$?</c> can act on.
    /// </para>
    /// </summary>
    public const int NeedsHuman = 2;
}
