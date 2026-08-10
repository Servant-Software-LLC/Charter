using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Markdig;
using Markdig.Extensions.CustomContainers;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Yaml;
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

    /// <summary>A <c>:::custom-html</c> container — the sanctioned raw-HTML escape hatch.</summary>
    CustomHtml,

    /// <summary>
    /// An unrecognized <c>:::foo</c> directive — a typo or an unlisted container that matches NO catalog
    /// block. It is the deliberate else-branch fallback of <see cref="CharterMarkdown"/>'s container
    /// classification, NOT a catalog member: it exists so an unknown directive surfaces VISIBLY (a
    /// rendered "unknown directive" element, a flagged handoff line) instead of silently masquerading as a
    /// <see cref="Note"/>. The drift test binds the real catalog and excludes this fallback.
    /// </summary>
    Unknown,
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
/// The single, document-wide id-assignment pass that BOTH the renderer and the <see cref="SourceMap"/>
/// consume, so a discriminated id can never differ between the two paths.
/// </summary>
/// <remarks>
/// <para>
/// A pure <see cref="Block.StableId(string)"/> is content-derived, so it ALIASES when the same content
/// recurs in one document — two identical prose blocks, two identical <c>:::diff</c> added-lines, two
/// identical <c>:::comparison</c> rows all hash to the same id, and an annotation on the second occurrence
/// would resolve to the first occurrence's source line (silent misattribution). This pass walks every
/// anchor slot ONCE, in canonical document order, and gives every occurrence of DUPLICATED content its own
/// discriminated id. Content that occurs exactly once keeps the pure content-derived id, so a document with
/// no duplicates is byte-identical to a bare per-slot hash and every anchor still survives edits to unrelated
/// blocks (invariant 2). Duplicate detection is GLOBAL across all slot kinds — a prose block and a diff line
/// that hash alike must still get distinct ids. Both consumers look a slot's id up by that slot's intrinsic
/// 1-based markdown line; those line numbers are identical across the renderer's and the source map's
/// independent (but deterministic) parses, so the two paths read the SAME assignment and cannot drift.
/// </para>
/// <para>
/// <b>The discriminator is CONTEXTUAL, never positional (Charter #50).</b> It used to be the occurrence index
/// (<c>-2</c>, <c>-3</c>, …), which is a function of how many identical blocks PRECEDE a slot: inserting an
/// identical block earlier renumbered every later duplicate, so an existing annotation on <c>bH-2</c>
/// resolved — successfully and silently — to a DIFFERENT block. That is misattribution, strictly worse than
/// orphaning, because nothing detects it. The discriminator is now a hash of the PRECEDING slot's assigned id
/// (plus the length of this slot's run of consecutive identical siblings), so a duplicate's identity is
/// derived from where it sits in the content, not from how many copies came before it: an identical block
/// inserted in a DIFFERENT neighbourhood gets a different id in the first place and leaves the existing
/// duplicates untouched.
/// </para>
/// <para>
/// <b>The tradeoff, stated plainly.</b> Any context-sensitive scheme means a duplicate's id can change when a
/// NEIGHBOUR changes — editing the block above a duplicated block orphans an annotation on it, and so does
/// growing/shrinking a run of adjacent identical siblings (the run length is folded in deliberately, so a run
/// that changes size invalidates its anchors instead of sliding them onto the wrong sibling). Introducing a
/// duplicate of a previously-unique block likewise re-ids ALL its copies, because "is this content
/// duplicated?" is what decides whether a slot is discriminated at all. Every one of those is an ORPHAN: it
/// resolves to nothing, is reported as <c>anchorStatus: "orphaned"</c> on the drain, is rendered as an orphan
/// in the review panel, and carries the reviewer's quote as a recovery hint. A wrong attribution is none of
/// those. Orphaning more often is the price of never misattributing, and it is worth paying. Consecutive
/// identical siblings with identical surroundings remain genuinely interchangeable — no content-derived
/// scheme can keep those stable — so there the guarantee is the weaker one: orphan, never re-point.
/// </para>
/// </remarks>
internal sealed class AnchorAssignment
{
    // The document-start sentinel: the context of the very first slot. At most one slot has it, so it cannot
    // make two discriminated ids collide.
    private const string DocumentStart = "";

    private readonly IReadOnlyDictionary<int, string> _idByLine;

