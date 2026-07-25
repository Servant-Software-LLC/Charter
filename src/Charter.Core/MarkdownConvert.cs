using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Charter.Core;

/// <summary>
/// The deterministic, best-effort transform behind <c>charter convert</c>: turn a plain Markdown document
/// into a valid <c>.charter.md</c> <em>seed</em> an agent then enriches. It is deliberately NOT an LLM/rich
/// generator — it never synthesizes <c>:::diagram</c>/<c>:::comparison</c> (that is the authoring agent's
/// job, per the <c>charter</c> skill's <c>authoring-from-source</c> reference). Because Charter's format IS
/// CommonMark plus <c>:::</c> directives, a plain <c>.md</c> is already a valid Charter plan, so this pass is
/// intentionally minimal: it passes every existing block through unchanged and applies ONE narrow,
/// high-confidence heuristic.
/// </summary>
/// <remarks>
/// <para>
/// The single heuristic: promote an "open questions"-style section to <c>:::question</c> blocks. A heading
/// fires when — after normalization (see <see cref="IsOpenQuestionsHeading"/>) — it contains a trigger word
/// (<c>open issue(s)</c>, <c>question(s)</c>, <c>risk(s)</c>, or <c>decision(s)</c>) as a WHOLE word, so a
/// numbered/prefixed heading like <c>9. Open questions / risks</c> fires just as <c>Open Questions</c> or
/// <c>Risks</c> does, while a heading with no trigger word (<c>Architecture</c>, <c>Summary</c>) never does.
/// When such a heading is immediately followed by a list — <em>bullet OR ordered/numbered</em> — each
/// <em>simple</em> top-level list item (a single paragraph, no nested list) becomes one OPEN
/// <c>:::question</c> block — a <c>charter-format</c>-valid JSON body with a generated stable <c>id</c>, the
/// item text (inline markup stripped, whitespace collapsed) as <c>title</c>, <c>mode: free-text</c> (the
/// safest default for a prose question — options are never invented), <c>target: human</c>, and no
/// <c>answer</c> (open). A complex list item (a nested sub-list, multiple blocks, or empty text) is left
/// VERBATIM rather than forced into a question. Everything outside such a section — prose, headings, tables,
/// fenced code, ordinary lists, blockquotes, front matter — is preserved.
/// </para>
/// <para>
/// This transform does NOT stamp the <c>charter-format-version</c> marker; that is
/// <see cref="CharterFormat.EnsureVersionMarker(string)"/>'s job, applied by the CLI verb after this pass so
/// the marker helper stays the single source of truth for the frontmatter shape. The transform is pure and
/// deterministic — no I/O — and normalizes line endings to LF so a splice computed on Markdig source spans is
/// cross-platform-stable.
/// </para>
/// </remarks>
public static class MarkdownConvert
{
    /// <summary>A leading list enumerator (<c>9. </c>, <c>10) </c>) stripped from a heading before matching,
    /// so a numbered/prefixed heading like <c>9. Open questions / risks</c> normalizes to the same text a bare
    /// <c>Open questions / risks</c> heading would.</summary>
    private static readonly Regex LeadingEnumerator = new(
        @"^\s*\d+[.)]\s+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The section-heading trigger words, matched as WHOLE words (<c>\b…\b</c>) anywhere in a
    /// normalized heading. Whole-word (not substring) matching is deliberate: <c>Brisketry</c> must NOT match
    /// <c>risk</c>. <c>open issue(s)</c> requires the <c>open</c> prefix (a bare <c>Issues</c> heading is not a
    /// trigger, preserving the original allow-list's intent), while <c>question(s)</c>, <c>risk(s)</c>, and
    /// <c>decision(s)</c> each fire on their own — so <c>9. Open questions / risks</c>, <c>Open Questions</c>,
    /// <c>Risks</c>, <c>Open questions and risks</c>, and <c>Decisions</c> all fire, and <c>Architecture</c> /
    /// <c>Summary</c> do not.</summary>
    private static readonly Regex TriggerWords = new(
        @"\b(open\s+issues?|questions?|risks?|decisions?)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Upper bound on the slug portion of a generated question id, so a long title yields a sane id.
    /// Uniqueness is guaranteed by <see cref="UniqueId"/>'s numeric discriminator, not by the slug alone.</summary>
    private const int MaxSlugLength = 60;

    /// <summary>Upper bound on a <see cref="SkippedItem.LeadSnippet"/> — enough lead text to identify the item
    /// in a report line without spilling a whole paragraph onto the console.</summary>
    private const int MaxSnippetLength = 60;

    // Relaxed JSON escaping keeps generated question titles readable in the .charter.md source (apostrophes,
    // angle brackets, and non-ASCII pass through as themselves rather than \uXXXX). It is safe here because the
    // body is JSON DATA in a markdown file, not HTML — the renderer HTML-escapes spec.Title at render time.
    private static readonly JsonSerializerOptions BodyJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Convert a plain Markdown document into a <c>.charter.md</c> seed body: pass every block through
    /// unchanged and promote the list under any allow-listed "open questions" heading into open
    /// <c>:::question</c> blocks. Deterministic and pure; the returned <see cref="ConvertResult.Markdown"/> is
    /// LF-normalized. Does not stamp the format-version marker (the CLI applies
    /// <see cref="CharterFormat.EnsureVersionMarker(string)"/> after). The returned
    /// <see cref="ConvertResult.Sections"/> report — one entry per section that promoted anything — makes the
    /// transform transparent: it names every item left as prose (a complex/nested item that is emitted verbatim
    /// rather than forced into a question) so the operation never silently drops content (Charter #34).
    /// </summary>
    public static ConvertResult Convert(string markdown)
    {
        var source = (markdown ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        if (source.Length == 0)
        {
            return new ConvertResult(source, Array.Empty<ConvertedSection>());
        }

        var document = CharterMarkdown.ParseDocument(source);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var replacements = new List<(int Start, int Length, string Text)>();
        var sections = new List<ConvertedSection>();

        for (var i = 0; i < document.Count; i++)
        {
            if (document[i] is not HeadingBlock heading || !IsOpenQuestionsHeading(heading))
            {
                continue;
            }

            // "Directly under the heading" — the immediately following top-level block must be the list.
            if (i + 1 >= document.Count || document[i + 1] is not ListBlock list)
            {
                continue;
            }

            var (converted, promoted, skipped) = ConvertList(list, source, usedIds);
            if (promoted == 0)
            {
                continue;
            }

            var start = list.Span.Start;
            var end = list.Span.End;
            var text = BlankLineBefore(source, start) + converted + BlankLineAfter(source, end + 1);
            replacements.Add((start, end - start + 1, text));
            sections.Add(new ConvertedSection(InlineText(heading.Inline), promoted, skipped));
        }

        return new ConvertResult(Splice(source, replacements), sections);
    }

    /// <summary>
    /// True when the heading names an "open questions"-style section. The exact rule: take the heading's plain
    /// inline text (<see cref="InlineText"/> already collapses whitespace and trims), strip a leading list
    /// enumerator (<see cref="LeadingEnumerator"/>, e.g. <c>9. </c>), lowercase it, then fire iff a
    /// <see cref="TriggerWords"/> trigger — <c>open issue(s)</c>, <c>question(s)</c>, <c>risk(s)</c>, or
    /// <c>decision(s)</c> — appears as a WHOLE word. Whole-word matching guards against over-firing
    /// (<c>Brisketry</c> ≠ <c>risk</c>); a heading with no trigger word (<c>Architecture</c>) never fires.
    /// </summary>
    private static bool IsOpenQuestionsHeading(HeadingBlock heading)
    {
        string normalized = LeadingEnumerator
            .Replace(InlineText(heading.Inline), string.Empty)
            .ToLowerInvariant();
        return TriggerWords.IsMatch(normalized);
    }

    /// <summary>
    /// Convert one list into interleaved <c>:::question</c> blocks (for simple items) and verbatim source
    /// (for complex items), returning the joined replacement text, the number of items promoted, and — for
    /// each complex item left verbatim — a <see cref="SkippedItem"/> naming its 1-based position in the list and
    /// a short lead snippet, so the caller can report exactly what it did not promote. When nothing is promoted
    /// the caller leaves the list untouched.
    /// </summary>
    private static (string Text, int Promoted, IReadOnlyList<SkippedItem> Skipped) ConvertList(
        ListBlock list, string source, HashSet<string> usedIds)
    {
        var parts = new List<string>();
        var promoted = 0;
        var skipped = new List<SkippedItem>();
        var ordinal = 0;

        foreach (var child in list)
        {
            if (child is not ListItemBlock item)
            {
                continue;
            }

            ordinal++;

            if (TrySimpleItemText(item, out var title))
            {
                parts.Add(BuildQuestionBlock(UniqueId(title, usedIds), title));
                promoted++;
            }
            else
            {
                parts.Add(Verbatim(source, item.Span));
                skipped.Add(new SkippedItem(ordinal, LeadSnippet(item)));
            }
        }

        return (string.Join("\n\n", parts), promoted, skipped);
    }

    /// <summary>
    /// A short, human-readable lead snippet for a complex item left verbatim: the plain text of its first
    /// paragraph (inline markup stripped, whitespace collapsed), capped at <see cref="MaxSnippetLength"/> with a
    /// trailing ellipsis when truncated, or <c>(empty item)</c> when the item has no leading paragraph text.
    /// Used only in the report, never in the produced markdown.
    /// </summary>
    private static string LeadSnippet(ListItemBlock item)
    {
        foreach (var child in item)
        {
            if (child is not ParagraphBlock paragraph)
            {
                continue;
            }

            var text = InlineText(paragraph.Inline);
            if (text.Length == 0)
            {
                break;
            }

            return text.Length <= MaxSnippetLength
                ? text
                : text[..MaxSnippetLength].TrimEnd() + "...";
        }

        return "(empty item)";
    }

    /// <summary>
    /// A "simple" list item is exactly one paragraph with non-empty text and no nested list — the shape that
    /// cleanly maps to a single question. Returns the item's plain text (inline markup stripped, whitespace
    /// collapsed) as <paramref name="title"/>. Anything else (a nested sub-list, multiple blocks, empty text)
    /// is complex and left verbatim.
    /// </summary>
    private static bool TrySimpleItemText(ListItemBlock item, out string title)
    {
        title = string.Empty;

        ParagraphBlock? paragraph = null;
        var childCount = 0;
        foreach (var child in item)
        {
            childCount++;
            paragraph = child as ParagraphBlock;
        }

        if (childCount != 1 || paragraph is null)
        {
            return false;
        }

        var text = InlineText(paragraph.Inline);
        if (text.Length == 0)
        {
            return false;
        }

        title = text;
        return true;
    }

    /// <summary>Build one open <c>:::question</c> block with a <c>charter-format</c>-valid free-text body.</summary>
    private static string BuildQuestionBlock(string id, string title)
    {
        var body = new JsonObject
        {
            ["id"] = id,
            ["title"] = title,
            ["mode"] = "free-text",
            ["target"] = "human",
        };

        return ":::question\n" + body.ToJsonString(BodyJsonOptions) + "\n:::";
    }

    /// <summary>
    /// A document-unique, deterministic id from the title: an ASCII slug (falling back to <c>question</c> when
    /// the title has no slug-able characters), disambiguated with a <c>-2</c>/<c>-3</c>… suffix against ids
    /// already generated for this document (mirroring the anchor model's duplicate discriminator).
    /// </summary>
    private static string UniqueId(string title, HashSet<string> usedIds)
    {
        var baseSlug = Slugify(title);
        if (baseSlug.Length == 0)
        {
            baseSlug = "question";
        }

        var candidate = baseSlug;
        var suffix = 2;
        while (!usedIds.Add(candidate))
        {
            candidate = baseSlug + "-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            suffix++;
        }

        return candidate;
    }

    /// <summary>Lowercase ASCII slug: alphanumeric runs joined by single hyphens, no leading/trailing hyphen,
    /// capped at <see cref="MaxSlugLength"/>. Non-ASCII and punctuation become separators, so the id is safe as
    /// both a JSON string and an HTML anchor.</summary>
    private static string Slugify(string title)
    {
        var slug = new StringBuilder(title.Length);
        var pendingSeparator = false;

        foreach (var ch in title.ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                if (pendingSeparator && slug.Length > 0)
                {
                    slug.Append('-');
                }

                pendingSeparator = false;
                slug.Append(ch);

                if (slug.Length >= MaxSlugLength)
                {
                    break;
                }
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return slug.ToString();
    }

    /// <summary>The exact source text of a Markdig span, or empty when the span is degenerate.</summary>
    private static string Verbatim(string source, SourceSpan span)
    {
        if (span.IsEmpty || source.Length == 0)
        {
            return string.Empty;
        }

        var start = Math.Clamp(span.Start, 0, source.Length - 1);
        var end = Math.Clamp(span.End, start, source.Length - 1);
        return source.Substring(start, end - start + 1);
    }

    /// <summary>
    /// The plain text of an inline tree — literal and code text concatenated, emphasis/link containers
    /// recursed into, line breaks flattened to spaces — with whitespace collapsed to single spaces and
    /// trimmed. Used for both heading matching and question titles so both see the same normalized text.
    /// </summary>
    private static string InlineText(ContainerInline? container)
    {
        var builder = new StringBuilder();
        AppendInlineText(container, builder);
        return CollapseWhitespace(builder.ToString());
    }

    private static void AppendInlineText(ContainerInline? container, StringBuilder builder)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case LineBreakInline:
                    builder.Append(' ');
                    break;
                case ContainerInline child:
                    AppendInlineText(child, builder);
                    break;
            }
        }
    }

    /// <summary>Collapse every run of whitespace (including newlines) to a single space and trim the ends.</summary>
    private static string CollapseWhitespace(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The newline prefix that guarantees a blank line separates a spliced-in container from the preceding
    /// content: 0 newlines when a blank line already precedes <paramref name="start"/>, otherwise enough to
    /// make one. Keeps the promoted <c>:::question</c> block a well-delimited container even when the source
    /// put the heading and list on adjacent lines.
    /// </summary>
    private static string BlankLineBefore(string source, int start)
    {
        var newlines = 0;
        for (var i = start - 1; i >= 0 && source[i] == '\n'; i--)
        {
            newlines++;
        }

        return new string('\n', Math.Max(0, 2 - newlines));
    }

    /// <summary>
    /// The newline suffix that guarantees a blank line follows a spliced-in container before the next block —
    /// unless the container ends the document, in which case none is added. Mirrors
    /// <see cref="BlankLineBefore"/> on the trailing side.
    /// </summary>
    private static string BlankLineAfter(string source, int endExclusive)
    {
        var newlines = 0;
        var i = endExclusive;
        while (i < source.Length && source[i] == '\n')
        {
            newlines++;
            i++;
        }

        var moreContentFollows = i < source.Length;
        return moreContentFollows ? new string('\n', Math.Max(0, 2 - newlines)) : string.Empty;
    }

    /// <summary>Apply the collected span replacements to <paramref name="source"/> in ascending order.</summary>
    private static string Splice(string source, List<(int Start, int Length, string Text)> replacements)
    {
        if (replacements.Count == 0)
        {
            return source;
        }

        replacements.Sort((a, b) => a.Start.CompareTo(b.Start));

        var builder = new StringBuilder(source.Length);
        var cursor = 0;
        foreach (var (start, length, text) in replacements)
        {
            builder.Append(source, cursor, start - cursor);
            builder.Append(text);
            cursor = start + length;
        }

        builder.Append(source, cursor, source.Length - cursor);
        return builder.ToString();
    }
}

/// <summary>
/// The result of <see cref="MarkdownConvert.Convert(string)"/>: the produced seed <paramref name="Markdown"/>
/// plus a per-section <paramref name="Sections"/> report so the transform is transparent rather than silent —
/// callers (the CLI) surface what was promoted and what was left as prose (Charter #34).
/// </summary>
/// <param name="Markdown">The LF-normalized seed markdown (not yet marker-stamped).</param>
/// <param name="Sections">One entry per section that promoted at least one item; empty when nothing promoted.</param>
public sealed record ConvertResult(string Markdown, IReadOnlyList<ConvertedSection> Sections);

/// <summary>
/// A section that <c>charter convert</c> promoted: its <paramref name="Heading"/> text, how many list items it
/// <paramref name="Promoted"/> to <c>:::question</c> blocks, and the items it <paramref name="Skipped"/> (left
/// verbatim as prose). The list's total item count is <c>Promoted + Skipped.Count</c>.
/// </summary>
/// <param name="Heading">The section heading's plain text (leading enumerator preserved, e.g. <c>9. Open questions / risks</c>).</param>
/// <param name="Promoted">The number of simple items promoted to open <c>:::question</c> blocks.</param>
/// <param name="Skipped">The complex/nested items left verbatim, each named by ordinal and lead snippet.</param>
public sealed record ConvertedSection(string Heading, int Promoted, IReadOnlyList<SkippedItem> Skipped);

/// <summary>
/// A single list item <c>charter convert</c> left as prose rather than promoting to a <c>:::question</c>:
/// its 1-based <paramref name="Ordinal"/> within the list and a short <paramref name="LeadSnippet"/> of its
/// lead text, enough for a human to find and hand-promote (or hand to an authoring agent to enrich).
/// </summary>
/// <param name="Ordinal">1-based position of the item within its list.</param>
/// <param name="LeadSnippet">A short, inline-markup-stripped snippet of the item's lead text, or <c>(empty item)</c>.</param>
public sealed record SkippedItem(int Ordinal, string LeadSnippet);
