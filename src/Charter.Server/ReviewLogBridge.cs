using System.Security.Cryptography;
using System.Text;
using Charter.Core;

namespace Charter.Server;

/// <summary>
/// A review-log operation was asked a question only the fold can answer, against a <c>.review/</c> that could
/// not be read (Charter #221).
/// </summary>
/// <remarks>
/// Its own type, rather than an <see cref="IOException"/>, because the two are caught for opposite reasons: the
/// I/O exceptions this codebase catches are the ones it means to ABSORB (a log that is momentarily locked, a
/// plan that cannot be re-read), and absorbing this one restores exactly the silence it exists to break.
/// </remarks>
internal sealed class ReviewLogUnreadableException : Exception
{
    public ReviewLogUnreadableException(string reviewDirectory)
        : base($"The review log at '{reviewDirectory}' could not be read, so no comment can be found in it.")
    {
        ReviewDirectory = reviewDirectory;
    }

    /// <summary>The directory that could not be read.</summary>
    public string ReviewDirectory { get; }
}

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
    /// Continue a thread: append the reviewer's REPLY to <paramref name="commentId"/> (Charter #158),
    /// returning the appended record, or null when this Charter has no writer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AppendReply</c> has always defaulted its actor to <c>human</c> — it was written for exactly this
    /// caller — and until now nothing could reach it: the browser bridge covered create/edit/retract/resolve,
    /// and <c>reply</c> was produced only by the agent's <c>charter reply</c>. So a thread was one round deep
    /// by construction: a reviewer who disagreed with the agent's reply could only RESOLVE it (settling a
    /// thing they did not agree with) or open a new, unlinked note.
    /// </para>
    /// <para>
    /// A reply deliberately does NOT change the comment's status. Reopening a settled decision as a side
    /// effect of adding a sentence would be a surprising write; reopen stays its own act.
    /// </para>
    /// </remarks>
    public ReviewRecord? Reply(string commentId, string body)
        => _writer?.AppendReply(commentId, body);

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
    /// <exception cref="ReviewLogUnreadableException">The fold could not be read, so neither answer is honest.</exception>
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
    /// <exception cref="ReviewLogUnreadableException">The fold could not be read, so authorship is unknown.</exception>
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
    /// <exception cref="ReviewLogUnreadableException">
    /// The fold could not be read. The alternative was <c>false</c> — the server's 404 — which tells a reviewer
    /// looking straight at the comment in the panel that it does not exist.
    /// </exception>
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

    /// <summary>
    /// The folded comment with this id, or null when there is no writer or no such comment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null means "not in the fold", and only a fold can say that.</b> Every caller reads null as <i>no such
    /// comment</i> and returns false, which the server turns into a 404 — so returning it for a directory that
    /// was never read tells the reviewer their comment does not exist on the evidence of not having looked
    /// (Charter #221). An <see cref="ReviewLogOutcome.Unknown"/> read therefore raises rather than answering:
    /// none of the three verbs can build an honest record without the fold either, since <c>prev</c> is "what
    /// this author had observed" and a retract turns on the comment's own authorship.
    /// </para>
    /// <para>
    /// The raise reaches the request loop, which answers 500. That is not a good answer, but it is the only one
    /// left: <c>Edit</c> / <c>Retract</c> / <c>Resolve</c> return <c>bool</c>, both of whose values are already
    /// claims about the fold, and "I could not read it" is a third thing.
    /// </para>
    /// </remarks>
    /// <exception cref="ReviewLogUnreadableException">The review directory could not be read.</exception>
    private ReviewComment? Find(string commentId)
    {
        if (_writer is null || string.IsNullOrEmpty(commentId))
        {
            return null;
        }

        var read = ReviewLogStore.Read(Directory);
        if (read.IsUnknown)
        {
            throw new ReviewLogUnreadableException(Directory);
        }

        return read.State.Comments
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
