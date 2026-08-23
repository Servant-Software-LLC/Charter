namespace Charter.Core;

/// <summary>
/// Everything ONE parse of a plan yields: its top-level blocks joined to their lines and assigned anchor ids,
/// and the nested <c>:::</c> directives that parse hides.
/// </summary>
/// <param name="Blocks">
/// Each top-level block's kind and raw source (what <see cref="BlockDocument"/> reports) TOGETHER with its
/// 1-based start line and its assigned anchor id (what <see cref="SourceMap"/> reports). Sub-anchors (a
/// <c>:::comparison</c>'s rows, a <c>:::diff</c>'s lines) are NOT here — they are anchor slots, not blocks, and
/// <see cref="SourceMap"/> already registers every one of them.
/// </param>
/// <param name="NestedDirectives">
/// The <c>:::</c> containers that render LIVE but are not blocks (Charter #203), in document order. They ride
/// this walk rather than a fourth <c>ParseDocument</c> so their line numbers come from the same parse as the
/// anchor assignment beside them.
/// </param>
internal sealed record PlanWalkResult(
    IReadOnlyList<(BlockKind Kind, string RawContent, int StartLine, string AnchorId)> Blocks,
    IReadOnlyList<NestedDirective> NestedDirectives);

/// <summary>
/// The joined view of a plan: two existing views plus one lint, all from one walk.
/// </summary>
/// <remarks>
/// This deliberately re-implements nothing. Classification comes from <c>CharterMarkdown.Describe</c>, the
/// line from <c>CharterMarkdown.StartLine</c>, the id from the one shared <see cref="AnchorAssignment"/>
/// pass the renderer and the source map both consume — so an anchor reported here is byte-identical to the
/// <c>id</c> the rendered HTML carries and to the key <see cref="SourceMap"/> registers — and the nested
/// directives from <see cref="NestedDirectiveLint"/>. It exists because a caller that needs "which block, on
/// which line, under which anchor" would otherwise have to correlate <see cref="BlockDocument.Parse"/> against
/// <see cref="SourceMap.Build"/> by hand, and any hand-rolled correlation is a place the two can drift.
/// </remarks>
internal static class PlanWalk
{
    /// <summary>Parse <paramref name="markdown"/> ONCE and report everything that walk can see.</summary>
    public static PlanWalkResult Walk(string markdown)
    {
        markdown ??= string.Empty;
        var document = CharterMarkdown.ParseDocument(markdown);
        var assignment = AnchorAssignment.Build(document, markdown);

        var blocks = new List<(BlockKind, string, int, string)>();
        foreach (var node in document)
        {
            var (kind, rawContent) = CharterMarkdown.Describe(node, markdown);
            var startLine = CharterMarkdown.StartLine(node);
            blocks.Add((kind, rawContent, startLine, assignment.IdForLine(startLine)));
        }

        return new PlanWalkResult(blocks, NestedDirectiveLint.Find(document));
    }
}
