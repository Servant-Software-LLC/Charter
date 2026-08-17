using Charter.Core;

namespace Charter.Server;

/// <summary>
/// The status tokens the panel and the <c>charter poll</c> envelope share. Kept as constants in one place so
/// the C# projection, the browser SDK, and an agent parsing the envelope cannot drift on spelling.
/// </summary>
public static class ReviewStatusTokens
{
    /// <summary>Awaiting a decision.</summary>
    public const string Open = "open";

    /// <summary>Settled closed by a resolve that observed everything before it.</summary>
    public const string Resolved = "resolved";

    /// <summary>Concurrently resolved AND reopened. NOT resolved — handoff must treat it as open (§4.2).</summary>
    public const string Contested = "contested";

    /// <summary>Withdrawn by its own author; the thread survives, the body is withheld (§4.2).</summary>
    public const string Retracted = "retracted";

    /// <summary>The anchor still matches a block in the plan exactly.</summary>
    public const string AnchorResolved = "resolved";

    /// <summary>
    /// The commented block is no longer in the plan. A NEUTRAL FACT, never "addressed" (§4.3): folding a
    /// <c>:::question</c> answer rewrites that block and orphans every comment on it though nobody addressed
    /// anything.
    /// </summary>
    public const string AnchorOrphaned = "orphaned";

    /// <summary>
    /// The plan is byte-identical (modulo line endings) to the text this comment was written against. A SOUND
    /// positive — byte-identical text at this path is this document (§4.3.1).
    /// </summary>
    public const string BaseCurrent = "current";

    /// <summary>
    /// The plan is not the text this comment was written against. <b>Evidence of nothing on its own</b> — an
    /// ordinary edit and a wholesale replacement look identical here, and this is the modal state of nearly
    /// every comment in a living document. Never read it as "ignore this comment" (§4.3.1).
    /// </summary>
    public const string BaseDifferent = "different";

    /// <summary>The record carries no <c>anchor.base</c>, or the plan could not be read (§4.3.1).</summary>
    public const string BaseUnknown = "unknown";

    /// <summary>The token for a <see cref="ReviewCommentStatus"/>.</summary>
    public static string For(ReviewCommentStatus status) => status switch
    {
        ReviewCommentStatus.Resolved => Resolved,
        ReviewCommentStatus.Contested => Contested,
        ReviewCommentStatus.Retracted => Retracted,
        _ => Open,
    };
}

/// <summary>One side of a contested comment, or the record that settled it — always with its own author (§4.2).</summary>
/// <param name="Id">The settling record's id.</param>
/// <param name="Op">Its op token (<c>resolve</c> / <c>reopen</c>).</param>
/// <param name="AuthorName">Who claimed it.</param>
/// <param name="AuthorEmail">Their identity.</param>
/// <param name="Ts">When they say they claimed it — presentation only (rule 3).</param>
/// <param name="Actor">Whether a human or an agent settled it — an agent-authored resolve is otherwise
/// indistinguishable from the reviewer's own (Charter #157).</param>
public sealed record ReviewLogSide(
    string Id, string Op, string AuthorName, string AuthorEmail, string Actor, string? Ts);

/// <summary>One reply in a comment's thread, as the panel renders it.</summary>
/// <param name="Id">The reply record's id.</param>
/// <param name="AuthorName">Who wrote it.</param>
/// <param name="AuthorEmail">Their identity.</param>
/// <param name="Actor">Whether a human or an agent wrote it.</param>
/// <param name="Body">The settled reply text, or <see langword="null"/> when its author withdrew it.</param>
/// <param name="Retracted">Whether its author withdrew it — replies are never removed by someone else's retract.</param>
/// <param name="Ts">Display stamp.</param>
public sealed record ReviewLogReplyView(
    string Id, string AuthorName, string AuthorEmail, string Actor, string? Body, bool Retracted, string? Ts);

