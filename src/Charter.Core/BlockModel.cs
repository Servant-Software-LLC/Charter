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
    /// the result never varies from run to run. NOT YET IMPLEMENTED.
    /// </summary>
    public static string StableId(string content) => throw new NotImplementedException();
}

/// <summary>
/// A Charter deliverable parsed into ordered <see cref="Block"/>s.
/// </summary>
/// <remarks>STUB (TDD red). Markdown parsing lands in a later task.</remarks>
public sealed class BlockDocument
{
    /// <summary>The document's blocks, in source order.</summary>
    public IReadOnlyList<Block> Blocks => throw new NotImplementedException();

    /// <summary>Parse markdown into ordered <see cref="Block"/>s. NOT YET IMPLEMENTED.</summary>
    public static BlockDocument Parse(string markdown) => throw new NotImplementedException();
}
