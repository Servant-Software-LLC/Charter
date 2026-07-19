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
/// line — left <c>null</c> here; the annotation-API task fills it via <c>SourceMap.LineForAnchor</c>.
/// </summary>
/// <param name="Id">Opaque, per-annotation identifier.</param>
/// <param name="Kind">Which kind of anchor the annotation targets.</param>
/// <param name="AnchorId">The stable anchor id the annotation is attached to.</param>
/// <param name="Note">The reviewer's free-text note.</param>
/// <param name="SourceLine">Resolved 1-based markdown source line, or <c>null</c> when unresolved.</param>
public sealed record Annotation(string Id, AnnotationKind Kind, string AnchorId, string Note, int? SourceLine = null);
