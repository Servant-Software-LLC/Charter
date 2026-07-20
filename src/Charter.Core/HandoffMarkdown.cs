using System.Text;
using System.Text.RegularExpressions;

namespace Charter.Core;

/// <summary>
/// Converts a reviewed Charter deliverable into canonical plain CommonMark for the Guardrails handoff
/// (invariant 5 — no MDX crosses the handoff). Every <c>:::</c> directive FENCE LINE is rewritten to plain
/// markdown Guardrails can parse — a <c>:::note</c>/<c>:::warn</c> to a labeled blockquote, a
/// <c>:::diagram</c> to a fenced <c>mermaid</c> code block, a <c>:::diff</c> to a fenced <c>diff</c> code
/// block, a <c>:::comparison</c> to its already-plain inner list, and a <c>:::question</c> to a resolved
/// (answered) or clearly-flagged open (unanswered) block. Prose, headings, lists, tables, and fenced code
/// blocks pass through VERBATIM — a mid-sentence mention of directive syntax (e.g. talking <em>about</em>
/// <c>:::note</c> in prose) is not a directive and is never rewritten.
/// </summary>
/// <remarks>
/// The block model (<see cref="BlockDocument.Parse(string)"/>) is the single source of truth for what a
/// block IS (invariant 3), so this seam never re-implements Markdig traversal: it parses once, then
/// dispatches on each block's <see cref="BlockKind"/> in source order. A container's
/// <see cref="Block.RawContent"/> spans the whole <c>:::</c> container, so the conversion strips the opening
/// fence line (<c>^:::\w+</c>) and the closing fence line (<c>^:::\s*$</c>) and reshapes what remains.
/// </remarks>
public static class HandoffMarkdown
{
    // The opening directive fence — ":::" immediately followed by the container name (note/warn/diagram/…).
    private static readonly Regex OpenFence = new(@"^:::\w+", RegexOptions.Compiled);

