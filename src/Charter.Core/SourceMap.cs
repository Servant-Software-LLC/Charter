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
    /// Build the anchor map for the given <paramref name="markdown"/>: every block's stable
    /// <see cref="Block.Id"/> maps to its 1-based markdown start line, and — the sub-block anchor model —
    /// each per-sub-element anchor of a <c>:::comparison</c> (per option row) or a <c>:::diff</c> (per diff
    /// line) additionally maps to THAT sub-element's own line. Because each anchor is derived from its own
    /// content, re-building the map after an unrelated edit re-resolves the same anchor to its new line, and
    /// one sub-element's anchor is unaffected by edits to its siblings.
    /// </summary>
    public static SourceMap Build(string markdown)
    {
        markdown ??= string.Empty;
        var document = CharterMarkdown.ParseDocument(markdown);

        var startLineByAnchor = new Dictionary<string, int>(StringComparer.Ordinal);

        // "First occurrence wins" for every anchor — block-level and sub-anchor alike — so a duplicated
        // block or row resolves to where it first appears.
        void Register(string anchor, int line)
        {
            if (!startLineByAnchor.ContainsKey(anchor))
            {
                startLineByAnchor[anchor] = line;
            }
        }

        foreach (var node in document)
        {
            var (_, rawContent) = CharterMarkdown.Describe(node, markdown);
            Register(Block.StableId(rawContent), CharterMarkdown.StartLine(node));

            // Descend into per-sub-element containers (:::comparison rows, :::diff lines) and register each
            // sub-anchor at its own line, so LineForAnchor(subId) resolves to that sub-element — not merely
            // the block's start line.
            foreach (var (_, subAnchor, line) in CharterMarkdown.SubAnchors(node, markdown))
            {
                Register(subAnchor, line);
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
