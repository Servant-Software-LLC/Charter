using System.Security.Cryptography;
using System.Text;
using Markdig;
using Markdig.Extensions.CustomContainers;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using MarkdigBlock = Markdig.Syntax.Block;

namespace Charter.Core;

/// <summary>
/// The kind of a parsed <see cref="Block"/>: either a CommonMark primitive (prose, heading, list,
/// table, fenced code) or a <c>:::</c> directive container from the Charter block catalog.
/// </summary>
public enum BlockKind
{
    /// <summary>A CommonMark paragraph.</summary>
    Prose,

    /// <summary>An ATX/setext heading.</summary>
    Heading,

    /// <summary>An ordered or unordered list.</summary>
    List,

    /// <summary>A GFM pipe table.</summary>
    Table,

    /// <summary>A fenced (or indented) code block.</summary>
    Code,

    /// <summary>A <c>:::note</c> callout container.</summary>
    Note,

    /// <summary>A <c>:::warn</c> callout container.</summary>
    Warn,

    /// <summary>A <c>:::diagram</c> container.</summary>
    Diagram,

    /// <summary>A <c>:::comparison</c> container.</summary>
    Comparison,

    /// <summary>A <c>:::question</c> container.</summary>
    Question,

    /// <summary>A <c>:::diff</c> container.</summary>
    Diff,
}

/// <summary>
/// One block of a Charter deliverable: a directive or prose block carrying its <see cref="Kind"/>, the
/// raw markdown that produced it, and a content-derived stable <see cref="Id"/> that a human annotation
/// anchors to.
/// </summary>
/// <remarks>
/// STUB (TDD red). The stable-id derivation — <see cref="StableId(string)"/> — is the behavioral seam
/// under test; a later task fills in the real logic. Every behavioral member throws
/// <see cref="NotImplementedException"/> for now so the authored tests compile and fail.
/// </remarks>
public sealed record Block(BlockKind Kind, string RawContent)
{
    /// <summary>
    /// The content-derived stable identifier for this block — the anchor a human annotation binds to.
    /// Derived purely from content, so it survives edits to <em>other</em> blocks (unlike a positional
    /// selector).
    /// </summary>
    public string Id => StableId(RawContent);

    /// <summary>
    /// The behavioral seam under test: derive a deterministic, content-derived id from a block's
    /// normalized content. Same content yields the same id; different content yields a different id;
    /// the result never varies from run to run — it is a pure SHA-256 of the normalized content, so it
    /// is stable across process runs and unaffected by edits to <em>other</em> blocks.
    /// </summary>
    public static string StableId(string content)
    {
        var normalized = CharterMarkdown.Normalize(content ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));

        // A 10-byte (80-bit) prefix, hex-encoded, is collision-safe for any realistic document and keeps
        // the anchor short. The leading letter keeps it a valid HTML id under every HTML version.
        return "b" + Convert.ToHexString(hash, 0, 10).ToLowerInvariant();
    }
}

/// <summary>
/// A Charter deliverable parsed into ordered <see cref="Block"/>s.
/// </summary>
public sealed class BlockDocument
{
    private BlockDocument(IReadOnlyList<Block> blocks) => Blocks = blocks;

    /// <summary>The document's blocks, in source order.</summary>
    public IReadOnlyList<Block> Blocks { get; }

    /// <summary>
    /// Parse markdown into ordered <see cref="Block"/>s. Each top-level CommonMark block (plus each
    /// <c>:::</c> directive container) becomes one <see cref="Block"/> whose <see cref="Block.RawContent"/>
    /// is the exact source text it spans — so its <see cref="Block.Id"/> depends only on its own content.
    /// </summary>
    public static BlockDocument Parse(string markdown)
    {
        markdown ??= string.Empty;
        var document = CharterMarkdown.ParseDocument(markdown);

        var blocks = new List<Block>();
        foreach (var node in document)
        {
            var (kind, rawContent) = CharterMarkdown.Describe(node, markdown);
            blocks.Add(new Block(kind, rawContent));
        }

        return new BlockDocument(blocks);
    }
}

/// <summary>
/// Shared Markdig parsing used by the block model, the renderer, and the source map so all three agree
/// on the same top-level blocks, their <see cref="BlockKind"/>, and the raw source text each spans — the
/// single input to a block's content-derived <see cref="Block.Id"/>.
/// </summary>
internal static class CharterMarkdown
{
    /// <summary>
    /// The one pipeline every seam parses with. Pipe tables and <c>:::</c> custom containers are enabled;
    /// auto-identifiers are deliberately NOT — heading ids are the stable, content-derived anchors, and
    /// Markdig's slug-based ids would overwrite them.
    /// </summary>
    internal static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseCustomContainers()
        .Build();

    /// <summary>Parse <paramref name="markdown"/> into a Markdig document using the shared pipeline.</summary>
    internal static MarkdownDocument ParseDocument(string markdown)
        => Markdown.Parse(markdown, Pipeline);

    /// <summary>Classify a top-level Markdig block and capture the raw source text it spans.</summary>
    internal static (BlockKind Kind, string RawContent) Describe(MarkdigBlock node, string markdown)
    {
        var kind = node switch
        {
            HeadingBlock => BlockKind.Heading,
            ListBlock => BlockKind.List,
            Table => BlockKind.Table,
            CustomContainer container => ClassifyContainer(container),
            CodeBlock => BlockKind.Code,
            _ => BlockKind.Prose,
        };

        return (kind, RawContentOf(node, markdown));
    }

    /// <summary>The 1-based markdown line where <paramref name="node"/> starts.</summary>
    internal static int StartLine(MarkdigBlock node) => node.Line + 1;

    /// <summary>
    /// Classify a <c>:::</c> custom container by its info string: <c>diagram</c> → a Mermaid
    /// <see cref="BlockKind.Diagram"/>, <c>warn</c> → a <see cref="BlockKind.Warn"/> callout, and everything
    /// else (including <c>note</c>) → a <see cref="BlockKind.Note"/> callout. Adds the M4 diagram kind while
    /// leaving the existing note/warn behavior untouched.
    /// </summary>
    private static BlockKind ClassifyContainer(CustomContainer container)
    {
        if (IsDiagram(container))
        {
            return BlockKind.Diagram;
        }

        return IsWarn(container) ? BlockKind.Warn : BlockKind.Note;
    }

    private static bool IsDiagram(CustomContainer container)
        => string.Equals(container.Info?.Trim(), "diagram", StringComparison.OrdinalIgnoreCase);

    private static bool IsWarn(CustomContainer container)
        => string.Equals(container.Info?.Trim(), "warn", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The exact source text a block spans. Content-derived ids need only that this is deterministic for
    /// the same source block; using the block's own <see cref="MarkdownObject.Span"/> guarantees an edit
    /// to an unrelated block leaves this text — and therefore the id — untouched.
    /// </summary>
    private static string RawContentOf(MarkdigBlock node, string markdown)
    {
        var span = node.Span;
        if (span.IsEmpty || markdown.Length == 0)
        {
            return string.Empty;
        }

        var start = Math.Clamp(span.Start, 0, markdown.Length - 1);
        var end = Math.Clamp(span.End, start, markdown.Length - 1);
        return markdown.Substring(start, end - start + 1);
    }

    /// <summary>
    /// Normalize block content before hashing: unify line endings and trim surrounding whitespace so the
    /// id ignores incidental CRLF / trailing-space differences that carry no semantic change.
    /// </summary>
    internal static string Normalize(string content)
        => content.Replace("\r\n", "\n", StringComparison.Ordinal)
                  .Replace('\r', '\n')
                  .Trim();
}
