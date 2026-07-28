namespace Charter.Core;

/// <summary>
/// One per-author review log handed to the fold: the file's name (for diagnostics) and its lines, in file
/// order. A source is pure data — reading it off disk belongs to the caller, so the fold stays a pure
/// function of its input.
/// </summary>
/// <param name="FileName">The log's file name, echoed in line-scoped diagnostics. Never a full path.</param>
/// <param name="Lines">The file's lines in file order; blank lines are ignored, and every line's 1-based index is its line number.</param>
public sealed record ReviewLogSource(string FileName, IReadOnlyList<string> Lines)
{
    /// <summary>
    /// Split a log file's text into lines the way the fold expects: on <c>\n</c>, tolerating <c>\r\n</c>.
    /// A trailing newline yields a final empty line, which the fold ignores.
    /// </summary>
    public static ReviewLogSource FromText(string fileName, string text)
    {
        var lines = (text ?? string.Empty).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd('\r');
        }

        return new ReviewLogSource(fileName, lines);
    }
}

/// <summary>
/// The deterministic fold from per-author append-only JSONL logs to the current review state, implementing
/// the eight fold rules and the concurrency detection of
/// <c>docs/plans/03-git-mediated-team-review.md</c> (§3, §4.2, §4.3) — which is normative, not descriptive.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order independence is the load-bearing property.</b> The spike proved that the same branches merged in
/// two orders produce different byte order, so two teammates with identical commits legitimately hold
/// different files. Nothing here may depend on file order, on the order of the lines within a file, or on
/// timestamps (rules 2 and 3) — every ordering the fold emits is derived from the records themselves.
/// </para>
/// <para>
/// <b>Nothing is dropped and nothing is guessed.</b> A malformed line, an unknown op, a known op at an
/// unknown version, an orphaned target, a retract by the wrong author and a concurrent settlement are all
/// retained and reported (see <see cref="ReviewDiagnosticKind"/>), never silently absorbed.
/// </para>
/// <para>
/// The fold is pure: no clock, no randomness, no I/O. The same sources always fold to the same state.
/// </para>
/// </remarks>
public static class ReviewLog
{
    /// <summary>The one record version this Charter applies. A known op at any other version is retained but NOT applied (rule 7).</summary>
    public const int SupportedVersion = ReviewRecord.CurrentVersion;

    /// <summary>
    /// Fold every author's log into the current review state. Always returns state: every problem surfaces
    /// as a <see cref="ReviewDiagnostic"/> rather than an exception, because one bad line must not cost a
    /// reviewer the rest of the review (rule 5).
    /// </summary>
    public static ReviewLogState Fold(IEnumerable<ReviewLogSource> sources)
    {
        var diagnostics = new List<ReviewDiagnostic>();
        var entries = ParseLines(sources, diagnostics);
        var byId = Deduplicate(entries, diagnostics);
        var applicable = SelectApplicable(byId, diagnostics);
        var comments = BuildComments(byId, applicable, diagnostics);

        return new ReviewLogState
        {
            Comments = comments,
            Diagnostics = SortDiagnostics(diagnostics),
        };
    }

    /// <summary>One parsed line, kept with where it came from so a duplicate or malformed line can be located.</summary>
    private sealed record LogEntry(ReviewRecord Record, string FileName, int LineNumber);

    /// <summary>
    /// Pass one: parse every line of every source. A line that will not parse is reported with its file, its
    /// 1-based line number and its raw text, and the fold carries on (rule 5).
    /// </summary>
    private static List<LogEntry> ParseLines(IEnumerable<ReviewLogSource> sources, List<ReviewDiagnostic> diagnostics)
    {
        var entries = new List<LogEntry>();
        foreach (var source in sources ?? Enumerable.Empty<ReviewLogSource>())
        {
            var lines = source.Lines ?? Array.Empty<string>();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (ReviewRecord.TryParse(line, out var record, out var error))
                {
                    entries.Add(new LogEntry(record!, source.FileName, i + 1));
                    continue;
                }

                diagnostics.Add(new ReviewDiagnostic
                {
                    Kind = ReviewDiagnosticKind.MalformedLine,
                    Message = error!,
                    FileName = source.FileName,
                    LineNumber = i + 1,
                    RawLine = line,
                });
            }
        }

