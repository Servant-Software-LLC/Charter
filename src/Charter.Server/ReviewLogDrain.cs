using Charter.Core;

namespace Charter.Server;

/// <summary>
/// What the server-less review-log read produced: the annotations to report, whether there was a log to read
/// at all, and why a read could not complete.
/// </summary>
/// <param name="Annotations">The comments this machine has not already been handed, ready for the envelope.</param>
/// <param name="HasLog">
/// Whether there is a review beside the plan to report on at all. True when the log directory was READ —
/// whether it held comments or nothing — and true when it could not be read but this machine has already been
/// handed records from it. False only for the plan nobody has commented on, which leaves the caller on its
/// unchanged "no session and no readable log" path.
/// </param>
/// <param name="DrainError">A human-readable reason when a log could not be read; null on a clean read.</param>
/// <param name="Delivered">
/// Every record id behind <paramref name="Annotations"/>, to be handed to
/// <see cref="ReviewLogDrain.ConfirmDelivered"/> <b>after</b> the envelope has actually been written.
/// <para>
/// <b>The split is the at-least-once guarantee.</b> Marking these consumed inside the read would make the drain
/// at-MOST-once: a crash, a broken pipe or a killed process between the ledger write and the envelope write
/// would lose a committed objection permanently on this machine, with `poll` reporting a clean empty from then
/// on. Re-delivering a comment the agent has already seen is recoverable; failing to deliver one it has not is
/// not — the same asymmetry <see cref="ReviewLogLedger"/> resolves the same way, and the same shape as the live
/// session's requeue-on-write-failure.
/// </para>
/// </param>
public sealed record ReviewLogDrainResult(
    IReadOnlyList<Annotation> Annotations, bool HasLog, string? DrainError, IReadOnlyList<string> Delivered)
{
    /// <summary>
    /// Set when the consumption ledger was discarded because it belonged to a DIFFERENT checkout at this path
    /// (#81). Not an error — the drain succeeded, and succeeded MORE completely than it would have. Additive
    /// and init-only so existing construction sites are untouched.
    /// </summary>
    public string? LedgerReset { get; init; }

    /// <summary>
    /// Nothing to read, and nothing this machine is owed: no review directory beside the plan, and no record
    /// ever handed over from one. <c>.review/</c> is created lazily on the first append
    /// (<c>docs/plans/03-git-mediated-team-review.md</c> §5.0), so this is the ORDINARY state of a plan nobody
    /// has commented on — not an error, and not a claim about what a reviewer said.
    /// </summary>
    public static ReviewLogDrainResult NoLog { get; } =
        new(Array.Empty<Annotation>(), false, null, Array.Empty<string>());

    /// <summary>
    /// The review directory was READ and holds no logs: nobody has commented. That is a positive finding, so
    /// it is reported as a log — the caller's clean-empty verdict is then about a queue it actually looked
    /// into, which is the only thing that verdict was ever entitled to claim.
    /// </summary>
    public static ReviewLogDrainResult EmptyLog { get; } =
        new(Array.Empty<Annotation>(), true, null, Array.Empty<string>());
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
/// be handed silence. And a review log that could not be READ is reported as a failed drain rather than an
/// empty one, so the queue state comes back UNKNOWN instead of as "the reviewer said nothing" (Charter #221).
/// </para>
/// <para>
/// <b>And nothing is withheld for staleness — deliberately (Charter #74, §4.3.1).</b> The #67 sidecar
/// quarantine does NOT cross over to the committed log. Its remedy (copy the queue aside, rewrite the live
/// file) does not exist for a git-tracked file Charter may never mutate, and its evidence ("not one anchor
/// resolves") has a high benign base rate here: a fresh clone or a second machine has an empty consumption
/// ledger, so a mature plan's whole review history is delivered in one poll and nearly all of it is
/// legitimately orphaned. A rule that fired there would silence a committed objection with no local remedy —
/// there is no <c>--keep-annotations</c> for a record that belongs to someone else's machine. Instead the
/// EVIDENCE travels: every comment carries <c>base</c> and <c>baseStatus</c> (<see cref="ReviewBaseStatus"/>)
/// so the reader can see which revision it was written against.
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
    /// machine's agent has not already been handed, per the ledger in <paramref name="consumedDirectory"/>.
    /// </summary>
    /// <remarks>
    /// <b>This does not record anything as delivered.</b> The caller must call
    /// <see cref="ConfirmDelivered"/> with <see cref="ReviewLogDrainResult.Delivered"/> once the envelope has
    /// been written — see that property for why the two halves are separate.
    /// </remarks>
    public static ReviewLogDrainResult Drain(string planPath, string consumedDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(planPath);
        ArgumentException.ThrowIfNullOrEmpty(consumedDirectory);

        var directory = ReviewLogPaths.DirectoryForPlan(planPath);

        // The log directory AS IT IS NOW, read once — and read for its OUTCOME, not merely for its file names.
        // An enumeration answers the same empty list to "there is nothing in it" and to "there is no it", and
        // the two are different answers to the question this verb is actually asked (Charter #221).
        var read = ReviewLogStore.Read(directory);
        var ledger = ReviewLogLedger.Load(consumedDirectory, planPath);

        if (read.IsUnknown)
        {
            // Nothing was learned — but "the directory is not there" arrives from two directions, and only one
            // of them is a failure. `.review/` is created lazily on the first append (§5.0), so the plan nobody
            // has commented on has no directory beside it: the ordinary, silent state of a solo review, which
            // must not become a warning. What separates them is what this machine already knows. The ledger
            // records every record it has been HANDED from that directory, so a non-empty one is proof the
            // directory was here; its absence now is a failed READ — a branch switch, a checkout replacing the
            // tree, a pull in flight — and telling the agent that was handed those very comments that the
            // reviewer said nothing is precisely the silent loss this exists to stop.
            return ledger.Count == 0
                ? ReviewLogDrainResult.NoLog
                : new ReviewLogDrainResult(
                    Array.Empty<Annotation>(),
                    HasLog: true,
                    DrainError: DescribeVanished(directory, ledger.Count),
                    Delivered: Array.Empty<string>());
        }

        if (read.IsEmpty)
        {
            // The directory was there and holds no logs. A finding, and the one exit 2 was always right about.
            return ReviewLogDrainResult.EmptyLog with { LedgerReset = ledger.ResetReason };
        }

        var fresh = read.State.Comments
            .Where(comment => !ledger.HasConsumedAll(RecordIdsOf(comment)))
            .ToList();

        // The plan AS IT IS NOW, read once: null when it cannot be read at all. Both verdicts hang off it —
        // the anchor (exact block-id match or orphaned, §4.3) and the base (§4.3.1) — and an unreadable plan
        // orphans everything and answers `unknown` for every base, which is the honest answer when nothing can
        // be verified. The same rule the read above applies to the log DIRECTORY: what could not be read is
        // reported as unknown, never as a finding.
        var markdown = TryReadPlan(planPath);
        var revision = ReviewBaseStatus.ForPlan(markdown);

        var annotations = AnchorResolution.Resolve(
            fresh.Select(comment => ToAnnotation(comment, revision)).ToList(), markdown ?? string.Empty);

        return new ReviewLogDrainResult(
            annotations,
            HasLog: true,
            DrainError: DescribeUnreadable(read.Unreadable),
            Delivered: fresh.SelectMany(RecordIdsOf).ToList())
        {
            LedgerReset = ledger.ResetReason,
        };
    }

    /// <summary>
    /// Record <paramref name="delivered"/> as handed to this machine's agent, so the next read does not report
    /// them again. Called <b>only after</b> the envelope carrying them has been written.
    /// </summary>
    /// <remarks>
    /// Throws on a ledger this machine cannot write. The caller must treat that as "these comments were
    /// delivered and will be delivered again", never as a failed drain: the records themselves are untouched,
    /// and a repeat is the recoverable error.
    /// </remarks>
    public static void ConfirmDelivered(
        string planPath, string consumedDirectory, IReadOnlyList<string> delivered)
    {
        ArgumentException.ThrowIfNullOrEmpty(planPath);
        ArgumentException.ThrowIfNullOrEmpty(consumedDirectory);
        ArgumentNullException.ThrowIfNull(delivered);

        if (delivered.Count == 0)
        {
            return;
        }

        // Re-read rather than holding the instance from Drain: another `charter poll` for the same plan may
        // have committed in between, and the ledger is a set — a fresh load merges instead of clobbering.
        var ledger = ReviewLogLedger.Load(consumedDirectory, planPath);
        ledger.MarkConsumed(delivered);
        ledger.Save();
    }

    /// <summary>
    /// One folded comment as the wire shape <c>poll</c> already emits, plus the review attribution an agent
    /// needs in order to tell a live objection from a withdrawal, and the revision it was written against so a
    /// comment authored on a document that is no longer at this path is not indistinguishable from fresh
    /// feedback (§4.3.1).
    /// </summary>
    private static Annotation ToAnnotation(ReviewComment comment, ReviewBaseStatus revision) => new(
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
            Ts: comment.Record.Ts),
        Base: comment.Anchor.Base,
        BaseStatus: revision.Of(comment.Anchor.Base));

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

    /// <summary>
    /// The reason a review directory this machine has been reading, and which is now not there, is a failed
    /// read rather than an empty queue. The companion of <see cref="DescribeUnreadable"/> one level up: that
    /// one names the log files that could not be read, this one the directory that could not be looked into —
    /// and it carries the evidence, because "it is gone" alone would not distinguish this from a plan nobody
    /// has commented on.
    /// </summary>
    private static string DescribeVanished(string directory, int consumedCount)
        => $"could not read the review log at {directory}: it is not there, but this machine has already been "
            + $"handed {consumedCount} record(s) from it";

    /// <summary>
    /// The plan's current text, or <see langword="null"/> when it cannot be read. The null is load-bearing:
    /// an unreadable plan must answer <c>baseStatus: "unknown"</c> rather than <c>"different"</c>, because
    /// "the plan is not what you commented on" is a claim, and Charter has not read the plan.
    /// </summary>
    private static string? TryReadPlan(string planPath)
    {
        try
        {
            return File.ReadAllText(planPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
