using Charter.Core;

namespace Charter.Server;

/// <summary>
/// What a review-log read LEARNED — which is not the same as what it found (Charter #221).
/// </summary>
/// <remarks>
/// The distinction that matters is between the two outcomes that carry no comments. <see cref="Empty"/> is a
/// POSITIVE finding: the directory was there and holds no logs, so nobody has commented. <see cref="Unknown"/>
/// is the absence of a finding. Collapsing them is what let a momentarily-absent <c>.review/</c> be reported to
/// an agent as <i>"the reviewer said nothing"</i> — the shape <see cref="ProbeOutcome"/> already carries one
/// layer up, for #217.
/// </remarks>
public enum ReviewLogOutcome
{
    /// <summary>Logs were found and folded. The only outcome whose comments are the whole answer.</summary>
    Present,

    /// <summary>
    /// The review directory was read and holds no logs. Nobody has commented — a normal state, not a failure,
    /// and the usual state of a solo review, since <c>.review/</c> is created lazily on the first append. This
    /// path stays silent and cheap: it is never a warning.
    /// </summary>
    Empty,

    /// <summary>
    /// The read could not complete — the directory was not there, and a bounded retry did not change that.
    /// <b>Not evidence that nobody commented.</b> A caller must not report "nothing queued", must not answer
    /// "no such comment", and must not replace a populated panel with an empty one.
    /// </summary>
    Unknown,
}

/// <summary>
/// The result of reading and folding a plan's review logs: the folded state, plus the logs that could not be
/// read at all. Unreadable files are reported rather than absorbed — a caller that showed a partial fold as if
/// it were complete would be exactly the silent loss the design (§3) exists to prevent.
/// </summary>
/// <param name="State">The folded review state across every log that was readable.</param>
/// <param name="Unreadable">The file names of logs that could not be read, with the reason.</param>
public sealed record ReviewLogRead(ReviewLogState State, IReadOnlyList<string> Unreadable)
{
    /// <summary>
    /// The review directory was read and holds no logs, so nobody has commented. Not an error — and not the
    /// answer for a directory that was never there to read, which is <see cref="Unknown"/>.
    /// </summary>
    public static ReviewLogRead Empty { get; } = new(NoComments(), Array.Empty<string>())
    {
        Outcome = ReviewLogOutcome.Empty,
    };

    /// <summary>
    /// The read could not complete. Carries no comments for the same reason it carries no finding: nothing was
    /// learned. <b>Not evidence that nobody commented.</b>
    /// </summary>
    public static ReviewLogRead Unknown { get; } = new(NoComments(), Array.Empty<string>())
    {
        Outcome = ReviewLogOutcome.Unknown,
    };

    /// <summary>
    /// <see cref="Unknown"/>, still NAMING the logs that could not be read and why. Reached when every log the
    /// directory holds was unreadable (Charter #221): nothing was learned, exactly as for an absent directory,
    /// so it must not read as "nobody commented" — but unlike an absent directory there is something to
    /// report, and a permission error that never clears has to stay as visible as it was before.
    /// </summary>
    /// <param name="unreadable">The logs that could not be read, each with its reason.</param>
    public static ReviewLogRead UnknownBecauseUnreadable(IReadOnlyList<string> unreadable) =>
        new(NoComments(), unreadable) { Outcome = ReviewLogOutcome.Unknown };

    /// <summary>
    /// What this read learned. See <see cref="ReviewLogOutcome"/>.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="ReviewLogOutcome.Present"/>, the only outcome the constructor can honestly carry:
    /// reaching it means a fold is already in hand. The two outcomes that carry no comments are reached through
    /// <see cref="Empty"/> and <see cref="Unknown"/> — the pairing ProbeResult's factories exist to make
    /// unmistakable (#217) — so neither can be built by accident from an empty fold.
    /// </remarks>
    public ReviewLogOutcome Outcome { get; init; } = ReviewLogOutcome.Present;

