using Markdig.Extensions.CustomContainers;
using Markdig.Syntax;

namespace Charter.Core;

/// <summary>
/// The nested-directive lint (Charter #203): <c>:::</c> containers that the RENDERER draws live but the BLOCK
/// MODEL cannot see, because <see cref="BlockDocument.Parse"/> yields top-level nodes only.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect is a divergence, not a nesting.</b> <c>CharterContainerRenderer</c> renders a container
/// wherever Markdig parsed it, and <c>:::note</c> / <c>:::warn</c> / <c>:::comparison</c> descend into their
/// children — so a <c>:::question</c> inside a <c>::::note</c> is drawn as a real, answerable form with a
/// submit control and a <c>data-question-id</c>, while <c>needsHuman</c> reads false, the flatten emits its raw
/// JSON body as blockquoted prose, and no answer to it can ever be folded back. Every downstream contract
/// (<see cref="HeadlessRecord"/>, <see cref="HandoffGate"/>, <see cref="SourceMap"/>, the duplicate-id lint) is
/// built on the block model, so all of them are blind to it at once.
/// </para>
/// <para>
/// <b>The predicate is "does this render LIVE", never "is this nested".</b> A structural
/// parent-is-not-the-document test would also flag a <c>:::question</c> inside <c>:::custom-html</c> — which
/// renders as inert prose, asserts nothing false, and is the author's own markup by decree. See
/// <c>CharterMarkdown.RendersChildren</c>, which this and the renderer's dispatch both read, for why that decree
/// must not be contradicted from the C# side.
/// </para>
/// <para>
/// <b>The chain is walked, not just the immediate parent.</b> A <c>:::question</c> inside a <c>:::note</c>
/// inside a <c>:::custom-html</c> is still inert, so every link from the container up to the document must be
/// one the renderer descends through: a child-rendering container, or a CommonMark container Markdig descends
/// through anyway (a list item, a blockquote). Blockquote nesting is in scope and behaves identically —
/// verified to render a live form.
/// </para>
/// <para>
/// <b>What this lint deliberately does NOT do.</b> It reports; it changes no walk. The block model, the anchor
/// assignment, the source map and the flatten all stay top-level-only, and a nested block still carries no
/// anchor (#166). Making the model descend honestly requires excising the nested span from the enclosing
/// container's <c>RawContent</c> — and <see cref="Block.Id"/> is a hash of <c>RawContent</c>, so every
/// containing block would re-id and every annotation on it would orphan. That re-id is the cost that settled it.
/// </para>
/// </remarks>
public static class NestedDirectiveLint
{
    /// <summary>
    /// Every <c>:::</c> directive in <paramref name="markdown"/> that renders live but is invisible to the block
    /// model, in document order. Empty for a plan whose directives are all top-level — which is every correct
    /// plan, so this lint's base rate on a healthy document is zero.
    /// </summary>
    public static IReadOnlyList<NestedDirective> Find(string markdown)
        => string.IsNullOrEmpty(markdown)
            ? Array.Empty<NestedDirective>()
            : Find(CharterMarkdown.ParseDocument(markdown));

    /// <summary>
    /// The same lint over an ALREADY-PARSED document, so <see cref="PlanWalk"/> can run it on the one walk
    /// <see cref="PlanInventory"/> promises rather than parsing a fourth time. Line numbers therefore come from
    /// the same parse as the anchor assignment beside them.
    /// </summary>
    internal static IReadOnlyList<NestedDirective> Find(MarkdownDocument document)
    {
        var found = new List<NestedDirective>();
        foreach (var node in document)
        {
            // Only strictly BELOW a top-level node: a top-level container is a Block, which is the whole point.
            if (node is ContainerBlock container)
            {
                CollectNested(container, found);
            }
        }

        return found;
    }

    /// <summary>
    /// The <see cref="HeadlessNoteKind"/> a nested directive of <paramref name="kind"/> is recorded under — the
    /// ONE mapping, read by <see cref="PlanInventory"/> and (through the note it produces) by
    /// <see cref="HandoffGate"/>.
    /// </summary>
    /// <remarks>
    /// <b>Four tokens, not one.</b> The tiers differ — a nested question escalates
    /// <see cref="PlanInventory.NeedsHuman"/>, a nested diff or unknown directive blocks strict handoff without
    /// escalating, and the rest are warnings — and <see cref="HandoffGate.Evaluate"/> switches on
    /// <c>note.Kind</c> while <c>unattended.md</c> tells consumers to branch on <c>kind</c>. One token carrying
    /// two tiers would make the gate's verdict unreproducible from the record.
    /// </remarks>
    public static HeadlessNoteKind NoteKindFor(BlockKind kind) => kind switch
    {
        BlockKind.Question => HeadlessNoteKind.NestedQuestion,
        BlockKind.Diff => HeadlessNoteKind.NestedDiff,
        BlockKind.Unknown => HeadlessNoteKind.NestedUnknownDirective,
        _ => HeadlessNoteKind.NestedDirective,
    };

    /// <summary>Every live-rendered <c>:::</c> container strictly below <paramref name="parent"/>, depth-first
    /// in document order.</summary>
    private static void CollectNested(ContainerBlock parent, List<NestedDirective> found)
    {
        foreach (var child in parent)
        {
            if (child is CustomContainer nested && RendersLive(nested))
            {
                found.Add(new NestedDirective(
                    CharterMarkdown.ClassifyContainer(nested),
                    nested.Info?.Trim() ?? string.Empty,
                    CharterMarkdown.StartLine(nested)));
            }

            if (child is ContainerBlock deeper)
            {
                CollectNested(deeper, found);
            }
        }
    }

    /// <summary>
    /// True when every link from <paramref name="container"/> up to the document is one the renderer descends
    /// through, so this container is drawn LIVE rather than swallowed as its parent's body text.
    /// </summary>
    /// <remarks>
    /// The allowed links are exactly two families: a container kind that renders its children
    /// (<c>CharterMarkdown.RendersChildren</c>), and a CommonMark container the HTML renderer descends through
    /// anyway — a list, a list item, a blockquote. Anything else (a <c>:::custom-html</c>, a <c>:::diagram</c>,
    /// a <c>:::diff</c>, an unknown <c>:::foo</c>, a table cell) makes the whole subtree inert, so the walk stops
    /// and reports nothing.
    /// </remarks>
    private static bool RendersLive(CustomContainer container)
    {
        for (var ancestor = container.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is MarkdownDocument)
            {
                return true;
            }

            if (ancestor is ListBlock or ListItemBlock or QuoteBlock)
            {
                continue;
            }

            if (ancestor is CustomContainer outer
                && CharterMarkdown.RendersChildren(CharterMarkdown.ClassifyContainer(outer)))
            {
                continue;
            }

            return false;
        }

        return false;
    }
}

/// <summary>One <c>:::</c> directive that renders live and is invisible to the block model.</summary>
/// <param name="Kind">What the catalog classifies it as — which decides its tier.</param>
/// <param name="Directive">The info string as authored (<c>question</c>, <c>diff</c>, <c>questoin</c>, …), for
/// the warning text. Never parsed by a consumer.</param>
/// <param name="SourceLine">1-based line where the container opens, so a reviewer can go straight to it.</param>
public sealed record NestedDirective(BlockKind Kind, string Directive, int SourceLine);