    private AnchorAssignment(IReadOnlyDictionary<int, string> idByLine) => _idByLine = idByLine;

    /// <summary>
    /// Walk <paramref name="document"/> once and assign every anchor slot its unique, duplicate-discriminated
    /// id. Deterministic in <paramref name="markdown"/>, so the renderer and the <see cref="SourceMap"/> — each
    /// calling this over its own parse of the same source — obtain an identical assignment.
    /// </summary>
    public static AnchorAssignment Build(MarkdownDocument document, string markdown)
    {
        markdown ??= string.Empty;

        var slots = Slots(document, markdown).ToList();

        // Pass 1 — which base ids are duplicated at all. Only those get a discriminator; unique content keeps
        // its pure hash, so a duplicate-free document is unaffected by any of this.
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, baseId) in slots)
        {
            occurrences[baseId] = occurrences.TryGetValue(baseId, out var seen) ? seen + 1 : 1;
        }

        // Pass 2 — assign, chaining each duplicate's discriminator off the PREVIOUS slot's assigned id.
        // Uniqueness is guaranteed by induction: two slots sharing a base id have distinct predecessors, whose
        // assigned ids are distinct by the same argument (the first slot's context is the unique sentinel), so
        // their discriminators differ. A pure base id never contains '-', so a discriminated id can never
        // collide with another slot's pure id either.
        var runLengths = RunLengths(slots);
        var idByLine = new Dictionary<int, string>();
        var previousId = DocumentStart;
        for (var i = 0; i < slots.Count; i++)
        {
            var (line, baseId) = slots[i];
            var id = occurrences[baseId] == 1
                ? baseId
                : baseId + "-" + Discriminator(previousId, runLengths[i]);

            idByLine[line] = id;
            previousId = id;
        }

        return new AnchorAssignment(idByLine);
    }

    /// <summary>
    /// For each slot, the length of the maximal run of CONSECUTIVE slots sharing its base id — one linear pass.
    /// Folded into the discriminator so that growing or shrinking a run of adjacent identical siblings changes
    /// every id in it, turning "the reviewer's note slides onto the sibling next door" (misattribution) into
    /// "the note orphans" (detectable, and reported as such). Adjacent identical siblings are otherwise
    /// indistinguishable by content or context at any depth, so this is the best available answer there.
    /// </summary>
    private static int[] RunLengths(IReadOnlyList<(int Line, string BaseId)> slots)
    {
        var lengths = new int[slots.Count];
        var start = 0;
        for (var i = 1; i <= slots.Count; i++)
        {
            if (i < slots.Count && string.Equals(slots[i].BaseId, slots[start].BaseId, StringComparison.Ordinal))
            {
                continue;
            }

            for (var j = start; j < i; j++)
            {
                lengths[j] = i - start;
            }

            start = i;
        }

        return lengths;
    }

    /// <summary>
    /// The contextual discriminator appended to a duplicated slot's base id: a hash of the preceding slot's
    /// ASSIGNED id and this slot's run length. Both inputs are already-normalized ASCII (a base id is hex, a
    /// run length is a number), so — like <see cref="Block.StableId(string)"/>, which normalizes CRLF before
    /// hashing — the result is unaffected by the checkout's line endings.
    /// </summary>
    private static string Discriminator(string previousAssignedId, int runLength)
    {
        var seed = previousAssignedId + "|" + runLength.ToString(CultureInfo.InvariantCulture);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));

        // 8 bytes (64 bits). The induction above rules out two slots sharing a base id AND a context, so this
        // only has to survive the birthday bound across one document's duplicates — comfortably, at 64 bits,
        // for any document a human reviews. (A collision would let two slots share an id, which the source map
        // would resolve to the first — the very misattribution this whole scheme exists to prevent.)
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>The assigned (possibly duplicate-discriminated) id of the anchor slot that starts at
    /// <paramref name="line"/> (its 1-based markdown line).</summary>
    public string IdForLine(int line) => _idByLine[line];

    /// <summary>
    /// Every anchor slot in canonical document order — the exact union both consumers already produce: for
    /// each top-level node, its block slot (start line + content-derived base id) followed by its sub-anchor
    /// slots (a <c>:::comparison</c>'s rows, a <c>:::diff</c>'s lines) in <see cref="CharterMarkdown.SubAnchors"/>
    /// order. Walked once here so duplicate detection — and therefore discrimination — is single-sourced, and
    /// so the "preceding slot" a duplicate's discriminator chains off is one agreed sequence rather than two.
    /// Each slot has a distinct 1-based line (a block starts before its sub-elements, sibling sub-elements sit
    /// on their own lines), so keying the assignment by line identifies each slot uniquely.
    /// </summary>
    private static IEnumerable<(int Line, string BaseId)> Slots(MarkdownDocument document, string markdown)
    {
        foreach (var node in document)
        {
            var (_, rawContent) = CharterMarkdown.Describe(node, markdown);
            yield return (CharterMarkdown.StartLine(node), Block.StableId(rawContent));

            foreach (var (_, subAnchor, line) in CharterMarkdown.SubAnchors(node, markdown))
            {
                yield return (line, subAnchor);
            }
        }
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
    /// Markdig's slug-based ids would overwrite them. Raw HTML is DISABLED (<see cref="MarkdownPipelineBuilderExtensions.DisableHtml"/>):
    /// raw HTML blocks/inline in plan markdown are ESCAPED to visible text rather than passed through live,
    /// closing the stored-XSS / phone-home surface. The block catalog is the only sanctioned rich surface;
    /// the one deliberate raw-HTML escape hatch is <c>:::custom-html</c>, which <see cref="CharterContainerRenderer"/>
    /// renders verbatim. The vendored Mermaid runtime and the serve-time SDK are injected AFTER render (not via
    /// markdown HTML), so escaping raw markdown HTML never touches them.
    /// <para>
    /// <see cref="MarkdownPipelineBuilderExtensions.UseSoftlineBreakAsHardlineBreak"/> is enabled (Charter #44):
    /// a bare newline inside a paragraph is a CommonMark SOFT break, which HTML's default
    /// <c>white-space: normal</c> then collapses — so an evidently-intentional multi-line block (a metadata
    /// header, an address, a list of names) renders as one run-on line. That is spec-correct and still wrong for
    /// a tool whose point is that the reviewer sees what the author intended, so Charter follows GFM (and the
    /// author's eye) and renders each newline as a visual break. This is a RENDERER-level extension: it changes
    /// HTML output only, never the parsed model — so content-derived <see cref="Block.Id"/> anchors, the
    /// <see cref="SourceMap"/>, and the plain-CommonMark handoff are all unaffected, and no existing annotation
    /// re-anchors because of it.
    /// </para>
    /// </summary>
    internal static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .UsePipeTables()
        .UseCustomContainers()
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml()
        .Build();

    /// <summary>
    /// Parse <paramref name="markdown"/> into a Markdig document using the shared pipeline, then STRIP any YAML
    /// front matter. Front matter is metadata (the <c>charter-format-version</c> marker), not a content block:
    /// removing the <see cref="YamlFrontMatterBlock"/> here makes every seam that traverses the document —
    /// render, <see cref="AnchorAssignment"/>/<see cref="SourceMap"/>, <see cref="BlockDocument"/> and thus the
    /// handoff/export — skip it uniformly (no anchor, never rendered as prose). Removing the node does not shift
    /// any other block's absolute <see cref="MarkdownObject.Span"/>/line, so anchors stay correct.
    /// <see cref="QuestionResolution"/>.<c>Apply</c> splices on the original source string, so the front matter
    /// is preserved there untouched.
    /// </summary>
    internal static MarkdownDocument ParseDocument(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);

        for (var i = document.Count - 1; i >= 0; i--)
        {
            if (document[i] is YamlFrontMatterBlock)
            {
                document.RemoveAt(i);
            }
        }

        return document;
    }

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
    /// Classify a <c>:::</c> custom container by its info string against the reconciled catalog:
    /// <c>diagram</c> → <see cref="BlockKind.Diagram"/>, <c>comparison</c> → <see cref="BlockKind.Comparison"/>,
    /// <c>diff</c> → <see cref="BlockKind.Diff"/>, <c>question</c> → <see cref="BlockKind.Question"/>,
    /// <c>custom-html</c> → <see cref="BlockKind.CustomHtml"/>, <c>warn</c> → <see cref="BlockKind.Warn"/>, and
    /// <c>note</c> → <see cref="BlockKind.Note"/>. Every OTHER info string — a typo or an unlisted directive —
    /// falls to <see cref="BlockKind.Unknown"/> so an unrecognized <c>:::foo</c> surfaces visibly rather than
    /// silently masquerading as a note (Charter #22). <c>note</c> is now matched explicitly because the
    /// else-branch is no longer a note fallback.
    /// </summary>
    internal static BlockKind ClassifyContainer(CustomContainer container)
    {
        if (IsDiagram(container))
        {
            return BlockKind.Diagram;
        }

        if (IsComparison(container))
        {
            return BlockKind.Comparison;
        }

        if (IsDiff(container))
        {
            return BlockKind.Diff;
        }

        if (IsQuestion(container))
        {
            return BlockKind.Question;
        }

        if (IsCustomHtml(container))
        {
            return BlockKind.CustomHtml;
        }

        if (IsWarn(container))
        {
            return BlockKind.Warn;
        }

        if (IsNote(container))
        {
            return BlockKind.Note;
        }

        return BlockKind.Unknown;
    }

    private static bool IsDiagram(CustomContainer container)
        => string.Equals(container.Info?.Trim(), "diagram", StringComparison.OrdinalIgnoreCase);

    private static bool IsComparison(CustomContainer container)
        => string.Equals(container.Info?.Trim(), "comparison", StringComparison.OrdinalIgnoreCase);

    private static bool IsDiff(CustomContainer container)
        => string.Equals(container.Info?.Trim(), "diff", StringComparison.OrdinalIgnoreCase);

    private static bool IsQuestion(CustomContainer container)
        => string.Equals(container.Info?.Trim(), "question", StringComparison.OrdinalIgnoreCase);

    private static bool IsCustomHtml(CustomContainer container)
        => string.Equals(container.Info?.Trim(), "custom-html", StringComparison.OrdinalIgnoreCase);

    private static bool IsWarn(CustomContainer container)
        => string.Equals(container.Info?.Trim(), "warn", StringComparison.OrdinalIgnoreCase);

    private static bool IsNote(CustomContainer container)
        => string.Equals(container.Info?.Trim(), "note", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The reusable sub-anchor descent — the foundation of the sub-block anchor model. For a container that
    /// is annotatable per sub-element — a <c>:::comparison</c> (per option row) or a <c>:::diff</c> (per diff
    /// line) — yield each sub-element paired with its content-derived sub-anchor and its 1-based markdown
    /// line. A sub-anchor is <see cref="Block.StableId(string)"/> of that sub-element's OWN raw source text,
    /// so an annotation on one survives edits to its siblings (content-derived, never positional —
    /// invariant 2). Any other node yields nothing, so both the renderer (which stamps each comparison row's
    /// <c>data-anchor</c>) and the <see cref="SourceMap"/> (which registers every sub-anchor → its line) can
    /// call this uniformly over every top-level node.
    /// </summary>
    internal static IEnumerable<(MarkdigBlock Row, string SubAnchor, int Line)> SubAnchors(MarkdigBlock node, string markdown)
    {
        if (node is not CustomContainer container)
        {
            yield break;
        }

        var kind = ClassifyContainer(container);
        if (kind == BlockKind.Comparison)
        {
            foreach (var row in SubAnchorRows(container))
            {
                var rawLine = SourceLine(markdown, row.Line);
                if (rawLine.Length == 0)
                {
                    continue;
                }

                yield return (row, Block.StableId(rawLine), StartLine(row));
            }
        }
        else if (kind == BlockKind.Diff)
        {
            // A :::diff's annotatable sub-elements are its individual diff LINES, which parse as one
            // paragraph of soft-broken lines rather than child blocks — so the sub-element is a source LINE,
            // handled by DiffLines. It still feeds the SAME (sub-anchor, line) contract: each line's
            // sub-anchor is Block.StableId of that line's OWN trimmed text (marker included).
            foreach (var line in DiffLines(container, markdown))
            {
                yield return (container, Block.StableId(line.Trimmed), line.Line);
            }
        }
    }

    /// <summary>
    /// The annotatable rows of a <c>:::comparison</c> — its option list items. (A <c>:::diff</c>'s
    /// sub-elements are per-line source lines, not child blocks, so they are handled by
    /// <see cref="DiffLines"/> rather than this block-level descent.)
    /// </summary>
    private static IEnumerable<MarkdigBlock> SubAnchorRows(CustomContainer container)
    {
        foreach (var child in container)
        {
            if (child is ListBlock list)
            {
                foreach (var item in list)
                {
                    yield return item;
                }
            }
        }
    }

    /// <summary>
    /// The per-line descent for a <c>:::diff</c> container: yield each diff LINE with its raw source text,
    /// its trimmed text (the input to the line's content-derived sub-anchor), its 1-based markdown line, and
    /// the add/del/context CSS class implied by its leading marker. The diff body parses as a single
    /// paragraph of soft-broken lines, so the sub-element is a source LINE — not a child block like a
    /// comparison row — but it feeds the SAME <see cref="SubAnchors"/> contract and the renderer's per-line
    /// markup, so both agree on exactly one anchor per line.
    /// </summary>
    internal static IEnumerable<(string Raw, string Trimmed, int Line, string CssClass)> DiffLines(CustomContainer container, string markdown)
    {
        if (markdown.Length == 0)
        {
            yield break;
        }

        // The FENCED body form (```diff … ```) is documented as meaning exactly the same thing as the raw form,
        // so it must yield exactly the same lines. Descending by SPAN would not: a FencedCodeBlock's span covers
        // its delimiters, which then render as two annotatable "diff lines" reading ```diff and ``` — content
        // the author never wrote. Descending by the block's LINES instead drops the delimiters and keeps every
        // real line, at its true source position.
        //
        // The fenced form is also the only SAFE body for machine-generated diffs (`charter recap`): a code fence
        // is opaque to the block parser, whereas a raw body made of arbitrary repository content can contain a
        // line that closes this container early, or one that opens a NESTED directive — either of which silently
        // discards the rest of the diff.
        if (container.Count == 1 && container[0] is FencedCodeBlock fenced)
        {
            var contentLine = fenced.Line + 1;   // 0-based; the opening delimiter occupies fenced.Line
            var lines = fenced.Lines;

            // Count, NOT lines.Lines.Length: Markdig hands back a pooled array that is usually longer than the
            // block, and the slots past Count hold stale entries from a previous block.
            for (var i = 0; i < lines.Count; i++)
            {
                var raw = lines.Lines[i].Slice.ToString() ?? string.Empty;
                var trimmedText = raw.Trim();
                if (trimmedText.Length != 0)
                {
                    yield return (raw, trimmedText, contentLine + 1, DiffLineClass(raw));
                }

                contentLine++;
            }

            yield break;
        }

        foreach (var child in container)
        {
            var span = child.Span;
            if (span.IsEmpty)
            {
                continue;
            }

            var start = Math.Clamp(span.Start, 0, markdown.Length - 1);
            var end = Math.Clamp(span.End, start, markdown.Length - 1);
            var text = markdown.Substring(start, end - start + 1)
                               .Replace("\r\n", "\n", StringComparison.Ordinal)
                               .Replace('\r', '\n');

            var rawLines = text.Split('\n');
            for (var i = 0; i < rawLines.Length; i++)
            {
                var raw = rawLines[i];
                var trimmed = raw.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                // child.Line is the 0-based source line of this child's first line; each '\n' in the
                // contiguous slice advances exactly one source line, so line i sits at child.Line + i
                // (rendered 1-based as child.Line + 1 + i).
                yield return (raw, trimmed, child.Line + 1 + i, DiffLineClass(raw));
            }
        }
    }

    /// <summary>
    /// The add/del/context class of a diff line, from its leading unified-diff marker: <c>+</c> → added,
    /// <c>-</c> → removed, anything else (a leading space or bare text) → unchanged context. The marker is
    /// part of the line's content, so an added and a removed line hash to distinct sub-anchors.
    /// </summary>
    private static string DiffLineClass(string raw)
        => raw.Length > 0 && raw[0] == '+' ? "diff-add"
         : raw.Length > 0 && raw[0] == '-' ? "diff-del"
         : "diff-context";

    /// <summary>
    /// The trimmed source text of the given 0-based markdown line, or empty when out of range. A row's
    /// sub-anchor and the line the source map hands back both derive from THIS line, so the anchor and the
    /// resolved line always describe the same text.
    /// </summary>
    private static string SourceLine(string markdown, int zeroBasedLine)
    {
        if (zeroBasedLine < 0 || markdown.Length == 0)
        {
            return string.Empty;
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
                            .Replace('\r', '\n')
                            .Split('\n');
        return zeroBasedLine < lines.Length ? lines[zeroBasedLine].Trim() : string.Empty;
    }

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
