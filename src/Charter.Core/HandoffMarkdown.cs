using System.Text;
using System.Text.RegularExpressions;

namespace Charter.Core;

/// <summary>
/// Converts a reviewed Charter deliverable into canonical plain CommonMark for the Guardrails handoff
/// (invariant 5 — no MDX crosses the handoff). Every <c>:::</c> directive FENCE LINE is rewritten to plain
/// markdown Guardrails can parse — a <c>:::note</c>/<c>:::warn</c> to a labeled blockquote, a
/// <c>:::diagram</c> to a fenced <c>mermaid</c> code block, a <c>:::diff</c> to a fenced <c>diff</c> code
/// block, a <c>:::comparison</c> to its already-plain inner list, a <c>:::custom-html</c> to its inner HTML
/// passed through verbatim, and a <c>:::question</c> to a resolved (answered — from the external answers dict
/// or the inline <c>answer</c>) or clearly-flagged open (unanswered) block. Prose, headings, lists, tables, and fenced code
/// blocks pass through VERBATIM — a mid-sentence mention of directive syntax (e.g. talking <em>about</em>
/// <c>:::note</c> in prose) is not a directive and is never rewritten.
/// </summary>
/// <remarks>
/// The block model (<see cref="BlockDocument.Parse(string)"/>) is the single source of truth for what a
/// block IS (invariant 3), so this seam never re-implements Markdig traversal: it parses once, then
/// dispatches on each block's <see cref="BlockKind"/> in source order. A container's
/// <see cref="Block.RawContent"/> spans the whole <c>:::</c> container, so the conversion strips its opening
/// and closing fence lines — recognized by <see cref="DirectiveFence"/> at ANY fence length, the same
/// vocabulary <see cref="QuestionResolution.QuestionBody"/> reads (Charter #172/#190) — and reshapes what
/// remains.
/// </remarks>
public static class HandoffMarkdown
{
    // The opening fence's directive name — the first non-whitespace token after the ":::". Used only to name
    // an unrecognized directive in its flagged handoff line.
    private static readonly Regex OpenFenceName = new(@"^:::\s*(\S+)", RegexOptions.Compiled);

    // A CommonMark code-fence line at COLUMN ZERO: three-or-more backticks (or tildes), an optional info
    // string, nothing else. Column zero is deliberate — a :::diff body's context lines are INDENTED (" ```"),
    // so an indented fence is diff CONTENT, never the container's own wrapper (see EmitFencedBody).
    private static readonly Regex CodeFenceLine = new(@"^(`{3,}|~{3,})[ \t]*(\S*)[ \t]*$", RegexOptions.Compiled);

    /// <summary>
    /// Emit the plain-markdown handoff for <paramref name="markdown"/>, resolving each <c>:::question</c>
    /// against <paramref name="answers"/> (question id → the selected/submitted answer value(s), the same
    /// shape as <c>Charter.Server.Answer.Values</c> — a plain dictionary, since Charter.Core does not depend
    /// on Charter.Server). A <c>null</c> lookup, or a question id missing from it, means that question was
    /// never answered and is emitted as a clearly-flagged open question.
    /// </summary>
    /// <param name="answersSha256">
    /// The hash of the <c>--answers</c> file's text, for the in-band answers stamp (Charter #187). <b>Supply it
    /// whenever <paramref name="answers"/> came from a file</b> — omitting it stamps <c>none</c>, which is a
    /// claim that no answers file was involved. The CLI cannot get this wrong because it carries both halves in
    /// one <see cref="HandoffAnswers"/>; an in-library caller resolving from a dictionary it built itself has no
    /// file to name, and <c>none</c> is then the honest value.
    /// </param>
    public static string Emit(
        string markdown,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? answers = null,
        string? answersSha256 = null)
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

