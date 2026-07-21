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
/// A single reviewer annotation queued in the <see cref="AnnotationStore"/>: an opaque <paramref name="Id"/>,
/// the <paramref name="Kind"/> of target, the <paramref name="AnchorId"/> it is attached to, and the
/// reviewer's <paramref name="Note"/>. <paramref name="SourceLine"/> is the resolved 1-based markdown source
/// line — left <c>null</c> here; the annotation-API task fills it via <c>SourceMap.LineForAnchor</c>. The
/// remaining fields are the optional sub-part fidelity payload, carried verbatim from the submission through
/// the drain so the downstream agent can tell WHICH part of the block was flagged: <paramref name="Quote"/> /
/// <paramref name="Start"/> / <paramref name="End"/> for a text-range selection, and <paramref name="NodeId"/>
/// for a diagram node. All are <c>null</c> for a whole-block (element) annotation.
/// </summary>
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
    string? NodeId = null);
