namespace Charter.Core;

/// <summary>
/// The resolve/reopen dimension of a comment. <see cref="Contested"/> is the design's whole point (§4.2):
/// when the latest resolve and the latest reopen are concurrent — neither observed the other — the fold
/// reports the disagreement instead of imposing a timestamp order on it and discarding one side.
/// </summary>
public enum ReviewResolution
{
    /// <summary>No settling resolve, or the settled record re-opened it.</summary>
    Open,

    /// <summary>A resolve settled it, having observed everything before it (or every concurrent branch agrees).</summary>
    Resolved,

    /// <summary>Concurrent, disagreeing settlements. NOT resolved — handoff and execution must treat it as open.</summary>
    Contested,
}

/// <summary>The single status a panel renders for a comment, combining retraction with <see cref="ReviewResolution"/>.</summary>
public enum ReviewCommentStatus
{
    /// <summary>Awaiting a decision.</summary>
    Open,

    /// <summary>Settled closed.</summary>
    Resolved,

    /// <summary>Concurrently resolved AND reopened; both sides are in <see cref="ReviewComment.ResolutionRecords"/>.</summary>
    Contested,

    /// <summary>Withdrawn by its own author. The thread and its replies survive — only the body is hidden (§4.2).</summary>
    Retracted,
}

/// <summary>
/// One reply in a comment's thread. A reply can be edited and retracted by its own author exactly like a
/// comment; it is never removed by someone else's retract of the comment it hangs from — replies are other
/// people's words (§4.2).
/// </summary>
public sealed record ReviewReply
{
    /// <summary>The reply record's id.</summary>
    public required string Id { get; init; }

    /// <summary>The originating <c>reply</c> record, kept whole so nothing the fold does not model is lost.</summary>
    public required ReviewRecord Record { get; init; }

    /// <summary>The settled reply text, or null when the reply has been retracted by its author.</summary>
    public string? Body { get; init; }

    /// <summary>The applied <c>retract</c> record, when the reply's author withdrew it.</summary>
    public ReviewRecord? RetractRecord { get; init; }

    /// <summary>Who wrote the reply.</summary>
    public ReviewAuthor Author => Record.Author;

    /// <summary>Whether a human or an agent wrote it (§4).</summary>
    public string Actor => Record.Actor;

    /// <summary>Whether the author withdrew it; render as "(reply withdrawn by author)".</summary>
    public bool IsRetracted => RetractRecord is not null;

    /// <summary>
    /// The id this reply was written against — the comment, or another reply when the thread nests. The fold
    /// attaches every reply to its root comment and leaves nesting to the renderer.
    /// </summary>
    public string? InReplyTo => Record.Target;
}

/// <summary>
/// One folded comment: its create record, its settled body and status, and its thread. The fold deliberately
/// does NOT say whether the comment's anchor still exists — resolving an anchor needs the plan, and it
/// resolves by exact <see cref="ReviewAnchor.BlockId"/> match or it is orphaned (§4.3). That match is the
/// caller's, so an orphan flag is not a property of the log.
/// </summary>
public sealed record ReviewComment
{
    /// <summary>The comment's id — the id of its <c>create</c> record.</summary>
    public required string Id { get; init; }

    /// <summary>The <c>create</c> record, kept whole (author, actor, ts, anchor, unknown members).</summary>
    public required ReviewRecord Record { get; init; }

    /// <summary>
    /// The settled body: the create's text, or the text of the settled <c>edit</c>. Null when the comment is
    /// retracted, so a caller that renders <see cref="Body"/> cannot leak a withdrawn comment by accident;
    /// the original text remains on <see cref="Record"/> for tooling that genuinely needs it.
    /// </summary>
    public string? Body { get; init; }

    /// <summary>The resolve/reopen dimension, independent of retraction.</summary>
    public required ReviewResolution Resolution { get; init; }

    /// <summary>
    /// The record(s) behind <see cref="Resolution"/>: the settling <c>resolve</c>/<c>reopen</c>, or — when
    /// <see cref="ReviewResolution.Contested"/> — BOTH concurrent sides, each carrying its own author so the
    /// panel can show who claimed what. Empty when the comment was never resolved.
    /// </summary>
    public required IReadOnlyList<ReviewRecord> ResolutionRecords { get; init; }

    /// <summary>The applied <c>retract</c> record, when the comment's own author withdrew it.</summary>
    public ReviewRecord? RetractRecord { get; init; }

    /// <summary>
    /// The state records nothing else observed — the live heads of this comment's <c>prev</c> forest (§4.2).
    /// This is the WRITER's half of the concurrency contract: a new <c>edit</c>/<c>resolve</c>/<c>reopen</c>/
    /// <c>retract</c> must point its <c>prev</c> at what its author has observed, and the heads are exactly
    /// that. Without it a writer can only send <c>prev: null</c>, which makes every second edit by one author
    /// read as a concurrent edit and reverts the body to the last agreed one. Empty when nobody has acted on
    /// the comment. Ordered by id, so it never depends on which order git merged the logs in.
    /// </summary>
    public required IReadOnlyList<ReviewRecord> StateHeads { get; init; }

    /// <summary>The thread, in display order (timestamp then id — presentation only, never causality).</summary>
    public required IReadOnlyList<ReviewReply> Replies { get; init; }

    /// <summary>Where the comment is anchored (§4.3). Never resolved by the fold.</summary>
    public ReviewAnchor Anchor => Record.Anchor!;

    /// <summary>Who wrote the comment.</summary>
    public ReviewAuthor Author => Record.Author;

    /// <summary>Whether a human or an agent wrote it (§4).</summary>
    public string Actor => Record.Actor;

    /// <summary>Whether the author withdrew it; render as "(comment withdrawn by author)" with the thread intact.</summary>
    public bool IsRetracted => RetractRecord is not null;

    /// <summary>
    /// The one status to render. Retraction wins over the resolve/reopen dimension, which stays visible on
    /// <see cref="Resolution"/>. A <see cref="ReviewCommentStatus.Contested"/> comment is NOT resolved.
    /// </summary>
    public ReviewCommentStatus Status => IsRetracted
        ? ReviewCommentStatus.Retracted
        : Resolution switch
        {
            ReviewResolution.Resolved => ReviewCommentStatus.Resolved,
            ReviewResolution.Contested => ReviewCommentStatus.Contested,
            _ => ReviewCommentStatus.Open,
        };
}

/// <summary>
/// The result of folding a plan's review logs: the current comments plus everything the fold refused to
/// apply silently. Both lists are deterministically ordered, so the same records fold to the same state no
/// matter which order git happened to merge them in (rule 2).
/// </summary>
public sealed record ReviewLogState
{
    /// <summary>The comments, ordered for display by timestamp then id — never by input or file order.</summary>
    public required IReadOnlyList<ReviewComment> Comments { get; init; }

    /// <summary>Everything retained but not applied, and everything applied that a human should know about.</summary>
    public required IReadOnlyList<ReviewDiagnostic> Diagnostics { get; init; }
}