    // The closing directive fence — a line that is nothing but ":::" and optional trailing whitespace.
    private static readonly Regex CloseFence = new(@"^:::\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Emit the plain-markdown handoff for <paramref name="markdown"/>, resolving each <c>:::question</c>
    /// against <paramref name="answers"/> (question id → the selected/submitted answer value(s), the same
    /// shape as <c>Charter.Server.Answer.Values</c> — a plain dictionary, since Charter.Core does not depend
    /// on Charter.Server). A <c>null</c> lookup, or a question id missing from it, means that question was
    /// never answered and is emitted as a clearly-flagged open question.
    /// </summary>
    public static string Emit(string markdown, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers = null)
    {
        // Normalize line endings up front so the block model, the fence-stripping, and the full-line recovery
        // below all agree on '\n' — a block's RawContent is a slice of exactly this source.
        var source = (markdown ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        // Parse once via the single source of truth for blocks (invariant 3), then convert each block in
        // source order — the block order the parser returns IS the handoff order.
        var blocks = BlockDocument.Parse(source).Blocks;

        var parts = new List<string>(blocks.Count);
        foreach (var block in blocks)
        {
            // TrimEnd drops a block's trailing newline(s) so the join below yields exactly one blank line
            // between blocks rather than doubling up when a RawContent slice carries its own line break.
            parts.Add(EmitBlock(block, source, answers).TrimEnd());
        }

        // A blank line between blocks keeps each one a distinct CommonMark block when the output is itself
        // re-parsed (the self-parse round-trip invariant 5 asserts).
        return string.Join("\n\n", parts);
    }

    /// <summary>Convert one parsed block to its plain-CommonMark handoff text, dispatching on its kind.</summary>
    private static string EmitBlock(Block block, string source, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers)
        => block.Kind switch
        {
            // Already plain CommonMark — nothing to convert, so pass the exact source lines through verbatim. A
            // prose block that merely MENTIONS ":::note" mid-line is one of these and survives unmolested.
            BlockKind.Prose or BlockKind.Heading or BlockKind.List or BlockKind.Table or BlockKind.Code
                => PassThrough(source, block.RawContent),

            // Callouts become a labeled blockquote; the fence pair is stripped.
            BlockKind.Note => EmitCallout(InnerLines(block.RawContent), "**Note:**"),
            BlockKind.Warn => EmitCallout(InnerLines(block.RawContent), "**Warning:**"),

            // A comparison's inner content is already a plain markdown list — keep it verbatim, fence gone.
            BlockKind.Comparison => string.Join("\n", InnerLines(block.RawContent)),

            // Diagram/diff inner source is preserved verbatim inside a fenced code block of the matching lang.
            BlockKind.Diagram => EmitFence(InnerLines(block.RawContent), "mermaid"),
            BlockKind.Diff => EmitFence(InnerLines(block.RawContent), "diff"),

            // A question resolves to answered prose or a flagged open question — never its raw JSON body.
            BlockKind.Question => EmitQuestion(block.RawContent, answers),

            // Any future kind falls back to verbatim source rather than silently dropping content.
            _ => PassThrough(source, block.RawContent),
        };

    /// <summary>
    /// The block's full source lines, verbatim. A block's <see cref="Block.RawContent"/> is normally already
    /// the complete source, but Markdig's pipe-table parser trims the outer <c>|</c> from a table's first and
    /// last rows, so the raw span alone would lose them. Locating the span in the original source and
    /// expanding both ends to their line boundaries restores the exact lines the author wrote. The expansion
    /// stops at every newline, so it can only ever grow a partial line into its whole line — never merge one
    /// block into the next.
    /// </summary>
    private static string PassThrough(string source, string rawContent)
    {
        if (string.IsNullOrEmpty(rawContent))
        {
            return rawContent;
        }

        var index = source.IndexOf(rawContent, StringComparison.Ordinal);
        if (index < 0)
        {
            return rawContent;
        }

        var start = index;
        while (start > 0 && source[start - 1] != '\n')
        {
            start--;
        }

        var end = index + rawContent.Length;
        while (end < source.Length && source[end] != '\n')
        {
            end++;
        }

        return source.Substring(start, end - start);
    }

    /// <summary>
    /// The lines between a container's fence pair. Normalizes line endings, drops surrounding blank lines so
    /// the fences are the first/last lines regardless of whether the span carried a trailing newline, then
    /// strips the opening (<c>^:::\w+</c>) and closing (<c>^:::\s*$</c>) fence lines.
    /// </summary>
    private static List<string> InnerLines(string rawContent)
    {
        var normalized = (rawContent ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var lines = new List<string>(normalized.Split('\n'));

        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        while (lines.Count > 0 && lines[0].Trim().Length == 0)
        {
            lines.RemoveAt(0);
        }

        if (lines.Count > 0 && OpenFence.IsMatch(lines[0]))
        {
            lines.RemoveAt(0);
        }

        if (lines.Count > 0 && CloseFence.IsMatch(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>
    /// Render a note/warn callout as a blockquote: every inner line is prefixed with <c>&gt; </c>, and the
    /// first non-empty line additionally carries the bold <paramref name="label"/> (e.g. <c>**Note:**</c>).
    /// </summary>
    private static string EmitCallout(IReadOnlyList<string> innerLines, string label)
    {
        var builder = new StringBuilder();
        var labelPlaced = false;

        for (var i = 0; i < innerLines.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            var line = innerLines[i];
            if (!labelPlaced && line.Trim().Length > 0)
            {
                builder.Append("> ").Append(label).Append(' ').Append(line);
                labelPlaced = true;
            }
            else if (line.Trim().Length == 0)
            {
                builder.Append('>');
            }
            else
            {
                builder.Append("> ").Append(line);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Wrap the inner source verbatim in a fenced code block tagged with <paramref name="language"/>. The
    /// fence is made STRICTLY longer than the longest run of consecutive backticks the body contains (and
    /// never fewer than three), so a body that itself carries <c>```</c> lines — a <c>:::diff</c> or
    /// <c>:::diagram</c> of a markdown file, Charter's own domain — cannot close the fence early and break out
    /// into misattributed blocks. CommonMark closes a fence with a run at least as long as the opener, so an
    /// opener longer than every inner run is un-closable by the body and the whole block stays intact.
    /// </summary>
    private static string EmitFence(IReadOnlyList<string> innerLines, string language)
    {
        var body = string.Join("\n", innerLines);
        var fence = new string('`', FenceLength(body));
        return new StringBuilder()
            .Append(fence).Append(language).Append('\n')
            .Append(body)
            .Append('\n').Append(fence)
            .ToString();
    }

    /// <summary>
    /// The opening/closing fence length for <paramref name="body"/>: one more than the longest run of
    /// consecutive backticks anywhere in the body, and never fewer than three.
    /// </summary>
    private static int FenceLength(string body)
    {
        var longest = 0;
        var current = 0;
        foreach (var ch in body)
        {
            if (ch == '`')
            {
                current++;
                if (current > longest)
                {
                    longest = current;
                }
            }
            else
            {
                current = 0;
            }
        }

        return Math.Max(3, longest + 1);
    }

    /// <summary>
    /// Resolve a <c>:::question</c> against <paramref name="answers"/>. When the question's id is present,
    /// emit answered prose (<c>**Q: {title}** — Answered: {values}</c>); otherwise a clearly-flagged open
    /// question (<c>&gt; **Open question (unresolved):** {title}</c>), listing the options for select modes.
    /// The raw JSON body is NEVER emitted in either branch.
    /// </summary>
    private static string EmitQuestion(string rawContent, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers)
    {
        var body = string.Join("\n", InnerLines(rawContent));
        var spec = QuestionSpec.Parse(body);

        if (answers is not null && answers.TryGetValue(spec.Id, out var values))
        {
            return $"**Q: {spec.Title}** — Answered: {string.Join(", ", values)}";
        }

        var open = $"> **Open question (unresolved):** {spec.Title}";
        if (spec.Mode is QuestionMode.SingleSelect or QuestionMode.MultiSelect)
        {
            open += $" (options: {string.Join(", ", spec.Options)})";
        }

        return open;
    }
}
