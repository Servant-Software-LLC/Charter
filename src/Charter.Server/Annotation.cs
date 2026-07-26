namespace Charter.Server;

/// <summary>
/// The kind of markdown anchor an <see cref="Annotation"/> is attached to. Charter surfaces three
/// annotation targets in a reviewed plan: a whole rendered <see cref="Element"/> (a block), a
/// <see cref="TextRange"/> selection inside a block, and a <see cref="DiagramNode"/> inside a rendered
/// diagram.
/// </summary>
public enum AnnotationKind
{
    /// <summary>A whole rendered block element, addressed by its stable block id.</summary>
    Element,

    /// <summary>A selected text range within a block.</summary>
    TextRange,

    /// <summary>A node inside a rendered diagram.</summary>
    DiagramNode,
}

/// <summary>
/// Whether an <see cref="Annotation"/>'s <see cref="Annotation.AnchorId"/> still resolves to a block in the
/// plan. Reported explicitly on the wire so the agent is TOLD when the block a note points at has changed,
/// instead of having to infer it from a null line (Charter #49).
/// </summary>
public enum AnchorStatus
{
    /// <summary>The anchor resolved; <see cref="Annotation.SourceLine"/> is the line to edit.</summary>
    Resolved,

    /// <summary>
    /// The anchor resolves to nothing — the block the reviewer commented on was edited or removed, so there
    /// is no line to hand over. <see cref="Annotation.Quote"/> / <see cref="Annotation.NodeId"/> and the note
    /// itself are the recovery hints for locating what the reviewer meant.
    /// </summary>
    Orphaned,
}

/// <summary>
/// A single reviewer annotation queued in the <see cref="AnnotationStore"/>: an opaque <paramref name="Id"/>,
/// the <paramref name="Kind"/> of target, the <paramref name="AnchorId"/> it is attached to, and the
/// reviewer's <paramref name="Note"/>. <paramref name="SourceLine"/> is the resolved 1-based markdown source
/// line, or <c>null</c> when the anchor does not resolve. The remaining fields are the optional sub-part
/// fidelity payload, carried verbatim from the submission through the drain so the downstream agent can tell
/// WHICH part of the block was flagged: <paramref name="Quote"/> / <paramref name="Start"/> /
/// <paramref name="End"/> for a text-range selection, and <paramref name="NodeId"/> for a diagram node. All
/// are <c>null</c> for a whole-block (element) annotation.
/// </summary>
/// <remarks>
/// <b>The stored <paramref name="SourceLine"/> is a submit-time snapshot and is NOT what the agent receives.</b>
/// It backs the reviewer's in-page panel, which is looking at the page as rendered when the note was taken.
/// The handoff value is re-resolved at DRAIN time by <see cref="AnchorResolution"/> against the plan as it is
/// then, because any edit above the block — including <c>poll --apply</c>'s own write — moves it (Charter #49).
/// </remarks>
/// <param name="Id">Opaque, per-annotation identifier.</param>
/// <param name="Kind">Which kind of anchor the annotation targets.</param>
/// <param name="AnchorId">The stable anchor id the annotation is attached to.</param>
/// <param name="Note">The reviewer's free-text note.</param>
/// <param name="SourceLine">Resolved 1-based markdown source line, or <c>null</c> when unresolved.</param>
/// <param name="Quote">Text-range only: the selected text within the block, or <c>null</c>.</param>
/// <param name="Start">Text-range only: the selection's start offset within the block, or <c>null</c>.</param>
/// <param name="End">Text-range only: the selection's end offset within the block, or <c>null</c>.</param>
/// <param name="NodeId">Diagram-node only: the flagged node's identity within the diagram, or <c>null</c>.</param>
public sealed record Annotation(
    string Id,
    AnnotationKind Kind,
    string AnchorId,
    string Note,
    int? SourceLine = null,
    string? Quote = null,
    int? Start = null,
    int? End = null,
    string? NodeId = null)
{
    /// <summary>
    /// Whether <see cref="AnchorId"/> resolved when <see cref="SourceLine"/> was last computed. DERIVED from
    /// <see cref="SourceLine"/> rather than stored alongside it, so the named status and the line can never
    /// disagree — one is the other, said out loud. It serializes as <c>anchorStatus</c> (<c>"resolved"</c> /
    /// <c>"orphaned"</c>) on every annotation the API emits; being derived, it is simply ignored when an
    /// annotation is read back (from the drain or from the durability sidecar) and is recomputed from the line.
    /// </summary>
    public AnchorStatus AnchorStatus => SourceLine is null ? AnchorStatus.Orphaned : AnchorStatus.Resolved;
}
