using Charter.Core;

namespace Charter.Server;

/// <summary>
/// The result of reading and folding a plan's review logs: the folded state, plus the logs that could not be
/// read at all. Unreadable files are reported rather than absorbed — a caller that showed a partial fold as if
/// it were complete would be exactly the silent loss the design (§3) exists to prevent.
/// </summary>
/// <param name="State">The folded review state across every log that was readable.</param>
/// <param name="Unreadable">The file names of logs that could not be read, with the reason.</param>
public sealed record ReviewLogRead(ReviewLogState State, IReadOnlyList<string> Unreadable)
{
    /// <summary>An empty read — no review directory, or no logs in it. Not an error.</summary>
    public static ReviewLogRead Empty { get; } = new(
        new ReviewLogState { Comments = Array.Empty<ReviewComment>(), Diagnostics = Array.Empty<ReviewDiagnostic>() },
        Array.Empty<string>());
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
    /// Read and fold every <c>*.jsonl</c> in <paramref name="reviewDirectory"/>. A missing directory folds to
    /// <see cref="ReviewLogRead.Empty"/>: a plan nobody has commented on is a normal state, not a failure.
    /// </summary>
    public static ReviewLogRead Read(string reviewDirectory)
    {
        var logs = ReviewLogPaths.EnumerateLogs(reviewDirectory);
        if (logs.Count == 0)
        {
            return ReviewLogRead.Empty;
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
