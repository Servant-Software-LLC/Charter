namespace Charter.Core;

/// <summary>
/// Maps each block's content-derived stable <see cref="Block.Id"/> to its markdown line range, so an
/// annotation anchored to a rendered block resolves back to the exact source lines the agent edits.
/// This is the deepest correctness seam in Charter: source (markdown) and render (HTML) are split, and
/// content-derived anchors let an annotation survive re-render of unrelated blocks.
/// </summary>
/// <remarks>STUB (TDD red). Building the map lands in a later task.</remarks>
public sealed class SourceMap
{
    /// <summary>
    /// Build the anchor map (block stable <see cref="Block.Id"/> to markdown line range) for the given
    /// <paramref name="markdown"/>. NOT YET IMPLEMENTED.
    /// </summary>
    public static SourceMap Build(string markdown) => throw new NotImplementedException();

    /// <summary>
    /// Resolve an anchor to the 1-based starting markdown line of the block whose stable
    /// <see cref="Block.Id"/> equals <paramref name="anchorId"/>, or <see langword="null"/> if no block
    /// carries that id. NOT YET IMPLEMENTED.
    /// </summary>
    public int? LineForAnchor(string anchorId) => throw new NotImplementedException();
}
