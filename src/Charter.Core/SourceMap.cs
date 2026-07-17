namespace Charter.Core;

/// <summary>
/// Maps each block's content-derived stable <see cref="Block.Id"/> to its markdown line range, so an
/// annotation anchored to a rendered block resolves back to the exact source lines the agent edits.
/// This is the deepest correctness seam in Charter: source (markdown) and render (HTML) are split, and
/// content-derived anchors let an annotation survive re-render of unrelated blocks.
/// </summary>
public sealed class SourceMap
{
    private readonly IReadOnlyDictionary<string, int> _startLineByAnchor;

    private SourceMap(IReadOnlyDictionary<string, int> startLineByAnchor)
        => _startLineByAnchor = startLineByAnchor;

    /// <summary>
    /// Build the anchor map (block stable <see cref="Block.Id"/> to its 1-based markdown start line) for
    /// the given <paramref name="markdown"/>. Because each anchor is derived from its block's own content,
    /// re-building the map after an unrelated edit re-resolves the same anchor to the block's new line.
    /// </summary>
    public static SourceMap Build(string markdown)
    {
        markdown ??= string.Empty;
        var document = CharterMarkdown.ParseDocument(markdown);

        var startLineByAnchor = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in document)
        {
            var (_, rawContent) = CharterMarkdown.Describe(node, markdown);
            var id = Block.StableId(rawContent);

            // Keep the first occurrence so a duplicated block resolves to where it first appears.
            if (!startLineByAnchor.ContainsKey(id))
            {
                startLineByAnchor[id] = CharterMarkdown.StartLine(node);
            }
        }

        return new SourceMap(startLineByAnchor);
    }

    /// <summary>
    /// Resolve an anchor to the 1-based starting markdown line of the block whose stable
    /// <see cref="Block.Id"/> equals <paramref name="anchorId"/>, or <see langword="null"/> if no block
    /// carries that id.
    /// </summary>
    public int? LineForAnchor(string anchorId)
        => anchorId is not null && _startLineByAnchor.TryGetValue(anchorId, out var line) ? line : null;
}