        // The in-band provenance stamp (Charter #172/#187). Until it existed the flattened plan
        // self-identified as NOTHING — no version, no hash, no link to its source; front matter is stripped
        // and a test pins that it is — so "did the plan Charter recorded match the plan Guardrails consumed"
        // could not be answered even in principle.
        //
        // Four properties earn it its place, and no out-of-band record has all four: an HTML comment is
        // CommonMark-safe (it is a leaf HTML block and renders as nothing), it is INVISIBLE in a rendered
        // diff on GitHub, it is deterministic in the plan text, and — the one that decides it — it SURVIVES A
        // CONSUMER THAT IGNORES EXIT CODES AND SIDE FILES. Out-of-band signalling cannot fix a failure of
        // out-of-band signalling.
        //
        // The hash is of the plan text EXACTLY AS READ, not of the normalized source above, so it is
        // byte-identical to `planSha256` in the same plan's `charter headless` record. Those two values are
        // the join; hashing different text here would make the join silently meaningless.
        //
        // TWO lines, because one identifies the PLAN and the pair identifies the RESOLUTION INPUTS
        // (Charter #187). Reproduced before it was fixed: run once with `--answers v1.json --manifest`, then
        // re-run as a plain `charter handoff`. The write is unconditional so plan.md becomes the
        // all-questions-open flatten; no manifest is written because it is opt-in; the OLD manifest survives —
        // and planSha256, this plan stamp, the headless record's planSha256 and charterVersion ALL FOUR MATCH.
        // A manifest certifying decisions that are not in the file beside it, with every documented join green.
        // The answers stamp makes exactly that visible from the two artifacts alone: the manifest says a hex,
        // the file says `none`.
        //
        // The answers line goes FIRST so the flattened plan still ENDS with the plan hash, which
        // references/handoff.md promises and a consumer may already be matching on.
        parts.Add(AnswersStamp(answersSha256));
        parts.Add(Stamp(markdown ?? string.Empty));

        // A blank line between blocks keeps each one a distinct CommonMark block when the output is itself
        // re-parsed (the self-parse round-trip invariant 5 asserts).
        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// The trailing provenance comment: <c>&lt;!-- charter: plan-sha256=&lt;hex&gt; --&gt;</c>. One line, no
    /// clock, no local path — the same discipline the forensic record keeps, so the flattened plan stays as
    /// safe to hand on as the artifact.
    /// </summary>
    public static string Stamp(string markdown)
        => $"<!-- {StampPrefix}{PlanHash.Sha256Hex(markdown)} -->";

    /// <summary>
    /// The stamp's fixed prefix — the string a consumer scans for. Exposed so a reader locates the stamp by
    /// the producer's own constant instead of a re-typed literal.
    /// </summary>
    public const string StampPrefix = "charter: plan-sha256=";

    /// <summary>
    /// The companion provenance comment: <c>&lt;!-- charter: answers-sha256=&lt;hex|none&gt; --&gt;</c>
    /// (Charter #187). <paramref name="answersSha256"/> is null when no <c>--answers</c> file was supplied, and
    /// the stamp then reads <see cref="NoAnswersFile"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the plan hash alone was not enough.</b> A run that supplies answers and a run that supplies none
    /// produce the same <c>plan-sha256</c> over the same plan — so a stale manifest from the first can sit
    /// beside the output of the second with every documented join agreeing. This line is the difference, and it
    /// is necessary AND sufficient: the hazard bites only when the resolution INPUTS differ, and the pair of
    /// stamps is exactly those inputs.
    /// </para>
    /// <para>
    /// It is also CRLF-immune, which the manifest's <c>handoffSha256</c> byte-hash is not — a line-ending
    /// rewrite in transit invalidates that hash while leaving both stamps readable and correct.
    /// </para>
    /// </remarks>
    public static string AnswersStamp(string? answersSha256)
        => $"<!-- {AnswersStampPrefix}{answersSha256 ?? NoAnswersFile} -->";

    /// <summary>The answers stamp's fixed prefix — the string a consumer scans for.</summary>
    public const string AnswersStampPrefix = "charter: answers-sha256=";

    /// <summary>
    /// What the answers stamp carries when no <c>--answers</c> file was supplied. A word rather than an empty
    /// value or an omitted line, because "this run merged no answers file" is a POSITIVE fact a consumer must
    /// be able to read — an absent line would be indistinguishable from a producer too old to write one.
    /// </summary>
    public const string NoAnswersFile = "none";

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

            // Diagram/diff inner source is preserved verbatim inside EXACTLY ONE fenced code block of the
            // matching language — whether the body was authored raw or already wrapped in its own fence.
            BlockKind.Diagram => EmitFencedBody(InnerLines(block.RawContent), "mermaid"),
            BlockKind.Diff => EmitFencedBody(InnerLines(block.RawContent), "diff"),

