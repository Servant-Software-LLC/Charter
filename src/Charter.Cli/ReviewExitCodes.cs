namespace Charter.Cli;

/// <summary>
/// The exit codes the review-drain verbs (<c>charter poll</c>, <c>charter resolve</c>) share so an agent can
/// branch on the outcome deterministically. Exit <c>1</c> is deliberately absent — it is the generic
/// verb-error code the top-level <c>RunVerb</c> wrapper maps any unexpected exception to.
/// </summary>
internal static class ReviewExitCodes
{
    /// <summary>At least one annotation/answer was drained (and, under apply, written inline successfully).</summary>
    public const int Drained = 0;

    /// <summary>A live session (or a readable sidecar) was found, but nothing was queued — a clean no-op.</summary>
    public const int CleanEmpty = 2;

    /// <summary>No live review session (and, for resolve, no sidecar) was found for the plan.</summary>
    public const int NoSession = 3;

    /// <summary>
    /// A drain could not complete (transport/parse failure): the queue state is UNKNOWN, not empty. Distinct
    /// from <see cref="CleanEmpty"/> so a caller never proceeds on a false "nothing queued".
    /// </summary>
    public const int DrainFailed = 4;

    /// <summary>
    /// The inline apply failed (duplicate question ids, a concurrent external edit, or an I/O error). The
    /// queued answers are PRESERVED (never committed), so a corrected re-run recovers them — no data loss.
    /// </summary>
    public const int ApplyFailed = 5;
}
