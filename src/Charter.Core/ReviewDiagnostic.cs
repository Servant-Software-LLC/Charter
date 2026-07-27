namespace Charter.Core;

/// <summary>
/// What the fold noticed but did not silently absorb. Every kind here exists because the design of record
/// (<c>docs/plans/03-git-mediated-team-review.md</c>) requires the condition to be LOUD: a fold that quietly
/// drops, guesses, or orders is the failure mode the whole design is built to prevent.
/// </summary>
public enum ReviewDiagnosticKind
{
    /// <summary>A line that is not a well-formed record: reported with file, line number and raw text, and folding continues (rule 5).</summary>
    MalformedLine,

    /// <summary>A byte-identical record seen more than once — reachable through ordinary merges; first wins (rule 1).</summary>
    DuplicateRecord,

    /// <summary>Two DIFFERENT records share one id. Retained, reported, and settled deterministically rather than by input order (rules 1 + 2).</summary>
    ConflictingDuplicate,

    /// <summary>An <c>op</c> this Charter does not know: retained, ignored for state, counted (rule 6).</summary>
    UnknownOp,

    /// <summary>A known op at an unknown <c>v</c>: retained, reported, and NOT applied — never reinterpreted under this version's semantics (rule 7).</summary>
    UnknownVersion,

    /// <summary>A record whose <c>target</c> is not a folded comment/reply — usually an unmerged branch, never corruption (rule 4).</summary>
    OrphanTarget,

    /// <summary>A <c>resolve</c>/<c>reopen</c> aimed at a reply rather than a comment: retained, reported, not applied.</summary>
    UnsupportedTarget,

    /// <summary>A <c>retract</c> by someone other than the item's author: retained, reported, NOT applied (§4.2).</summary>
    RetractNotByAuthor,

    /// <summary>Concurrent <c>edit</c>s to one item: the last agreed body stands and both edits are reported, never silently ordered.</summary>
    ConcurrentEdit,

    /// <summary>A <c>prev</c> chain that loops back on itself — impossible from Charter's writer, so it is reported rather than trusted.</summary>
    ChainCycle,
}

/// <summary>
/// One thing the fold retained but did not apply, or applied while wanting a human to know. Diagnostics are
/// data, not exceptions: <see cref="ReviewLog.Fold(IEnumerable{ReviewLogSource})"/> always returns state.
/// </summary>
public sealed record ReviewDiagnostic
{
    /// <summary>What was noticed.</summary>
    public required ReviewDiagnosticKind Kind { get; init; }

    /// <summary>A human-readable explanation. Deterministic — it embeds ids and ops, never paths or clocks.</summary>
    public required string Message { get; init; }

    /// <summary>The log file the offending line came from, when the diagnostic is line-scoped.</summary>
    public string? FileName { get; init; }

    /// <summary>The 1-based line number within <see cref="FileName"/>, when the diagnostic is line-scoped.</summary>
    public int? LineNumber { get; init; }

    /// <summary>
    /// The raw line text, preserved for a malformed line so nothing a reviewer typed is lost even when the
    /// line cannot be parsed (rule 5).
    /// </summary>
    public string? RawLine { get; init; }

    /// <summary>The id of the record this is about, when it parsed far enough to have one.</summary>
    public string? RecordId { get; init; }

    /// <summary>The id the record pointed at, for target- and chain-scoped diagnostics.</summary>
    public string? TargetId { get; init; }

    /// <summary>
    /// The record itself, so a caller can surface a retained-but-not-applied record (an unknown op, a
    /// rejected retract, an orphan) rather than merely counting it.
    /// </summary>
    public ReviewRecord? Record { get; init; }
}