            // A question resolves to answered prose or a flagged open question — never its raw JSON body.
            BlockKind.Question => EmitQuestion(block.RawContent, answers),

            // The raw-HTML escape hatch passes its inner HTML through VERBATIM (fence stripped) — raw HTML is
            // valid CommonMark, so it survives the bridge to Guardrails as authored rather than being flattened
            // to a callout (which is what it wrongly did while it misclassified as a note).
            BlockKind.CustomHtml => string.Join("\n", InnerLines(block.RawContent)),

            // An unrecognized :::foo directive is flagged, NOT flattened as a note and NOT passed through as a
            // raw ::: fence (which would re-open a directive line in the plain-markdown handoff, breaking the
            // invariant-5 self-parse). It surfaces as a single blockquote line naming the directive (Charter #22).
            BlockKind.Unknown => EmitUnknown(block.RawContent),

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
    /// strips the opening and closing fence lines — recognized by <see cref="DirectiveFence"/>, the one fence
    /// vocabulary this shares with <see cref="QuestionResolution.QuestionBody"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Any fence length</b> (Charter #190). This used to match <c>^:::\w+</c> and <c>^:::\s*$</c>, so a
    /// container opened with FOUR colons had neither fence recognized and both survived into the flattened
    /// body. That is not an exotic shape: <c>charter-format</c> tells an author to widen the fence whenever a
    /// body line would itself start with <c>:::</c>, so the defect fired on the NESTING case — a callout
    /// carrying a diagram, a <c>:::diff</c> OF a Charter plan — which is where it is least likely to be
    /// noticed. #172 had already widened the <c>::::question</c> path (there the unreadable body cost the
    /// handoff the question's id, title and target); this is the same widening for the prose containers, and
    /// now through the same helper so they cannot drift apart again.
    /// </para>
    /// <para>
    /// "Invariant 5 held anyway" was true of only two of the six containers. A note/warn flattens to a
    /// blockquote, so its leaked <c>::::</c> rode behind a <c>&gt;</c> — luck, not design. A
    /// <c>:::comparison</c> and a <c>:::custom-html</c> emit their inner lines VERBATIM, so the leak was a
    /// live directive line at column zero in the plain-CommonMark handoff; and a <c>:::diff</c>'s leaked
    /// opener sat where <see cref="TryUnwrapOwnFence"/> looks for the body's own <c>```diff</c> fence, so the
    /// unwrap failed and the container's fence lines came out as diff CONTENT inside an escalated fence.
    /// </para>
    /// <para>
    /// Only the FIRST and LAST line are tested, which is what keeps a colon run inside a body content: a
    /// <c>:::diff</c>'s context lines arrive as <c>" :::note"</c> and stay exactly where the author put them.
    /// </para>
    /// </remarks>
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

        if (lines.Count > 0 && DirectiveFence.IsOpen(lines[0]))
        {
            lines.RemoveAt(0);
        }

        if (lines.Count > 0 && DirectiveFence.IsClose(lines[^1]))
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
    /// Emit a <c>:::diagram</c> / <c>:::diff</c> body as EXACTLY ONE fenced code block tagged with
    /// <paramref name="language"/>, whichever of the two accepted authoring forms it used.
    /// </summary>
    /// <remarks>
    /// The documented authoring form wraps the body in its own <c>```mermaid</c> / <c>```diff</c> fence, and
    /// the renderer accepts both that and a raw, fence-less body. The handoff, however, wrapped
    /// UNCONDITIONALLY: an already-fenced body came out DOUBLE-fenced (<c>````mermaid</c> around
    /// <c>```mermaid</c>), which turns the inner fence into literal text so GitHub renders the flattened plan's
    /// diagram as a code listing rather than a diagram (Charter #48/C2). So: unwrap an outer fence that is the
    /// container's own wrapper, then emit once.
    /// The wrapper is only recognized at COLUMN ZERO with a matching (or empty) info string. That is what keeps
    /// a <c>:::diff</c> of a markdown file safe — its fence lines arrive as diff context/add/remove lines
    /// (<c>" ```"</c>, <c>"+```"</c>), which are body content and must stay inside the emitted fence, and which
    /// <see cref="EmitFence"/> then protects with a longer opener.
    /// </remarks>
    private static string EmitFencedBody(List<string> innerLines, string language)
        => EmitFence(TryUnwrapOwnFence(innerLines, language, out var content) ? content : innerLines, language);