        return entries;
    }

    /// <summary>
    /// Rule 1 — dedupe by id. Identical copies are reachable through ordinary merges, so "first wins" is
    /// safe for them; when two copies of an id are NOT identical the input order would decide, which rule 2
    /// forbids, so the ordinally-smallest canonical form wins and the collision is reported.
    /// </summary>
    private static Dictionary<string, ReviewRecord> Deduplicate(
        List<LogEntry> entries,
        List<ReviewDiagnostic> diagnostics)
    {
        var byId = new Dictionary<string, ReviewRecord>(StringComparer.Ordinal);
        foreach (var group in entries.GroupBy(e => e.Record.Id, StringComparer.Ordinal))
        {
            var copies = group.ToList();
            if (copies.Count == 1)
            {
                byId[group.Key] = copies[0].Record;
                continue;
            }

            var ranked = copies
                .Select(entry => (Entry: entry, Json: entry.Record.ToJson()))
                .OrderBy(copy => copy.Json, StringComparer.Ordinal)
                .ThenBy(copy => copy.Entry.FileName, StringComparer.Ordinal)
                .ThenBy(copy => copy.Entry.LineNumber)
                .ToList();

            var conflicting = ranked.Any(copy => !string.Equals(copy.Json, ranked[0].Json, StringComparison.Ordinal));
            byId[group.Key] = ranked[0].Entry.Record;

            foreach (var loser in ranked.Skip(1))
            {
                diagnostics.Add(new ReviewDiagnostic
                {
                    Kind = conflicting ? ReviewDiagnosticKind.ConflictingDuplicate : ReviewDiagnosticKind.DuplicateRecord,
                    Message = conflicting
                        ? $"Record id \"{group.Key}\" is used by records with different content; the ordinally-first content was applied."
                        : $"Record id \"{group.Key}\" appears more than once; the duplicate was ignored.",
                    FileName = loser.Entry.FileName,
                    LineNumber = loser.Entry.LineNumber,
                    RecordId = group.Key,
                    Record = loser.Entry.Record,
                });
            }
        }

        return byId;
    }

    /// <summary>
    /// Rules 6 and 7 — an unknown op is retained, ignored for state and counted; a KNOWN op at an unknown
    /// version is retained, reported and NOT applied, because applying it under this version's semantics is
    /// exactly how two teammates end up silently holding different state.
    /// </summary>
    private static List<ReviewRecord> SelectApplicable(
        Dictionary<string, ReviewRecord> byId,
        List<ReviewDiagnostic> diagnostics)
    {
        var applicable = new List<ReviewRecord>();
        foreach (var record in byId.Values.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            if (record.OpKind == ReviewOpKind.Unknown)
            {
                diagnostics.Add(new ReviewDiagnostic
                {
                    Kind = ReviewDiagnosticKind.UnknownOp,
                    Message = $"Op \"{record.Op}\" is not known to this Charter; the record was retained but not applied.",
                    RecordId = record.Id,
                    Record = record,
                });
                continue;
            }

            if (record.Version != SupportedVersion)
            {
                diagnostics.Add(new ReviewDiagnostic
                {
                    Kind = ReviewDiagnosticKind.UnknownVersion,
                    Message = $"Record version {record.Version} is not supported (this Charter applies v{SupportedVersion}); "
                        + $"the \"{record.Op}\" record was retained but not applied.",
                    RecordId = record.Id,
                    Record = record,
                });
                continue;
            }

            applicable.Add(record);
        }

        return applicable;
    }

    /// <summary>Pass two — apply the applicable records to build every comment and its thread.</summary>
    private static List<ReviewComment> BuildComments(
        Dictionary<string, ReviewRecord> byId,
        List<ReviewRecord> applicable,
        List<ReviewDiagnostic> diagnostics)
    {
        var creates = applicable
            .Where(r => r.OpKind == ReviewOpKind.Create)
            .ToDictionary(r => r.Id, StringComparer.Ordinal);
        var replies = applicable
            .Where(r => r.OpKind == ReviewOpKind.Reply)
            .ToDictionary(r => r.Id, StringComparer.Ordinal);

        var repliesByComment = AttachReplies(replies, creates, byId, diagnostics);
        var nodesByTarget = GroupStateRecords(applicable, creates, replies, byId, diagnostics, out var ineffective);

        var comments = new List<ReviewComment>();
        foreach (var create in creates.Values)
        {
            var thread = repliesByComment.TryGetValue(create.Id, out var attached) ? attached : new List<ReviewRecord>();
            comments.Add(BuildComment(create, thread, nodesByTarget, ineffective, diagnostics));
        }

        return Order(comments, c => c.Record, c => c.Id);
    }

    /// <summary>
    /// Attach every reply to its ROOT comment, following a reply-of-a-reply up the thread. A reply whose
    /// target is not a folded comment is an unmerged branch, not corruption: it is reported and kept in the
    /// diagnostics, and it attaches by itself once the target arrives in another source (rule 4).
    /// </summary>
    private static Dictionary<string, List<ReviewRecord>> AttachReplies(
        Dictionary<string, ReviewRecord> replies,
        Dictionary<string, ReviewRecord> creates,
        Dictionary<string, ReviewRecord> byId,
        List<ReviewDiagnostic> diagnostics)
    {
        var byComment = new Dictionary<string, List<ReviewRecord>>(StringComparer.Ordinal);
        foreach (var reply in replies.Values)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { reply.Id };
            var current = reply.Target;
            while (current is not null && !creates.ContainsKey(current) && replies.TryGetValue(current, out var parent))
            {
                if (!visited.Add(current))
                {
                    diagnostics.Add(Cycle(reply, current));
                    current = null;
                    break;
                }

                current = parent.Target;
            }

            if (current is null || !creates.ContainsKey(current))
            {
                if (current is not null)
                {
                    diagnostics.Add(OrphanTarget(reply, current, byId));
                }

                continue;
            }

            if (!byComment.TryGetValue(current, out var thread))
            {
                thread = new List<ReviewRecord>();
                byComment[current] = thread;
            }

            thread.Add(reply);
        }

        return byComment;
    }

    /// <summary>
    /// Bucket every state record (<c>edit</c>/<c>resolve</c>/<c>reopen</c>/<c>retract</c>) under the item it
    /// targets, reporting the three cases that must never apply silently: an orphaned target (rule 4), a
    /// <c>resolve</c>/<c>reopen</c> aimed at a reply rather than a comment, and a <c>retract</c> by anyone
    /// other than the item's own author (§4.2). A rejected retract stays in the chain — its author really did
    /// observe it — but is listed in <paramref name="ineffective"/> so it changes nothing.
    /// </summary>
    private static Dictionary<string, List<ReviewRecord>> GroupStateRecords(
        List<ReviewRecord> applicable,
        Dictionary<string, ReviewRecord> creates,
        Dictionary<string, ReviewRecord> replies,
        Dictionary<string, ReviewRecord> byId,
        List<ReviewDiagnostic> diagnostics,
        out HashSet<string> ineffective)
    {
        ineffective = new HashSet<string>(StringComparer.Ordinal);
        var nodesByTarget = new Dictionary<string, List<ReviewRecord>>(StringComparer.Ordinal);

        foreach (var record in applicable.Where(r => ReviewOps.IsStateOp(r.OpKind)))
        {
            var target = record.Target ?? string.Empty;
            var item = creates.TryGetValue(target, out var comment) ? comment
                : replies.TryGetValue(target, out var reply) ? reply
                : null;

            if (item is null)
            {
                diagnostics.Add(OrphanTarget(record, target, byId));
                continue;
            }

            if ((record.OpKind is ReviewOpKind.Resolve or ReviewOpKind.Reopen) && !creates.ContainsKey(target))
            {
                diagnostics.Add(new ReviewDiagnostic
                {
                    Kind = ReviewDiagnosticKind.UnsupportedTarget,
                    Message = $"\"{record.Op}\" targets reply \"{target}\" rather than a comment; the record was retained but not applied.",
                    RecordId = record.Id,
                    TargetId = target,
                    Record = record,
                });
                continue;
            }

            if (record.OpKind == ReviewOpKind.Retract && !IsSameAuthor(record.Author, item.Author))
            {
                diagnostics.Add(new ReviewDiagnostic
                {
                    Kind = ReviewDiagnosticKind.RetractNotByAuthor,
                    Message = $"\"retract\" of \"{target}\" is by {record.Author.Email}, not its author {item.Author.Email}; "
                        + "the record was retained but not applied.",
                    RecordId = record.Id,
                    TargetId = target,
                    Record = record,
                });
                ineffective.Add(record.Id);
            }

            if (!nodesByTarget.TryGetValue(target, out var nodes))
            {
                nodes = new List<ReviewRecord>();
                nodesByTarget[target] = nodes;
            }

            nodes.Add(record);
        }

        return nodesByTarget;
    }

    /// <summary>Fold one comment and its thread from the state records that target them.</summary>
    private static ReviewComment BuildComment(
        ReviewRecord create,
        List<ReviewRecord> thread,
        Dictionary<string, List<ReviewRecord>> nodesByTarget,
        HashSet<string> ineffective,
        List<ReviewDiagnostic> diagnostics)
    {
        var outcome = FoldItem(create, nodesByTarget, ineffective, diagnostics);
        var replies = thread
            .Select(reply => BuildReply(reply, nodesByTarget, ineffective, diagnostics));

        return new ReviewComment
        {
            Id = create.Id,
            Record = create,
            Body = outcome.Retract is null ? outcome.Body(create) : null,
            Resolution = outcome.Resolution,
            ResolutionRecords = outcome.ResolutionRecords,
            RetractRecord = outcome.Retract,
            StateHeads = outcome.StateHeads,
            Replies = Order(replies.ToList(), r => r.Record, r => r.Id),
        };
    }

    /// <summary>Fold one reply: replies carry a body and can be edited or withdrawn by their own author.</summary>
    private static ReviewReply BuildReply(
        ReviewRecord reply,
        Dictionary<string, List<ReviewRecord>> nodesByTarget,
        HashSet<string> ineffective,
        List<ReviewDiagnostic> diagnostics)
    {
        var outcome = FoldItem(reply, nodesByTarget, ineffective, diagnostics);
        return new ReviewReply
        {
            Id = reply.Id,
            Record = reply,
            Body = outcome.Retract is null ? outcome.Body(reply) : null,
            RetractRecord = outcome.Retract,
        };
    }

    /// <summary>What the state records settled for one item.</summary>
    private sealed record ItemOutcome(
        ReviewResolution Resolution,
        IReadOnlyList<ReviewRecord> ResolutionRecords,
        ReviewRecord? Edit,
        ReviewRecord? Retract,
        IReadOnlyList<ReviewRecord> StateHeads)
    {
        /// <summary>An item nobody has acted on: open, unedited, not withdrawn.</summary>
        public static readonly ItemOutcome Untouched =
            new(ReviewResolution.Open, Array.Empty<ReviewRecord>(), null, null, Array.Empty<ReviewRecord>());

        /// <summary>The settled text: the settled edit's body, or the item's original body.</summary>
        public string? Body(ReviewRecord item) => Edit?.Body ?? item.Body;
    }

    /// <summary>
    /// Settle one item from its <c>prev</c> chain (§4.2). Concurrency is DETECTED, never ordered: the chain's
    /// heads are the records nothing else observed, and when two heads disagree the fold says so.
    /// </summary>
    private static ItemOutcome FoldItem(
        ReviewRecord item,
        Dictionary<string, List<ReviewRecord>> nodesByTarget,
        HashSet<string> ineffective,
        List<ReviewDiagnostic> diagnostics)
    {
        if (!nodesByTarget.TryGetValue(item.Id, out var nodes) || nodes.Count == 0)
        {
            return ItemOutcome.Untouched;
        }

        var chain = new PrevChain(nodes);
        if (chain.HasCycle)
        {
            diagnostics.Add(new ReviewDiagnostic
            {
                Kind = ReviewDiagnosticKind.ChainCycle,
                Message = $"The \"prev\" chain for \"{item.Id}\" loops back on itself; every record in it was treated as unobserved.",
                TargetId = item.Id,
            });
        }

        var (resolution, resolutionRecords) = SettleResolution(chain);
        var edit = SettleEdit(chain, item.Id, diagnostics);
        var retract = nodes
            .Where(n => n.OpKind == ReviewOpKind.Retract && !ineffective.Contains(n.Id))
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        return new ItemOutcome(resolution, resolutionRecords, edit, retract, chain.Heads);
    }

    /// <summary>
    /// The resolve/reopen dimension. Each chain head votes with the nearest <c>resolve</c>/<c>reopen</c> it
    /// observed; a branch that never touched resolution does not vote (a teammate's edit is not a claim about
    /// whether the comment is closed). Agreeing votes converge — two concurrent resolves are just resolved —
    /// and disagreeing ones are <see cref="ReviewResolution.Contested"/>, with BOTH sides reported. There is
    /// deliberately no timestamp tie-break: that is the behaviour that silently discards an offline
    /// reviewer's explicit reopen.
    /// </summary>
    private static (ReviewResolution Resolution, IReadOnlyList<ReviewRecord> Records) SettleResolution(PrevChain chain)
    {
        var votes = chain.Vote(r => r.OpKind is ReviewOpKind.Resolve or ReviewOpKind.Reopen);
        if (votes.Count == 0)
        {
            return (ReviewResolution.Open, Array.Empty<ReviewRecord>());
        }

        var resolves = votes.Any(v => v.OpKind == ReviewOpKind.Resolve);
        var reopens = votes.Any(v => v.OpKind == ReviewOpKind.Reopen);
        var resolution = resolves && reopens ? ReviewResolution.Contested
            : resolves ? ReviewResolution.Resolved
            : ReviewResolution.Open;

        return (resolution, votes);
    }

    /// <summary>
    /// The body dimension, settled the same way. One voting head means an observed edit chain and its body
    /// stands; several mean genuinely concurrent edits, which are reported and left unordered — the last
    /// body every branch agreed on stands instead of an arbitrary winner.
    /// </summary>
    private static ReviewRecord? SettleEdit(PrevChain chain, string itemId, List<ReviewDiagnostic> diagnostics)
    {
        var votes = chain.Vote(r => r.OpKind == ReviewOpKind.Edit);
        if (votes.Count <= 1)
        {
            return votes.Count == 1 ? votes[0] : null;
        }

        diagnostics.Add(new ReviewDiagnostic
        {
            Kind = ReviewDiagnosticKind.ConcurrentEdit,
            Message = $"Concurrent edits to \"{itemId}\" ({string.Join(", ", votes.Select(v => v.Id))}); "
                + "the last agreed body was kept and neither edit was applied.",
            TargetId = itemId,
        });

        return chain.LastAgreed(r => r.OpKind == ReviewOpKind.Edit);
    }

    /// <summary>
    /// A <c>prev</c> forest over one item's state records. A record's parent is the record it OBSERVED; a
    /// <c>prev</c> naming a record this fold has not seen (an unmerged branch) or naming itself counts as
    /// observing nothing, so that record is a root. The heads — records no other record observed — are the
    /// live claims about the item, and more than one head means concurrency.
    /// </summary>
    private sealed class PrevChain
    {
        private readonly Dictionary<string, ReviewRecord> _nodes;
        private readonly Dictionary<string, List<ReviewRecord>> _ancestorsByHead = new(StringComparer.Ordinal);

        public PrevChain(IReadOnlyList<ReviewRecord> nodes)
        {
            _nodes = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                var parent = ParentOf(node);
                if (parent is not null)
                {
                    observed.Add(parent.Id);
                }
            }

            var heads = nodes.Where(n => !observed.Contains(n.Id)).OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
            if (heads.Count == 0)
            {
                // Every record was observed by another, so the chain must loop — impossible from Charter's
                // writer. Trust nothing about the order: treat every record as its own live claim.
                HasCycle = true;
                heads = nodes.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
            }

            Heads = heads;
            foreach (var head in heads)
            {
                _ancestorsByHead[head.Id] = WalkFrom(head);
            }
        }

        /// <summary>The records nothing else observed — one per concurrent branch.</summary>
        public IReadOnlyList<ReviewRecord> Heads { get; }

        /// <summary>True when a <c>prev</c> link loops; the caller reports it rather than trusting the chain.</summary>
        public bool HasCycle { get; private set; }

        /// <summary>
        /// One vote per head: the nearest record on that branch matching <paramref name="predicate"/>, deduped
        /// by id and ordered by id so the result never depends on input order. A branch with no match abstains.
        /// </summary>
        public IReadOnlyList<ReviewRecord> Vote(Func<ReviewRecord, bool> predicate)
        {
            var votes = new Dictionary<string, ReviewRecord>(StringComparer.Ordinal);
            foreach (var head in Heads)
            {
                var vote = _ancestorsByHead[head.Id].FirstOrDefault(predicate);
                if (vote is not null)
                {
                    votes[vote.Id] = vote;
                }
            }

            return votes.Values.OrderBy(v => v.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// The nearest record matching <paramref name="predicate"/> at or above the point every head still
        /// agreed — the last state nobody has contradicted. Null when the branches share no history.
        /// </summary>
        public ReviewRecord? LastAgreed(Func<ReviewRecord, bool> predicate)
        {
            var chains = Heads.Select(head => _ancestorsByHead[head.Id]).ToList();
            var shared = chains.Skip(1)
                .Select(chain => new HashSet<string>(chain.Select(n => n.Id), StringComparer.Ordinal))
                .ToList();

            var forkIndex = chains[0].FindIndex(node => shared.All(ids => ids.Contains(node.Id)));
            return forkIndex < 0 ? null : chains[0].Skip(forkIndex).FirstOrDefault(predicate);
        }

        /// <summary>The record <paramref name="node"/> observed, or null when it observed nothing this fold can see.</summary>
        private ReviewRecord? ParentOf(ReviewRecord node)
            => node.Prev is { Length: > 0 } prev
                && !string.Equals(prev, node.Id, StringComparison.Ordinal)
                && _nodes.TryGetValue(prev, out var parent)
                ? parent
                : null;

        /// <summary>The branch from <paramref name="head"/> back to its root, nearest first; a loop stops the walk and is reported.</summary>
        private List<ReviewRecord> WalkFrom(ReviewRecord head)
        {
            var branch = new List<ReviewRecord>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var node = head; node is not null; node = ParentOf(node))
            {
                if (!visited.Add(node.Id))
                {
                    HasCycle = true;
                    break;
                }

                branch.Add(node);
            }

            return branch;
        }
    }

    /// <summary>Whether two records were written by the same person. Email is the identity (§4); names are not.</summary>
    private static bool IsSameAuthor(ReviewAuthor left, ReviewAuthor right)
        => string.Equals(left.Email, right.Email, StringComparison.OrdinalIgnoreCase);

    /// <summary>An orphaned target: an unmerged branch, not corruption. Retained here so it is never lost (rule 4).</summary>
    private static ReviewDiagnostic OrphanTarget(ReviewRecord record, string target, Dictionary<string, ReviewRecord> byId)
        => new()
        {
            Kind = ReviewDiagnosticKind.OrphanTarget,
            Message = byId.ContainsKey(target)
                ? $"\"{record.Op}\" targets \"{target}\", which was retained but not applied; the record was retained but not applied."
                : $"\"{record.Op}\" targets \"{target}\", which is not in any log yet (an unmerged branch); the record was retained but not applied.",
            RecordId = record.Id,
            TargetId = target,
            Record = record,
        };

    /// <summary>A reply thread that points back at itself — reported rather than followed.</summary>
    private static ReviewDiagnostic Cycle(ReviewRecord record, string target)
        => new()
        {
            Kind = ReviewDiagnosticKind.ChainCycle,
            Message = $"The reply chain from \"{record.Id}\" loops back on itself at \"{target}\"; the reply was not attached.",
            RecordId = record.Id,
            TargetId = target,
            Record = record,
        };

    /// <summary>
    /// Display order: timestamp then id. Timestamps are presentation ONLY (rule 3) — this orders a thread for
    /// a human and never decides state — and the id tie-break keeps the order total, so it cannot depend on
    /// which order git merged the logs in.
    /// </summary>
    private static List<T> Order<T>(List<T> items, Func<T, ReviewRecord> record, Func<T, string> id)
        => items
            .OrderBy(item => record(item).Timestamp ?? DateTimeOffset.MinValue)
            .ThenBy(id, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// A total, ordinal order over diagnostics so the whole fold result — not just its comments — is a
    /// deterministic function of the records, on every platform and culture.
    /// </summary>
    private static List<ReviewDiagnostic> SortDiagnostics(List<ReviewDiagnostic> diagnostics)
        => diagnostics
            .OrderBy(d => d.Kind)
            .ThenBy(d => d.FileName ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.LineNumber ?? 0)
            .ThenBy(d => d.RecordId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.TargetId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ToList();
}
