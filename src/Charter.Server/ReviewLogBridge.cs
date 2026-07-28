using System.Security.Cryptography;
using System.Text;
using Charter.Core;

namespace Charter.Server;

/// <summary>
/// The review server's half of the git-mediated review log: it folds every author's log for the panel, and
/// turns the panel's actions (comment / edit / retract / resolve) into appended records in THIS author's log.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every write folds first.</b> The record a write must produce depends on state that lives in other
/// people's files — <c>prev</c> is "the latest state record this author had observed" (§4.2), and a retract is
/// valid only from the item's own author. Re-folding is the only honest way to know either, and a plan's logs
/// are a few kilobytes, so the cost is not worth an incremental cache that could go stale behind a
/// <c>git pull</c>.
/// </para>
/// <para>
/// <b>`prev` is a single pointer, so it can absorb only one head.</b> When a comment is already contested
/// there are several live heads and a new record can name only one of them; the bridge picks the ordinally
/// greatest, deterministically. That is faithful to §4.2 (the author really did observe it) but it means a
/// contest cannot always be cleared by one resolve — see the report accompanying this change.
/// </para>
/// <para>
/// <b>Nothing here serves a file.</b> The logs are read and folded in-process and only their projection
/// (<see cref="ReviewLogView"/>) crosses the wire, so the plan directory never becomes HTTP-readable.
/// </para>
/// </remarks>
internal sealed class ReviewLogBridge
{
    private readonly string _planPath;
    private readonly ReviewLogWriter? _writer;

    public ReviewLogBridge(string planPath, ReviewLogWriter? writer)
    {
        ArgumentException.ThrowIfNullOrEmpty(planPath);
        _planPath = planPath;
        _writer = writer;
        Directory = writer?.ReviewDirectory ?? ReviewLogPaths.DirectoryForPlan(planPath);
    }

    /// <summary>The plan's <c>.review/</c> directory — what the reload watcher watches, alongside the plan file.</summary>
    public string Directory { get; }

    /// <summary>Whether this Charter has an author identity and may therefore append records.</summary>
    public bool CanWrite => _writer is not null;

    /// <summary>
    /// Fold every author's log and project it for the panel, resolving anchors against
    /// <paramref name="markdown"/> — the plan as it is right now.
    /// </summary>
    public ReviewLogView BuildView(string markdown)
        => ReviewLogView.Build(ReviewLogStore.Read(Directory), markdown, _writer?.Author.Email);

    /// <summary>
    /// Open a comment, returning the appended record (whose id becomes the annotation's id, so the panel's
    /// later edit/retract/resolve reach the right record), or null when this Charter has no writer.
    /// </summary>
    public ReviewRecord? Create(ReviewAnchor anchor, string body)
        => _writer?.AppendCreate(anchor, body);

    /// <summary>
    /// The plan's content hash, stamped into a new comment's <c>anchor.base</c> (§4). It is written NOW
    /// because records are immutable and committed forever: build-order step 5 renders an orphan's diff by
    /// fetching this plan revision from git, and a record that never carried the hash can never acquire one.
    /// </summary>
    public static string PlanHash(string markdown)
        => "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(markdown ?? string.Empty))).ToLowerInvariant();

    /// <summary>
    /// Replace <paramref name="commentId"/>'s body. False when there is no writer or no such comment in the
    /// fold — never a guess about which comment was meant.
    /// </summary>
    public bool Edit(string commentId, string body)
    {
        var comment = Find(commentId);
        if (comment is null)
        {
            return false;
        }

        _writer!.AppendEdit(commentId, body, PrevFor(comment));
        return true;
    }

    /// <summary>
    /// Withdraw <paramref name="commentId"/>. Refused for anyone but the comment's own author — the fold would
    /// retain and report such a record without applying it, so writing one would only add noise, and the point
    /// of the rule is that a teammate cannot silently delete a blocking objection (§4.2).
    /// </summary>
    public bool Retract(string commentId)
    {
        var comment = Find(commentId);
        if (comment is null || !IsOwnComment(comment))
        {
            return false;
        }

        _writer!.AppendRetract(commentId, PrevFor(comment));
        return true;
    }

    /// <summary>
    /// Close <paramref name="commentId"/>. Open to anyone — review is collaborative, and the panel attributes
    /// every settlement to whoever made it (§4.2).
    /// </summary>
    public bool Resolve(string commentId)
    {
        var comment = Find(commentId);
        if (comment is null)
        {
            return false;
        }

        _writer!.AppendResolve(commentId, PrevFor(comment));
        return true;
    }

    /// <summary>The folded comment with this id, or null when there is no writer or no such comment.</summary>
    private ReviewComment? Find(string commentId)
    {
        if (_writer is null || string.IsNullOrEmpty(commentId))
        {
            return null;
        }

        return ReviewLogStore.Read(Directory).State.Comments
            .FirstOrDefault(c => string.Equals(c.Id, commentId, StringComparison.Ordinal));
    }

    /// <summary>Identity is the email, compared case-insensitively — exactly as the fold compares it.</summary>
    private bool IsOwnComment(ReviewComment comment)
        => string.Equals(comment.Author.Email, _writer!.Author.Email, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a new state record should claim to have observed: the live head of the comment's <c>prev</c>
    /// forest. Null when nobody has acted on the comment yet.
    /// </summary>
    private static string? PrevFor(ReviewComment comment)
        => comment.StateHeads.Count == 0 ? null : comment.StateHeads[^1].Id;
}