    /// <summary>True when logs were found and folded — the only case whose comments are the whole answer.</summary>
    public bool IsPresent => Outcome == ReviewLogOutcome.Present;

    /// <summary>
    /// True only when the directory was read and held no logs. <b>Read this rather than <c>!IsPresent</c></b>
    /// before reporting that nobody has commented: the negation is true of <see cref="ReviewLogOutcome.Unknown"/>
    /// as well, and the difference between the two spellings is the whole of Charter #221 — the same rule
    /// <see cref="ProbeResult.IsAbsent"/> exists to enforce for #217.
    /// </summary>
    public bool IsEmpty => Outcome == ReviewLogOutcome.Empty;

    /// <summary>
    /// True when the read could not complete. Every consumer branches on this POSITIVELY — <c>charter poll</c>
    /// exits 4, the panel declines the view, and <c>FindComment</c> refuses to answer "not found".
    /// </summary>
    public bool IsUnknown => Outcome == ReviewLogOutcome.Unknown;

    /// <summary>
    /// True when the read could not complete because logs WERE there and none could be read — as opposed to a
    /// directory that was not there at all. Both are <see cref="IsUnknown"/>: nothing was learned either way,
    /// and a consumer that only needs "did this read learn anything?" should keep asking that.
    /// </summary>
    /// <remarks>
    /// It exists for the one consumer that must tell them apart. <c>charter poll</c> treats an ABSENT directory
    /// as the ordinary state of a solo review — no log, exit 3 — because <c>.review/</c> is created lazily. A
    /// directory holding logs it cannot read is never that: it is a failed read, exit 4 with the reason. Named
    /// here so the drain branches on a property instead of inferring the sub-state from the list, the same rule
    /// <see cref="IsEmpty"/> and <see cref="ProbeResult.IsAbsent"/> exist to enforce.
    /// </remarks>
    public bool IsUnreadable => IsUnknown && Unreadable.Count > 0;

    // The state both no-comment outcomes carry. They differ in what was learned, never in what was found.
    private static ReviewLogState NoComments() => new()
    {
        Comments = Array.Empty<ReviewComment>(),
        Diagnostics = Array.Empty<ReviewDiagnostic>(),
    };
}

/// <summary>
/// Reads every per-author log beside a plan and folds them with
/// <see cref="ReviewLog.Fold(IEnumerable{ReviewLogSource})"/> — the ONE read path shared by the review
/// server's panel and <c>charter poll</c>'s server-less fallback, so the two can never disagree about what a
/// plan's review says.
/// </summary>
/// <remarks>
/// The fold itself is pure; all the I/O is here. A read is retried briefly because the logs arrive by git — a
/// <c>git pull</c> or a merge can be replacing a file at the exact moment the panel refreshes — and a
/// transient sharing conflict must not be reported as a missing teammate.
/// </remarks>
public static class ReviewLogStore
{
    private const int ReadAttempts = 3;
    private const int ReadRetryDelayMs = 15;

    /// <summary>Read and fold the logs for the plan at <paramref name="planPath"/>.</summary>
    public static ReviewLogRead ReadForPlan(string planPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(planPath);
        return Read(ReviewLogPaths.DirectoryForPlan(planPath));
    }

    /// <summary>
    /// Read and fold every <c>*.jsonl</c> in <paramref name="reviewDirectory"/>. A directory that is THERE and
    /// holds no logs folds to <see cref="ReviewLogRead.Empty"/>: a plan nobody has commented on is a normal
    /// state, not a failure. A directory that is not there at all — after a short bounded retry — reads
    /// <see cref="ReviewLogRead.Unknown"/>, because it was never looked into.
    /// </summary>
    public static ReviewLogRead Read(string reviewDirectory) => Read(reviewDirectory, Thread.Sleep);

