namespace Charter.Server;

/// <summary>
/// Answers one question per review-log comment: <b>was this comment written against the text the plan holds
/// right now?</b> — the <c>baseStatus</c> half of the resolution of Charter #74
/// (<c>docs/plans/03-git-mediated-team-review.md</c> §4.3.1, which is normative).
/// </summary>
/// <remarks>
/// <para>
/// <b>This LABELS. It never suppresses.</b> Every reader of the fold delivers every comment it holds (§4.3.1);
/// the #67 sidecar quarantine deliberately does <b>not</b> cross over to the committed review log, because the
/// remedy (copy aside, rewrite the live file) does not exist for a git-tracked file and the evidence ("not one
/// anchor resolves") has a high benign base rate over a shared, permanent log — a fresh clone of a mature plan
/// delivers its whole review history at once and nearly all of it is legitimately orphaned.
/// </para>
/// <para>
/// <b>The asymmetry is the whole point.</b> <see cref="ReviewStatusTokens.BaseCurrent"/> is a SOUND POSITIVE:
/// byte-identical text at this path is this document, by every definition Charter has.
/// <see cref="ReviewStatusTokens.BaseDifferent"/> proves only "not exactly this text" — an ordinary edit and a
/// wholesale replacement produce the same observation, and it is the modal state of nearly every comment in a
/// living document. It must never be read as "ignore this comment"; inferring "a different document is at this
/// path" from a hash mismatch is the §4.3 misattribution class in a different costume.
/// </para>
/// <para>
/// <b>Line endings are not content.</b> <c>anchor.base</c> is minted by <see cref="ReviewLogBridge.PlanHash"/>
/// over the plan's RAW bytes, but <c>Block.StableId</c> normalizes CRLF before hashing precisely because a
/// checkout's <c>core.autocrlf</c> is not an edit. So a plan is compared in every newline form: without this a
/// Windows/Linux team reads <c>different</c> on every comment at the identical revision, and the signal would
/// be dead exactly where team review lives. Widening the match can only ever turn a false <c>different</c> into
/// <c>current</c> — two texts equal modulo line endings are the same document to the anchor model too.
/// </para>
/// <para>
/// The plan is hashed ONCE per read (three small hashes), not once per comment, which is why this is an
/// instance built from the plan rather than a static two-argument call.
/// </para>
/// </remarks>
public sealed class ReviewBaseStatus
{
    /// <summary>The comparator for a plan there is no text for: every comment answers <c>unknown</c>.</summary>
    private static readonly ReviewBaseStatus Unverifiable = new(Array.Empty<string>());

    private readonly IReadOnlyList<string> _hashes;

    private ReviewBaseStatus(IReadOnlyList<string> hashes) => _hashes = hashes;

    /// <summary>
    /// Build the comparator for <paramref name="markdown"/> — the plan AS IT IS NOW.
    /// </summary>
    /// <remarks>
    /// <b>No text means <c>unknown</c>, never <c>different</c>.</b> <see langword="null"/> is a plan that could
    /// not be read; <b>empty is treated the same way</b>, because the reachable ways to hold an empty plan are a
    /// read that raced a truncate-then-write, an interrupted save, and a caller that answers an unreadable file
    /// with <c>string.Empty</c> — in every one of them "the plan is not the text you commented on" would be a
    /// claim about a document Charter never saw. Answering <c>different</c> for a whole review at the moment the
    /// drafting agent happens to be rewriting the plan is precisely the unearned claim §4.3.1 exists to remove,
    /// so the degenerate case of a genuinely, deliberately empty plan is given up to close it.
    /// </remarks>
    public static ReviewBaseStatus ForPlan(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return Unverifiable;
        }

        var lf = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        var forms = new List<string>(3) { markdown };
        foreach (var form in new[] { lf, crlf })
        {
            if (!forms.Contains(form, StringComparer.Ordinal))
            {
                forms.Add(form);
            }
        }

        return new ReviewBaseStatus(forms.Select(ReviewLogBridge.PlanHash).ToList());
    }

    /// <summary>
    /// The token for one record's <c>anchor.base</c>: <c>current</c> when the plan is byte-identical (modulo
    /// line endings) to the text that comment was RECORDED against, <c>different</c> when it is not, and
    /// <c>unknown</c> when the record does not say or there is no plan text to compare.
    /// </summary>
    /// <remarks>
    /// Compared case-insensitively: <see cref="ReviewLogBridge.PlanHash"/> writes lowercase hex, but a record
    /// is a committed artifact that another tool may have produced, and a case difference in a hex digest is
    /// not a difference in the document.
    /// </remarks>
    public string Of(string? recordBase)
    {
        if (string.IsNullOrEmpty(recordBase) || _hashes.Count == 0)
        {
            return ReviewStatusTokens.BaseUnknown;
        }

        return _hashes.Contains(recordBase, StringComparer.OrdinalIgnoreCase)
            ? ReviewStatusTokens.BaseCurrent
            : ReviewStatusTokens.BaseDifferent;
    }
}