/// <summary>
/// One folded comment projected for display: who said it, whether a human or an agent said it, what state it
/// settled into, and whether its anchor still exists in the plan.
/// </summary>
/// <param name="Id">The comment's id (its <c>create</c> record's id).</param>
/// <param name="AuthorName">Who wrote it.</param>
/// <param name="AuthorEmail">Their identity — the fold compares this, case-insensitively.</param>
/// <param name="Actor">Whether a human or an agent wrote it (§4).</param>
/// <param name="Status">One of <see cref="ReviewStatusTokens"/>' four comment states.</param>
/// <param name="AnchorStatus">Whether the anchor resolved exactly, or is orphaned (§4.3).</param>
/// <param name="AnchorId">The block id the comment was written against.</param>
/// <param name="Kind">The anchor kind token (<c>element</c> / <c>text-range</c> / <c>diagram-node</c>).</param>
/// <param name="Quote">What the reviewer had selected — how an orphan stays legible.</param>
/// <param name="SourceLine">The anchor's current 1-based markdown line, or null when orphaned.</param>
/// <param name="Base">The record's <c>anchor.base</c> verbatim — the plan's content hash when it was recorded.</param>
/// <param name="BaseStatus">
/// Whether the plan is still that text: <c>current</c> / <c>different</c> / <c>unknown</c> (§4.3.1). It exists
/// so the panel's orphan line — <i>"the plan has changed since this comment was written"</i> — is a claim
/// Charter can back, rather than one it asserts on every orphan including the ones where the plan has not
/// changed at all.
/// </param>
/// <param name="Body">The settled text, or null when withdrawn.</param>
/// <param name="Ts">Display stamp.</param>
/// <param name="Mine">Whether the reading identity wrote it — only its own author may retract it.</param>
/// <param name="Sides">The record(s) behind <paramref name="Status"/>; BOTH when contested.</param>
/// <param name="Replies">The thread, in display order.</param>
public sealed record ReviewLogCommentView(
    string Id,
    string AuthorName,
    string AuthorEmail,
    string Actor,
    string Status,
    string AnchorStatus,
    string AnchorId,
    string? Kind,
    string? Quote,
    int? SourceLine,
    string? Base,
    string BaseStatus,
    string? Body,
    string? Ts,
    bool Mine,
    IReadOnlyList<ReviewLogSide> Sides,
    IReadOnlyList<ReviewLogReplyView> Replies);

/// <summary>Something the fold retained but did not apply, surfaced verbatim rather than counted.</summary>
/// <param name="Kind">The diagnostic kind.</param>
/// <param name="Message">Its human-readable explanation.</param>
/// <param name="FileName">The log it came from, when line-scoped.</param>
/// <param name="LineNumber">The 1-based line, when line-scoped.</param>
public sealed record ReviewLogNoticeView(string Kind, string Message, string? FileName, int? LineNumber);

