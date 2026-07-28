using Charter.Core;

namespace Charter.Server;

/// <summary>
/// What the server-less review-log read produced: the annotations to report, whether there was a log to read
/// at all, and why a read could not complete.
/// </summary>
/// <param name="Annotations">The comments this machine has not already been handed, ready for the envelope.</param>
/// <param name="HasLog">Whether any per-author log exists beside the plan — false means "nothing to read".</param>
/// <param name="DrainError">A human-readable reason when a log could not be read; null on a clean read.</param>
public sealed record ReviewLogDrainResult(
    IReadOnlyList<Annotation> Annotations, bool HasLog, string? DrainError)
{
    /// <summary>No review directory, or no logs in it — not an error, just nothing to read.</summary>
    public static ReviewLogDrainResult NoLog { get; } = new(Array.Empty<Annotation>(), false, null);
}

/// <summary>
/// The server-less read path for <c>charter poll</c> (§5 of
/// <c>docs/plans/03-git-mediated-team-review.md</c>) — the step that actually closes the loop.
/// </summary>
/// <remarks>
/// <para>
/// <c>poll</c> is otherwise a CLIENT of a running loopback server: it resolves a session from the registry,
/// probes it, and exits 3 when none is live. That makes the payoff impossible — A's agent reading B's
/// committed comments requires A to be running <c>charter review</c>, and A is not: A is EXECUTING. This is
/// the analogue of the fallback <c>charter resolve</c> already has, reading the committed logs directly.
/// </para>
/// <para>
/// <b>Consumption is machine-local and never a log record.</b> A comment is reported once per machine; the
/// same comment on a teammate's machine is unaffected. See <see cref="ReviewLogLedger"/>.
/// </para>
/// <para>
/// <b>Nothing is silently dropped.</b> A withdrawn comment is reported with a <c>retracted</c> status and an
/// explicit withdrawal body; a comment whose block has changed is reported with <c>sourceLine: null</c> and
/// <c>anchorStatus: "orphaned"</c> — the agent needs to know it is looking at a note whose block moved, not to
/// be handed silence.
/// </para>
/// </remarks>
public static class ReviewLogDrain
{
    /// <summary>
    /// What a withdrawn comment's body reads as. The design renders a retract as this exact phrase (§4.2), and
    /// an empty body would read to an agent as "no feedback" rather than "the author took this back".
    /// </summary>
    public const string WithdrawnBody = "(comment withdrawn by author)";

    /// <summary>
    /// Read every per-author log beside <paramref name="planPath"/>, fold it, and report the comments this
    /// machine's agent has not already been handed — recording them as delivered in
    /// <paramref name="consumedDirectory"/>.
    /// </summary>
    public static ReviewLogDrainResult Drain(string planPath, string consumedDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(planPath);
        ArgumentException.ThrowIfNullOrEmpty(consumedDirectory);

        var directory = ReviewLogPaths.DirectoryForPlan(planPath);
        if (ReviewLogPaths.EnumerateLogs(directory).Count == 0)
        {
            return ReviewLogDrainResult.NoLog;
        }

        var read = ReviewLogStore.Read(directory);
        var ledger = ReviewLogLedger.Load(consumedDirectory, planPath);

        var fresh = read.State.Comments
            .Where(comment => !ledger.HasConsumedAll(RecordIdsOf(comment)))
            .ToList();

        // Resolve every anchor against the plan AS IT IS NOW, through the one kernel every handoff path uses.
        // Exact block-id match or orphaned (§4.3) — an unreadable plan orphans everything, which is the honest
        // answer when no line can be verified.
        var annotations = AnchorResolution.Resolve(fresh.Select(ToAnnotation).ToList(), ReadPlanOrEmpty(planPath));

        if (fresh.Count > 0)
        {
            ledger.MarkConsumed(fresh.SelectMany(RecordIdsOf));
            ledger.Save();
        }

        return new ReviewLogDrainResult(annotations, HasLog: true, DrainError: DescribeUnreadable(read.Unreadable));
    }

    /// <summary>
    /// One folded comment as the wire shape <c>poll</c> already emits, plus the review attribution an agent
    /// needs in order to tell a live objection from a withdrawal.
    /// </summary>
    private static Annotation ToAnnotation(ReviewComment comment) => new(
        Id: comment.Id,
        Kind: AnnotationApi.ParseKind(comment.Anchor.Kind),
        AnchorId: comment.Anchor.BlockId,
        Note: comment.Body ?? WithdrawnBody,
        SourceLine: null,
        Quote: comment.Anchor.Quote,
        Review: new ReviewAttribution(
            AuthorName: string.IsNullOrEmpty(comment.Author.Name) ? comment.Author.Email : comment.Author.Name,
            AuthorEmail: comment.Author.Email,
            Actor: comment.Actor,
            Status: ReviewStatusTokens.For(comment.Status),
            Ts: comment.Record.Ts));

    /// <summary>
    /// Every record id that contributes to what a comment currently SAYS. A later edit, resolve, retract or
    /// reply mints a new id, so the comment becomes deliverable again — which is right: the agent is being
    /// told something new about it, not the same thing twice.
    /// </summary>
    private static IEnumerable<string> RecordIdsOf(ReviewComment comment)
    {
        yield return comment.Id;

        foreach (var head in comment.StateHeads)
        {
            yield return head.Id;
        }

        foreach (var settlement in comment.ResolutionRecords)
        {
            yield return settlement.Id;
        }

        if (comment.RetractRecord is not null)
        {
            yield return comment.RetractRecord.Id;
        }

        foreach (var reply in comment.Replies)
        {
            yield return reply.Id;
            if (reply.RetractRecord is not null)
            {
                yield return reply.RetractRecord.Id;
            }
        }
    }

    /// <summary>
    /// A log that could not be read means the review state is UNKNOWN, not empty — the caller turns this into
    /// the distinct drain-failed exit rather than reporting "nothing queued".
    /// </summary>
    private static string? DescribeUnreadable(IReadOnlyList<string> unreadable)
        => unreadable.Count == 0
            ? null
            : $"could not read {unreadable.Count} review log(s): {string.Join("; ", unreadable)}";

    private static string ReadPlanOrEmpty(string planPath)
    {
        try
        {
            return File.ReadAllText(planPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