    /// <summary>
    /// True when <paramref name="innerLines"/> is exactly ONE fenced code block that is the container's own
    /// wrapper — a column-zero opening fence whose info string is empty or <paramref name="language"/>, a
    /// column-zero closing fence as the last line, and no intermediate line that would close it early. Then
    /// <paramref name="content"/> is the lines BETWEEN the fences. Any other shape (raw body, mixed content,
    /// an indented fence, a different language) returns false so the caller wraps the body verbatim.
    /// </summary>
    private static bool TryUnwrapOwnFence(List<string> innerLines, string language, out List<string> content)
    {
        content = innerLines;
        if (innerLines.Count < 2)
        {
            return false;
        }

        var opener = CodeFenceLine.Match(innerLines[0]);
        if (!opener.Success)
        {
            return false;
        }

        var info = opener.Groups[2].Value;
        if (info.Length > 0 && !string.Equals(info, language, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var marker = opener.Groups[1].Value;
        if (!IsCloser(innerLines[^1], marker))
        {
            return false;
        }

        // An intermediate closer would mean the body is several blocks, not one wrapper — leave it alone.
        for (var i = 1; i < innerLines.Count - 1; i++)
        {
            if (IsCloser(innerLines[i], marker))
            {
                return false;
            }
        }

        content = innerLines.GetRange(1, innerLines.Count - 2);
        return true;
    }

    /// <summary>
    /// True when <paramref name="line"/> is a column-zero closing fence for an opener of
    /// <paramref name="marker"/> — the same fence character, at least as long, and carrying no info string.
    /// </summary>
    private static bool IsCloser(string line, string marker)
    {
        var match = CodeFenceLine.Match(line);
        return match.Success
            && match.Groups[2].Value.Length == 0
            && match.Groups[1].Value.Length >= marker.Length
            && match.Groups[1].Value[0] == marker[0];
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
    /// Flag an unrecognized <c>:::foo</c> directive as a blockquote naming the directive, so a typo or unlisted
    /// container is VISIBLE in the handoff rather than flattened to a note (Charter #22) — followed by its BODY,
    /// preserved as blockquoted prose context. The interop contract requires the body to be parsed through and
    /// NEVER silently dropped; the previous single-line flag discarded it, losing real plan content behind a
    /// directive typo (Charter #48/C5). Every emitted line begins with <c>&gt;</c> and the directive name rides
    /// inside inline code, so no line can start with <c>:::</c> — the invariant-5 self-parse still holds even
    /// when the body itself contains directive-looking text.
    /// </summary>
    private static string EmitUnknown(string rawContent)
    {
        var directive = UnknownDirectiveName(rawContent);
        var body = InnerLines(rawContent);

        var builder = new StringBuilder()
            .Append("> **Unknown Charter directive `:::").Append(directive).Append("` — not in the format catalog.**");

        if (body.Count == 0)
        {
            return builder.ToString();
        }

        builder.Append(" Its body is preserved below.").Append("\n>");
        foreach (var line in body)
        {
            builder.Append('\n').Append(line.Trim().Length == 0 ? ">" : "> " + line);
        }

        return builder.ToString();
    }

    /// <summary>The directive name of an unknown container — the first token after the opening <c>:::</c> fence,
    /// or the first non-blank line's text when no fence token can be read.</summary>
    private static string UnknownDirectiveName(string rawContent)
    {
        var normalized = (rawContent ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        foreach (var line in normalized.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var match = OpenFenceName.Match(trimmed);
            return match.Success ? match.Groups[1].Value : trimmed;
        }

        return string.Empty;
    }

    /// <summary>
    /// Resolve a <c>:::question</c> against <paramref name="answers"/>. When the question's id is present,
    /// emit answered prose (<c>**Q: {title}** — Answered: {values}</c>); otherwise a clearly-flagged open
    /// question (<c>&gt; **Open question (unresolved):** {title}</c>). EITHER branch is followed by the same
    /// <see cref="QuestionMetadataLine"/> carrying <c>id</c>, <c>mode</c>, <c>target</c> and <c>options</c>.
    /// The raw JSON body is NEVER emitted in either branch.
    /// </summary>
    private static string EmitQuestion(string rawContent, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers)
    {
        // ONE question-body parse, shared with `charter headless`'s forensic record (Charter #172). This used
        // to be a private line-split here, and the two diverged in both directions: an unterminated container
        // at EOF flattened perfectly while the record called it malformed, and a `::::question` fence read
        // perfectly in the record while the flatten DELETED the question's title, id and target behind a
        // "Malformed question" line. QuestionBodyParityTests pins the agreement.
        var body = QuestionResolution.QuestionBody(rawContent);

        // Degrade a malformed/empty :::question to a clearly-flagged line rather than throwing (which would
        // abort the whole handoff). The flag is a single blockquote line, so it never starts a line with :::
        // (invariant 5), and every other block still emits.
        if (body is null)
        {
            return "> **Malformed question (could not parse): the container is not a well-formed :::question.**";
        }

        if (!QuestionSpec.TryParse(body, out var spec, out var error) || spec is null)
        {
            return $"> **Malformed question (could not parse): {error}**";
        }

        // Migration-bridge faithfulness (DA blocker 1): the external answers dict wins when it carries this id
        // AND that entry passes AnswerRules, else fall back to the answer carried INLINE in the resolved
        // :::question (spec.Answer). Without the fallback, a resolved .charter.md flattens as
        // all-questions-open and every human decision is lost; without the rules, an out-of-band file could
        // assert a value the question's own options forbid, or erase a decision a human made (Charter #186).
        // Shared with HandoffGate so a --fail-if-needs-human verdict can never describe a different document
        // from the one this emits (Charter #172).
        var resolved = AnswerRules.Merge(spec, answers);

        var metadata = QuestionMetadataLine(spec);

        // The agent's reasoning travels downstream too (Charter #132). On an OPEN question it is the context
        // a breakdown needs to ask well; on an ANSWERED one it is sharper still — read against `recommended`,
        // an answer that went the other way shows not just which option the human rejected but the argument
        // they rejected with it.
        //
        // Emitted as its own line rather than folded into the compact metadata line: that line is `key:
        // \`value\`` pairs meant to parse with a trivial scan, and a couple of sentences of prose (possibly
        // containing backticks) does not belong in it. The `_Why: ` prefix is load-bearing, not decoration —
        // it guarantees the line cannot START with `:::` however the rationale begins (invariant 5), and
        // newlines are collapsed so one rationale stays one line.
        var why = spec.Rationale is { Length: > 0 }
            ? "_Why: " + Inline(spec.Rationale) + "_"
            : null;

        // A DECISION, not merely a non-empty array: `[""]` used to flatten as "Answered:" with nothing after
        // it, which is what a mis-written generator produces and what a strict gate then certified
        // (Charter #188). Same predicate as the record's `answered` and the rendered page's status.
        if (AnswerRules.IsDecision(resolved))
        {
            var answered = $"**Q: {spec.Title}** — Answered: {string.Join(", ", resolved)}\n{metadata}";
            return why is null ? answered : answered + "\n" + why;
        }

        // An open question DELEGATED to an agent must TELL the agent what to do, not leave it to infer
        // (Charter #172). On this path there is no parser and no routing table — the flattened plan is prose,
        // and prose is the whole interface. "Open question (unresolved)" reads as something someone ELSE will
        // settle, which is exactly wrong for a block whose `target` says the reader settles it.
        var delegated = string.Equals(QuestionSpec.Token(spec.Target), "agent", StringComparison.Ordinal);
        var lead = delegated
            ? $"> **Delegated decision — you must settle this before building:** {spec.Title}\n> {metadata}"
                + $"\n> _{DecisionInstruction(spec)}_"
            : $"> **Open question (unresolved):** {spec.Title}\n> {metadata}";

        return why is null ? lead : lead + "\n> " + why;
    }

    /// <summary>
    /// What a delegated (<c>target: agent</c>) question tells its reader to DO, phrased for the question's own
    /// mode and naming the author's lean where there is one.
    /// </summary>
    /// <remarks>
    /// The leading <c>Decide: </c> is load-bearing rather than decorative, exactly as <c>_Why: </c> is: it
    /// guarantees the line cannot START with <c>:::</c> however the option values are spelled (invariant 5).
    /// The whole line rides inside the blockquote with the lead and the metadata, so it can never separate
    /// from the question it belongs to.
    /// </remarks>
    private static string DecisionInstruction(QuestionSpec spec)
    {
        var choose = QuestionSpec.Token(spec.Mode) switch
        {
            "single" => "choose exactly one of the options above",
            "multi" => "choose one or more of the options above",
            "bool" => "decide yes or no",
            "number" => "supply a number",
            _ => "write the answer yourself",
        };

        var lean = spec.Recommended is { Length: > 0 } value
            ? $" The plan's author leans `{value}`; depart from it only with a stated reason."
            : " The plan's author gave no lean.";

        return $"Decide: {choose}, state the choice and your reason in the work you generate from this plan, "
            + $"and build against it. Do not carry it forward as an open question.{lean}";
    }

    /// <summary>
    /// The question's routing metadata as ONE compact, plain-CommonMark line emitted under BOTH the Answered
    /// and the Open line — identical in shape either way.
    /// </summary>
    /// <remarks>
    /// The flattened plan used to drop <c>id</c>, <c>target</c> and <c>mode</c> outright and to keep
    /// <c>options</c> only on OPEN questions (Charter #48/C3+C4), which broke two things measured end to end:
    /// <list type="bullet">
    ///   <item><description><c>options</c> on an ANSWERED question is the rationale the breakdown needs — the
    ///     REJECTED option is what lets it author a guardrail that fails if the implementation reaches for it.
    ///     <c>charter-format</c> tells the interpreter to fold the answer in "keeping the options as
    ///     rationale"; dropping them destroyed exactly that.</description></item>
    ///   <item><description><c>target</c> is the routing signal, so that a consumer <em>can</em> branch on
    ///     <c>human</c> vs <c>agent</c>: without it the flattened path structurally CANNOT honour
    ///     <c>target: agent</c>, and a decision the author explicitly delegated to the agent is
    ///     indistinguishable from one that needs a person.
    ///     <b>No claim is made that any consumer does branch on it.</b> This used to say the headless
    ///     breakdown branches on <c>human</c> vs <c>agent</c>; it does not — neither literal this seam emits
    ///     (<c>Open question (unresolved)</c>, <c>_Question — id</c>) appears anywhere in Guardrails' source,
    ///     docs or skills. That false claim was the sole justification for exempting <c>target: agent</c>
    ///     from strict <c>handoff</c>'s gate (Charter #172), which is why the exemption is now narrowed to
    ///     DECIDABLE agent questions only. Filed reciprocally as Guardrails #500.</description></item>
    /// </list>
    /// The shape is emphasis + inline code — plain CommonMark that renders as a readable sub-line on GitHub and
    /// parses with a trivial <c>key: `value`</c> scan. Fields are separated by <c>;</c> so the <c>,</c> inside
    /// the options list is unambiguous, and nothing here starts a line with <c>:::</c> (invariant 5).
    /// </remarks>
    /// <summary>Collapse a value to a single line. A rationale is plain text but may carry newlines, and a
    /// break mid-line would leave the tail outside the blockquote an open question is emitted in.</summary>
    private static string Inline(string text)
        => text.Replace("\r\n", " ", StringComparison.Ordinal)
               .Replace('\r', ' ')
               .Replace('\n', ' ')
               .Trim();

    private static string QuestionMetadataLine(QuestionSpec spec)
    {
        var builder = new StringBuilder()
            .Append("_Question — id: `").Append(spec.Id).Append('`')
            .Append("; mode: `").Append(QuestionSpec.Token(spec.Mode)).Append('`')
            .Append("; target: `").Append(QuestionSpec.Token(spec.Target)).Append('`');

        if (spec.Options.Count > 0)
        {
            builder.Append("; options: ");
            for (var i = 0; i < spec.Options.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append('`').Append(spec.Options[i]).Append('`');
            }
        }

        // The authoring agent's lean travels downstream too (Charter #125). On an OPEN question it tells the
        // breakdown which way the plan was heading; on an ANSWERED one it is the sharper signal — a human who
        // chose AGAINST the recommendation made a deliberate correction, and a task built from that plan should
        // not quietly drift back toward the option they rejected.
        if (spec.Recommended is { Length: > 0 })
        {
            builder.Append("; recommended: `").Append(spec.Recommended).Append('`');
        }

        return builder.Append('_').ToString();
    }
}