/// <summary>
/// Everything the in-page review panel needs about a plan's review logs, computed SERVER-SIDE: the folded
/// comments of every author, their anchors resolved against the plan as it is right now, and what the fold
/// refused to apply.
/// </summary>
/// <remarks>
/// <para>
/// <b>Anchors resolve by exact block-id match or they are orphaned (§4.3).</b> There is no fuzzy ladder — it
/// was withdrawn because it re-introduced the misattribution class the anchor model was rewritten to
/// eliminate. An orphan renders from its <see cref="ReviewLogCommentView.Quote"/> plus a note that the plan
/// has changed, and is never re-attached to a guess.
/// </para>
/// <para>
/// <b>This is a projection, never a file server.</b> The logs are read and folded in-process and only their
/// PROJECTION crosses the wire. The review server's confinement root is the plan's own directory, so
/// <c>.review/</c> already passes it — adding a static-file branch there would turn every sibling file under
/// <c>docs/plans/</c> into a key-gated HTTP-readable resource in one line.
/// </para>
/// </remarks>
/// <param name="Comments">Every author's comments, in display order.</param>
/// <param name="Diagnostics">What the fold retained but did not apply.</param>
/// <param name="Unreadable">Logs that could not be read at all — never silently treated as absent.</param>
/// <param name="SelfEmail">The identity the panel is reading as, or null when this Charter has no writer.</param>
public sealed record ReviewLogView(
    IReadOnlyList<ReviewLogCommentView> Comments,
    IReadOnlyList<ReviewLogNoticeView> Diagnostics,
    IReadOnlyList<string> Unreadable,
    string? SelfEmail)
{
    /// <summary>The view for a plan with no logs at all.</summary>
    public static ReviewLogView Empty { get; } = new(
        Array.Empty<ReviewLogCommentView>(),
        Array.Empty<ReviewLogNoticeView>(),
        Array.Empty<string>(),
        null);

    /// <summary>
    /// Project <paramref name="read"/> for display, resolving every anchor against
    /// <paramref name="markdown"/> — the plan as it is NOW, which is the only version an anchor can honestly
    /// be checked against.
    /// </summary>
    public static ReviewLogView Build(ReviewLogRead read, string markdown, string? selfEmail)
    {
        ArgumentNullException.ThrowIfNull(read);

        var map = SourceMap.Build(markdown ?? string.Empty);
        var revision = ReviewBaseStatus.ForPlan(markdown);
        var comments = read.State.Comments.Select(c => Project(c, map, revision, selfEmail)).ToList();
        var diagnostics = read.State.Diagnostics
            .Select(d => new ReviewLogNoticeView(d.Kind.ToString(), d.Message, d.FileName, d.LineNumber))
            .ToList();

        return new ReviewLogView(comments, diagnostics, read.Unreadable, selfEmail);
    }

    private static ReviewLogCommentView Project(
        ReviewComment comment, SourceMap map, ReviewBaseStatus revision, string? selfEmail)
    {
        // Exact match or orphan. LineForAnchor IS the exact-id lookup — a miss means the block the reviewer
        // commented on has changed, which is a fact to report, not a prompt to go looking for a near match.
        var sourceLine = map.LineForAnchor(comment.Anchor.BlockId);

        return new ReviewLogCommentView(
            Id: comment.Id,
            AuthorName: DisplayName(comment.Author),
            AuthorEmail: comment.Author.Email,
            Actor: comment.Actor,
            Status: ReviewStatusTokens.For(comment.Status),
            AnchorStatus: sourceLine is null ? ReviewStatusTokens.AnchorOrphaned : ReviewStatusTokens.AnchorResolved,
            AnchorId: comment.Anchor.BlockId,
            Kind: comment.Anchor.Kind,
            Quote: comment.Anchor.Quote,
            SourceLine: sourceLine,
            Base: comment.Anchor.Base,
            BaseStatus: revision.Of(comment.Anchor.Base),
            Body: comment.Body,
            Ts: comment.Record.Ts,
            Mine: IsSelf(comment.Author.Email, selfEmail),
            Sides: comment.ResolutionRecords.Select(Side).ToList(),
            Replies: comment.Replies.Select(Reply).ToList());
    }

    private static ReviewLogSide Side(ReviewRecord record)
        => new(record.Id, record.Op, DisplayName(record.Author), record.Author.Email, record.Actor, record.Ts);

    private static ReviewLogReplyView Reply(ReviewReply reply)
        => new(reply.Id, DisplayName(reply.Author), reply.Author.Email, reply.Actor, reply.Body, reply.IsRetracted, reply.Record.Ts);

    /// <summary>The email when git had no <c>user.name</c> — a panel entry must never be attributed to nobody.</summary>
    private static string DisplayName(ReviewAuthor author)
        => string.IsNullOrEmpty(author.Name) ? author.Email : author.Name;

    /// <summary>Identity is the email, compared case-insensitively — exactly as the fold compares it (§2).</summary>
    private static bool IsSelf(string email, string? selfEmail)
        => !string.IsNullOrEmpty(selfEmail) && string.Equals(email, selfEmail, StringComparison.OrdinalIgnoreCase);
}
