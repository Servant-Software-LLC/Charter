namespace Charter.Cli;

/// <summary>
/// The exit codes <c>charter headless</c> uses so an unattended harness can branch deterministically on the
/// outcome — never by inspecting the output files and guessing. (This repo has already been bitten once by an
/// agent branching on an empty array instead of an exit code; the review-drain verbs got
/// <see cref="ReviewExitCodes"/> for the same reason.)
/// </summary>
/// <remarks>
/// Deliberately a SEPARATE contract from <see cref="ReviewExitCodes"/>: those are the review-DRAIN codes
/// (<c>poll</c>/<c>resolve</c>), whose 2 means "a queue was found and it was empty". Reusing that class here
/// would imply the two vocabularies are one, and a caller would eventually read <c>headless</c>'s 2 as
/// "nothing queued". Exit <c>1</c> is absent from both: it is the generic verb-error code the top-level
/// <c>RunVerb</c> wrapper maps any unexpected exception to.
/// </remarks>
internal static class HeadlessExitCodes
{
    /// <summary>
    /// The artifact and the forensic record are on disk and nothing is outstanding — the crew may proceed.
    /// </summary>
    public const int Complete = 0;

    /// <summary>
    /// The artifact and the forensic record are on disk, AND the plan carries something a human must decide
    /// or fix (see <c>Charter.Core.HeadlessRecord.NeedsHuman</c> for the exact three conditions). This is an
    /// ESCALATION, not a failure: everything was persisted, and the record's <c>needsHuman</c> flag says the
    /// same thing this code does, so a harness reading the file and one reading <c>$?</c> cannot disagree.
    /// </summary>
    public const int NeedsHuman = 2;
}
