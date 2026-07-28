namespace Charter.Core;

/// <summary>
/// The joined view of a plan's top-level blocks: each block's kind and raw source (what
/// <see cref="BlockDocument"/> reports) TOGETHER with its 1-based start line and its assigned anchor id (what
/// <see cref="SourceMap"/> reports). Two existing views, one walk.
/// </summary>
/// <remarks>
/// This deliberately re-implements nothing. Classification comes from <c>CharterMarkdown.Describe</c>, the
/// line from <c>CharterMarkdown.StartLine</c>, and the id from the one shared <see cref="AnchorAssignment"/>
/// pass the renderer and the source map both consume — so an anchor reported here is byte-identical to the
/// <c>id</c> the rendered HTML carries and to the key <see cref="SourceMap"/> registers. It exists because a
/// caller that needs "which block, on which line, under which anchor" would otherwise have to correlate
/// <see cref="BlockDocument.Parse"/> against <see cref="SourceMap.Build"/> by hand, and any hand-rolled
/// correlation is a place the two can drift.
/// </remarks>
internal static class PlanWalk
{
    /// <summary>
    /// Walk <paramref name="markdown"/>'s top-level blocks in document order. Sub-anchors (a
    /// <c>:::comparison</c>'s rows, a <c>:::diff</c>'s lines) are NOT yielded here — they are anchor slots,
    /// not blocks, and <see cref="SourceMap"/> already registers every one of them.
    /// </summary>
    public static IEnumerable<(BlockKind Kind, string RawContent, int StartLine, string AnchorId)> Blocks(
        string markdown)
    {
        markdown ??= string.Empty;
        var document = CharterMarkdown.ParseDocument(markdown);
        var assignment = AnchorAssignment.Build(document, markdown);

        foreach (var node in document)
        {
            var (kind, rawContent) = CharterMarkdown.Describe(node, markdown);
            var startLine = CharterMarkdown.StartLine(node);
            yield return (kind, rawContent, startLine, assignment.IdForLine(startLine));
        }
    }
}