    /// <summary>
    /// The same read with the retry's WAIT supplied by the caller, so a transient absence can be arranged
    /// deterministically instead of by racing a background thread against the bound.
    /// </summary>
    /// <param name="reviewDirectory">The plan's review-log directory.</param>
    /// <param name="waitBetweenAttempts">
    /// Called between attempts with the delay, in milliseconds, the read would otherwise have slept. The
    /// public overload passes <see cref="Thread.Sleep(int)"/>.
    /// </param>
    internal static ReviewLogRead Read(string reviewDirectory, Action<int> waitBetweenAttempts)
    {
        ArgumentException.ThrowIfNullOrEmpty(reviewDirectory);
        ArgumentNullException.ThrowIfNull(waitBetweenAttempts);

        // The same budget the per-file read spends, for the same reason: the logs arrive by git, so a pull or a
        // checkout can be putting the directory back at the moment the panel refreshes. BOUNDED, and by the
        // attempt count rather than a clock — an unbounded retry would hang the panel instead of emptying it,
        // which is worse than the bug this fixes.
        //
        // A look that answered IN FULL is answered on the first look. A look that did not — the directory was
        // absent, or a log in it could not be read — is worth another within the same budget. That second case
        // is the one Charter #221 kept hitting after the absent-directory fix: ReviewLogWriter appends under
        // FileShare.None and holds the file through an fsync, the per-file retry below spends only ~30ms, and a
        // slow CI disk outlasts it. The reviewer's one log is then EVERY log, and the panel emptied. The LATEST
        // look is what is returned.
        for (var attempt = 1; ; attempt++)
        {
            var read = TryRead(reviewDirectory);
            if (read is not null && read.Unreadable.Count == 0)
            {
                return read;
            }

            if (attempt >= ReadAttempts)
            {
                // Absent throughout is Unknown; every log unreadable is Unknown naming them; a PARTIAL read
                // keeps what it could fold with the unreadable logs reported beside it, as it always has.
                return read ?? ReviewLogRead.Unknown;
            }

            waitBetweenAttempts(ReadRetryDelayMs);
        }
    }

    // One look at the directory: the fold, or null when there was no directory to look into.
    private static ReviewLogRead? TryRead(string reviewDirectory)
    {
        var logs = ReviewLogPaths.EnumerateLogs(reviewDirectory);
        if (logs.Count == 0)
        {
            // EnumerateLogs answers the same empty list to both questions, so the directory itself settles
            // which one was asked: there and holding nothing is a positive finding — nobody has commented —
            // while not there at all is nothing learned. Neither branch complains about what it found.
            // `.review/` is created lazily on the first append, so an absent one is the ORDINARY state of a
            // solo review, and it has to stay every bit as cheap and as quiet as a comment-less directory.
            return Directory.Exists(reviewDirectory) ? ReviewLogRead.Empty : null;
        }

        var sources = new List<ReviewLogSource>(logs.Count);
        var unreadable = new List<string>();

        foreach (var log in logs)
        {
            var fileName = Path.GetFileName(log);
            var text = TryReadAllText(log, out var error);
            if (text is null)
            {
                unreadable.Add($"{fileName}: {error}");
                continue;
            }

            sources.Add(ReviewLogSource.FromText(fileName, text));
        }

        // Logs were there and NONE could be read, so nothing was learned. Folding the empty set would return zero
        // comments labelled Present — "there is nothing" — which is the very silent loss this record exists to
        // prevent, and precisely what emptied the panel in #221's recurrence.
        if (sources.Count == 0)
        {
            return ReviewLogRead.UnknownBecauseUnreadable(unreadable);
        }

        return new ReviewLogRead(ReviewLog.Fold(sources), unreadable);
    }

    // Read one log, tolerating the brief sharing conflicts a concurrent append or a git checkout creates.
    private static string? TryReadAllText(string path, out string? error)
    {
        error = null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                if (attempt >= ReadAttempts)
                {
                    return null;
                }

                Thread.Sleep(ReadRetryDelayMs);
            }
        }
    }
}
