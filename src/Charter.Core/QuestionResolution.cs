using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Charter.Core;

/// <summary>
/// The single, deterministic kernel that writes resolved answers back INTO a Charter deliverable's
/// <c>:::question</c> blocks (the living-document model — a resolved question carries its <c>answer</c>
/// inline). <see cref="Apply"/> splices each drained answer into its question's JSON body via a surgical
/// <see cref="JsonObject"/> key-add, and <see cref="FindDuplicateQuestionIds"/> is the review-time lint that
/// flags two questions sharing an id (which <see cref="Apply"/> would otherwise answer in BOTH).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Apply"/> is deliberately NOT a <see cref="QuestionSpec"/> round-trip. <see cref="QuestionSpec"/>
/// captures only five keys and its parse normalizes/drops everything else, so rebuilding a block from a spec
/// would silently discard any other body key. Instead this kernel parses the block's JSON body to a
/// <see cref="JsonObject"/>, sets ONLY the <c>answer</c> key, and re-serializes that object in place —
/// preserving every other key. It reuses <see cref="BlockDocument.Parse(string)"/> to locate the question
/// blocks rather than re-implementing Markdig traversal (mirroring <see cref="HandoffMarkdown"/>'s discipline).
/// </para>
/// <para>
/// It preserves the fence lines, every non-question block, prose, and any YAML front matter EXACTLY — those
/// live in the verbatim segments this kernel copies straight from the source. For the rewritten JSON body it
/// makes a BEST-EFFORT (not guaranteed) layout promise: when the answer can be spliced in place and the result
/// proven equivalent, the authored body survives byte-for-byte apart from the single line that gains the
/// answer; otherwise the object is re-serialized compactly, which may re-whitespace the body. Callers must not
/// depend on the body's byte layout — but they can rely on an answered question not silently reflowing a
/// carefully authored multi-line block onto one line.
/// </para>
/// </remarks>
public static class QuestionResolution
{
    /// <summary>
    /// Splice each answer in <paramref name="answersById"/> (question id -&gt; the selected/submitted value(s),
    /// the same shape as <c>Charter.Server.Answer.Values</c>) into the matching <c>:::question</c> block's JSON
    /// body as an <c>answer</c> array, returning the rewritten markdown. A question whose id is not in the map,
    /// or whose body is not parseable JSON, is left untouched; every non-question byte of
    /// <paramref name="markdown"/> (prose, other blocks, front matter, fences) is preserved verbatim.
    /// Deterministic in its inputs.
    /// </summary>
    public static string Apply(string markdown, IReadOnlyDictionary<string, IReadOnlyList<string>> answersById)
    {
        if (string.IsNullOrEmpty(markdown) || answersById is null || answersById.Count == 0)
        {
            return markdown ?? string.Empty;
        }

        var builder = new StringBuilder(markdown.Length);
        var cursor = 0;

        foreach (var block in BlockDocument.Parse(markdown).Blocks)
        {
            if (block.Kind != BlockKind.Question || string.IsNullOrEmpty(block.RawContent))
            {
                continue;
            }

            var index = markdown.IndexOf(block.RawContent, cursor, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var updated = ResolveBlock(block.RawContent, answersById);
            if (updated is null)
            {
                continue;
            }

            // Copy the verbatim run since the last write (prose, front matter, skipped blocks), then the
            // rewritten question block; advance the cursor only past a block we actually replaced.
            builder.Append(markdown, cursor, index - cursor);
            builder.Append(updated);
            cursor = index + block.RawContent.Length;
        }

        builder.Append(markdown, cursor, markdown.Length - cursor);
        return builder.ToString();
    }

    /// <summary>
    /// Apply <paramref name="answersById"/> to the plan file at <paramref name="planPath"/> IN PLACE via a
    /// single atomic write: read the current markdown, refuse a plan with duplicate <c>:::question</c> ids,
    /// splice answers with <see cref="Apply"/>, then persist the result through a uniquely-named temp file
    /// created IN THE PLAN'S OWN DIRECTORY and renamed over the original. Because the temp shares the plan's
    /// directory (and therefore its volume), the rename is atomic on Windows and Unix alike, so a concurrent
    /// reader — the review server's per-request <c>File.ReadAllText</c> — always sees a complete old-or-new
    /// file, never a half-written one. This is the single discrete writer the living-document model requires
    /// (§1.4): one invocation, one atomic replace, no torn read. Returns the rewritten markdown that was
    /// persisted. A failure before the rename leaves the original file untouched and removes the temp.
    /// </summary>
    /// <remarks>
    /// Two guards make the write safe against the failure modes a cold review found in the destructive drain:
    /// <list type="bullet">
    ///   <item><b>Duplicate-id refusal.</b> If the plan carries two <c>:::question</c> blocks sharing an id,
    ///   <see cref="Apply"/> would splice the answer into BOTH — a silent double-write. This throws
    ///   <see cref="DuplicateQuestionIdException"/> BEFORE writing anything, so the plan is left untouched and
    ///   the caller can preserve the queued answers and report a clear error.</item>
    ///   <item><b>Concurrent-edit precondition.</b> The atomic rename prevents a torn read, not a lost update
    ///   versus an external editor (the drafting agent's own <c>Edit</c>/<c>Write</c>, or a second
    ///   <c>resolve</c>/<c>poll --apply</c>). This captures the content read at the start and, just before the
    ///   rename, re-reads the file; if it changed underneath, the write is refused with an
    ///   <see cref="IOException"/> rather than silently clobbering the external edit.</item>
    /// </list>
    /// </remarks>
    public static string ApplyToFile(string planPath, IReadOnlyDictionary<string, IReadOnlyList<string>> answersById)
    {
        if (string.IsNullOrEmpty(planPath))
        {
            throw new ArgumentException("A plan path is required.", nameof(planPath));
        }

        var markdown = File.ReadAllText(planPath);

        // Refuse a duplicate-id plan BEFORE any write: applying an answer to two blocks sharing an id is a
        // silent double-write, so this is a review-time error, not a resolution to guess at.
        var duplicates = FindDuplicateQuestionIds(markdown);
        if (duplicates.Count > 0)
        {
            throw new DuplicateQuestionIdException(duplicates);
        }

        var updated = Apply(markdown, answersById);
        AtomicWriteIfUnchanged(planPath, expectedCurrent: markdown, contents: updated);
        return updated;
    }

    /// <summary>
    /// Write <paramref name="contents"/> to <paramref name="destinationPath"/> atomically AND only if the file
    /// still holds <paramref name="expectedCurrent"/> (the content the caller read before computing the write).
    /// A uniquely-named temp file in the SAME directory is written, then renamed over the destination
    /// (<see cref="File.Move(string, string, bool)"/>, a same-volume rename). The temp shares the destination's
    /// directory so the rename never crosses volumes; a failure before the rename leaves the destination
    /// untouched, and the temp is removed on a failed write so no orphan is left in the plan directory.
    /// </summary>
    /// <remarks>
    /// The precondition re-reads the file immediately before the rename and compares it to
    /// <paramref name="expectedCurrent"/>; a mismatch means an external writer changed the file since the
    /// caller read it, so overwriting would silently lose that edit — this throws an <see cref="IOException"/>
    /// instead. It narrows, but cannot fully close, the read→write window; the living-document model's real
    /// guarantee is single-writer discipline (§1.4), and this is the loud backstop when that is violated.
    /// Exposed <c>internal</c> so the precondition can be proven deterministically without a cross-process race.
    /// </remarks>
    internal static void AtomicWriteIfUnchanged(string destinationPath, string expectedCurrent, string contents)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = ".";
        }

        // A dotted, randomized temp name in the plan's own directory: same volume (atomic rename), unique
        // (no collision with a concurrent writer), and hidden-ish so a directory listing is not littered.
        var tempPath = Path.Combine(
            directory,
            "." + Path.GetFileName(fullPath) + "." + Path.GetRandomFileName() + ".tmp");

        try
        {
            File.WriteAllText(tempPath, contents);

            // Concurrent-edit precondition: re-read the destination as late as possible (just before the
            // rename) and refuse if it no longer matches what the caller based the write on.
            var current = File.ReadAllText(fullPath);
            if (!string.Equals(current, expectedCurrent, StringComparison.Ordinal))
            {
                throw new IOException(
                    $"the plan changed on disk since it was read ({Path.GetFileName(fullPath)}); "
                    + "not overwriting. Re-run to apply against the current version.");
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Best-effort delete of a temp file left behind by a failed <see cref="AtomicWrite"/>.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temp is a cosmetic nuisance, never a data-loss event — the original is intact.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The missing-lean lint (Charter #142): the ids of OPEN, human-targeted, select-mode questions in
    /// <paramref name="markdown"/> that carry no <c>recommended</c> key at all, in document order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>recommended</c> is optional in schema and load-bearing in the unattended path. <c>charter headless</c>
    /// escalates an open human question, and the usefulness of that escalation depends entirely on whether the
    /// record can say what the authoring agent would have chosen. Without a lean the escalation says "a human
    /// must decide" and offers nothing to decide WITH. Eleven questions were authored across two real plans
    /// without one and nothing anywhere noticed, because an omitted optional field produces valid output — the
    /// worst available combination of silent and wrong.
    /// </para>
    /// <para>
    /// <b>An explicit <c>"recommended": null</c> is NOT reported.</b> That is the deliberate opt-out: some forks
    /// genuinely are 50/50, and "I considered a lean and declined to give one" must be distinguishable from "I
    /// never knew the field existed". Only a key that is ABSENT reads as the latter. This is why the lint works
    /// on the raw JSON body rather than on a parsed <see cref="QuestionSpec"/> — the parse maps both spellings
    /// to a null <see cref="QuestionSpec.Recommended"/> and the distinction is gone.
    /// </para>
    /// <para>
    /// Scoped to what a lean can actually mean: <c>free-text</c> and <c>bool</c> have no options to recommend,
    /// an <c>agent</c>-targeted question is answered downstream rather than escalated to a person, and an
    /// ANSWERED question's lean is moot — the decision is already recorded.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> FindQuestionsMissingRecommendation(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return Array.Empty<string>();
        }

        var missing = new List<string>();

        foreach (var block in BlockDocument.Parse(markdown).Blocks)
        {
            if (block.Kind != BlockKind.Question)
            {
                continue;
            }

            var body = QuestionBody(block.RawContent);
            if (body is null)
            {
                continue;
            }

            JsonObject? root;
            try
            {
                root = JsonNode.Parse(body) as JsonObject;
            }
            catch (JsonException)
            {
                continue;   // a malformed body is the parser's problem to report, not this lint's
            }

            if (root is null || !root.TryGetPropertyValue("id", out var idNode))
            {
                continue;
            }

            var id = idNode?.GetValue<string>();
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            // Absent `recommended` only. A present-but-null key is the deliberate decline.
            if (root.ContainsKey("recommended"))
            {
                continue;
            }

            // An answered question has a decision on record; a lean would change nothing about it. "Answered"
            // is AnswerRules.IsDecision — a blank value is not a decision, so a question carrying one still
            // needs the lean it never got (Charter #188).
            if (root.TryGetPropertyValue("answer", out var answerNode)
                && answerNode is JsonArray answered
                && AnswerRules.IsDecision(AnswerStrings(answered)))
            {
                continue;
            }

            // `agent` questions are resolved downstream, not escalated to a person.
            var target = root.TryGetPropertyValue("target", out var targetNode)
                ? targetNode?.GetValue<string>()
                : null;
            if (target is not null && !string.Equals(target, "human", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Only a select mode has options to lean toward.
            var mode = root.TryGetPropertyValue("mode", out var modeNode) ? modeNode?.GetValue<string>() : null;
            if (!string.Equals(mode, "single", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mode, "multi", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            missing.Add(id);
        }

        return missing;
    }

    /// <summary>
    /// The document-unique-question-id lint: the distinct ids carried by more than one <c>:::question</c> block
    /// in <paramref name="markdown"/>, in first-seen order (empty when every question id is unique). A duplicate
    /// id is a review-time error because <see cref="Apply"/> would write the same answer into every block that
    /// carries it — a silent double-write. Ids are read from the raw JSON body exactly as <see cref="Apply"/>
    /// reads them, so the lint reports precisely the ids that would be double-written.
    /// </summary>
    public static IReadOnlyList<string> FindDuplicateQuestionIds(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return Array.Empty<string>();
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var block in BlockDocument.Parse(markdown).Blocks)
        {
            if (block.Kind != BlockKind.Question)
            {
                continue;
            }

            var id = ReadQuestionId(block.RawContent);
            if (id is null)
            {
                continue;
            }

            if (counts.TryGetValue(id, out var seen))
            {
                counts[id] = seen + 1;
            }
            else
            {
                counts[id] = 1;
                order.Add(id);
            }
        }

        return order.Where(id => counts[id] > 1).ToList();
    }

    /// <summary>
    /// <b>The ONE definition of a <c>:::question</c>'s body</b>: the lines of a container's
    /// <see cref="Block.RawContent"/> between its opening fence line and its closing fence line, with line
    /// endings normalized to <c>\n</c> — or <c>null</c> when the content is not a container at all (no opening
    /// fence line to strip). An empty body is <c>""</c>, not <c>null</c>: the container exists, it simply
    /// declares nothing, and the schema parse is the right place to say so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every reader of a question body goes through here — <see cref="HandoffMarkdown"/>'s flatten,
    /// <see cref="HeadlessRecord"/>'s forensic record, <see cref="QuestionIdentity"/>'s declared-shape
    /// fingerprint, and the two lints below. That is not tidiness: the <b>definition</b> of the block was
    /// single-sourced in <c>charter-format</c> while the <b>parse</b> was forked, and the fork was load-bearing
    /// (Charter #172). <c>HandoffMarkdown</c> carried its own line-splitting that normalized line endings and
    /// tolerated a missing closing fence but recognised an opening fence only as <c>^:::\w+</c>; this kernel
    /// required a closing fence and looked only for <c>\n</c>. The result was a container that one verb read
    /// perfectly and the other called malformed — in BOTH directions, depending on the shape.
    /// </para>
    /// <para>
    /// Three tolerances, each earned by a shape the renderer already accepts — so this function agrees with
    /// what a reviewer sees on the page:
    /// </para>
    /// <list type="number">
    ///   <item><description><b>Any fence length.</b> The opening line is whatever the container opened with
    ///     (<c>:::question</c>, <c>::::question</c>, …). CommonMark directive containers nest by fence length,
    ///     so a <c>::::</c> opener is ordinary authoring, not a defect.</description></item>
    ///   <item><description><b>A missing closing fence.</b> Markdig closes an unterminated container at EOF
    ///     and the renderer draws the form, so calling it unreadable escalates a question a human can plainly
    ///     answer on the page.</description></item>
    ///   <item><description><b>Any line ending.</b> <c>\r\n</c> and a bare <c>\r</c> terminate a line exactly
    ///     as <c>\n</c> does.</description></item>
    /// </list>
    /// </remarks>
    public static string? QuestionBody(string rawContent)
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

        // No opening fence line means this is not a container span at all, so there is no body to name.
        if (lines.Count == 0 || !IsOpenFence(lines[0]))
        {
            return null;
        }

        lines.RemoveAt(0);

        if (lines.Count > 0 && IsCloseFence(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join("\n", lines);
    }

    /// <summary>True when <paramref name="line"/> opens a directive container — three or more colons followed
    /// by a directive name. Read from <see cref="DirectiveFence"/>, the one fence vocabulary, which the
    /// flatten reads too (#190): this tolerance used to live here alone, which is exactly how the two seams
    /// came to disagree about a <c>::::</c> container in the first place.</summary>
    private static bool IsOpenFence(string line) => DirectiveFence.IsOpen(line);

    /// <summary>
    /// The rewritten raw content of one <c>:::question</c> block with its answer spliced in, or <c>null</c> when
    /// the body is not parseable JSON, carries no string <c>id</c>, or its id is absent from
    /// <paramref name="answersById"/> (all "leave untouched" cases).
    /// </summary>
    private static string? ResolveBlock(string rawContent, IReadOnlyDictionary<string, IReadOnlyList<string>> answersById)
    {
        if (!TryLocateJsonBody(rawContent, out var bodyStart, out var bodyEnd))
        {
            return null;
        }

        var obj = ParseBody(rawContent.Substring(bodyStart, bodyEnd - bodyStart));
        if (obj is null)
        {
            return null;
        }

        var id = ReadId(obj);
        if (id is null || !answersById.TryGetValue(id, out var values))
        {
            return null;
        }

        // Surgical key-add: set ONLY "answer", preserving every other key (and their order — a new key appends).
        var answerArray = new JsonArray();
        foreach (var value in values)
        {
            answerArray.Add(JsonValue.Create(value));
        }

        var hadAnswer = obj.ContainsKey("answer");
        obj["answer"] = answerArray;

        var opening = rawContent.Substring(0, bodyStart);
        var body = rawContent.Substring(bodyStart, bodyEnd - bodyStart);
        var closing = rawContent.Substring(bodyEnd);
        var newline = opening.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        // Prefer the minimal in-place splice, which keeps the AUTHORED body byte-for-byte apart from the one
        // line that gains the answer. Re-serializing the whole object instead collapses a multi-line body onto
        // one line, shrinking the file and shifting the source line of every anchor below it (Charter #49).
        // Falls back to the whole-object re-serialize whenever the splice cannot be PROVEN equivalent.
        var spliced = hadAnswer ? null : TrySpliceAnswer(body, answerArray, obj);
        return spliced is not null
            ? opening + spliced + closing
            : opening + obj.ToJsonString() + newline + closing;
    }

    /// <summary>
    /// The minimal-diff write: return <paramref name="body"/> with <c>"answer": […]</c> inserted immediately
    /// after its last content character, so every authored line — indentation, key order, line breaks, the
    /// trailing newline — survives untouched and only the body's final content line grows. Returns
    /// <see langword="null"/> (caller falls back to re-serializing the object) whenever the result cannot be
    /// PROVEN to be exactly the intended object: the body must end in the root <c>}</c>, and the spliced text
    /// must re-parse to an object whose canonical serialization equals <paramref name="expected"/>'s. That
    /// verification is what makes a textual splice safe — it can produce the right bytes or nothing, never
    /// subtly wrong JSON. The caller only takes this path when the body carries NO existing <c>answer</c> key,
    /// so the insertion can never duplicate one.
    /// </summary>
    private static string? TrySpliceAnswer(string body, JsonArray answer, JsonObject expected)
    {
        var close = LastContentIndex(body, body.Length - 1);
        if (close < 0 || body[close] != '}')
        {
            return null;
        }

        var last = LastContentIndex(body, close - 1);
        if (last < 0)
        {
            return null;
        }

        // An empty object ({}) has nothing to comma-separate the new key from.
        var insertion = (body[last] == '{' ? string.Empty : ", ") + "\"answer\": " + answer.ToJsonString();
        var candidate = string.Concat(body.AsSpan(0, last + 1), insertion, body.AsSpan(last + 1));

        try
        {
            return JsonNode.Parse(candidate) is JsonObject spliced
                && string.Equals(spliced.ToJsonString(), expected.ToJsonString(), StringComparison.Ordinal)
                    ? candidate
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The index of the last non-whitespace character at or before <paramref name="from"/>, or -1.</summary>
    private static int LastContentIndex(string text, int from)
    {
        for (var i = Math.Min(from, text.Length - 1); i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// An <c>answer</c> array's string elements, with any non-string element read as blank. A value that is
    /// not a string is not a decision either, and this lint must never throw on a body the schema parse has
    /// not yet judged.
    /// </summary>
    private static IReadOnlyList<string> AnswerStrings(JsonArray answer)
    {
        var values = new List<string>(answer.Count);
        foreach (var node in answer)
        {
            values.Add(node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty);
        }

        return values;
    }

    /// <summary>The string <c>id</c> of a question block's JSON body, or <c>null</c> when it is unreadable.</summary>
    private static string? ReadQuestionId(string rawContent)
        => QuestionBody(rawContent) is { } body ? ReadId(ParseBody(body)) : null;

    /// <summary>Parse a JSON body to a <see cref="JsonObject"/>, or <c>null</c> when it is not a JSON object.</summary>
    private static JsonObject? ParseBody(string body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The string value of the object's <c>id</c> key, or <c>null</c> when absent or not a string.</summary>
    private static string? ReadId(JsonObject? obj)
        => obj is not null && obj["id"] is JsonValue value && value.TryGetValue<string>(out var id) ? id : null;

    /// <summary>
    /// The WRITE-side twin of <see cref="QuestionBody"/>: the same body, located as a SPAN of
    /// <paramref name="rawContent"/> rather than returned as text, so <see cref="ResolveBlock"/> can splice an
    /// answer in while preserving the authored bytes on either side.
    /// <paramref name="bodyStart"/> is the index just after the opening fence line and
    /// <paramref name="bodyEnd"/> is the start of the closing fence line (or the end of the content when the
    /// container was never closed). Returns <c>false</c> when there is no opening fence line to skip — the one
    /// case <see cref="QuestionBody"/> also answers <c>null</c> for.
    /// </summary>
    /// <remarks>
    /// It exists as a second function only because indices cannot survive line-ending normalization, and the
    /// splice needs indices. It must therefore agree with <see cref="QuestionBody"/> on the two questions that
    /// matter — is there a body, and where does it end — or a plan whose question the record can READ becomes
    /// one whose answer <c>resolve</c> silently declines to WRITE. Both accept any fence length, any line
    /// ending, and an unterminated container at EOF.
    /// <para>
    /// Exposed <c>internal</c> so that agreement can be PROVEN rather than reasoned about
    /// (<c>QuestionBodyParityTests</c>). It is worth the exposure: the one time the two were written
    /// independently they disagreed in three container shapes and nothing caught it, and a disagreement here
    /// is invisible from outside — the write path simply declines to splice a body it mis-located, which
    /// looks exactly like a question that had no answer to write.
    /// </para>
    /// </remarks>
    internal static bool TryLocateJsonBody(string rawContent, out int bodyStart, out int bodyEnd)
    {
        bodyStart = 0;
        bodyEnd = 0;

        if (string.IsNullOrEmpty(rawContent))
        {
            return false;
        }

        // The opening fence line ends at the first line break of ANY flavour; a \r\n pair counts once.
        var firstBreak = rawContent.IndexOfAny(['\n', '\r']);
        if (firstBreak < 0)
        {
            return false;
        }

        bodyStart = firstBreak + 1;
        if (rawContent[firstBreak] == '\r'
            && bodyStart < rawContent.Length
            && rawContent[bodyStart] == '\n')
        {
            bodyStart++;
        }

        // The closing fence, when there is one, is the last non-blank line of the container span. Trimming
        // trailing whitespace first makes the last line break point at the start of that closing line
        // regardless of a trailing newline in the raw slice; the trimmed prefix shares indices with the
        // original, so the offset stays valid.
        var trimmedEnd = rawContent.TrimEnd();
        var closeLineStart = trimmedEnd.LastIndexOfAny(['\n', '\r']) + 1;

        // An UNTERMINATED container is not malformed — Markdig closes it at EOF and the renderer draws the
        // form — so its body simply runs to the end of the span rather than stopping at a fence.
        //
        // `<` and not `<=`: a container whose body is EMPTY (`:::question` then `:::`) puts the closing fence
        // line at exactly bodyStart, and that is a closed container with a zero-length body — the shape
        // QuestionBody returns "" for. Rejecting it here would make the two disagree on the one case they most
        // obviously must not, and would hand the splice a "body" of ":::".
        if (closeLineStart < bodyStart || !IsCloseFence(trimmedEnd.AsSpan(closeLineStart)))
        {
            bodyEnd = rawContent.Length;
            return true;
        }

        bodyEnd = closeLineStart;
        return true;
    }

    /// <summary>True when <paramref name="line"/> is a container closing fence — three or more colons only.
    /// Read from <see cref="DirectiveFence"/> for the same reason <see cref="IsOpenFence"/> is.</summary>
    private static bool IsCloseFence(ReadOnlySpan<char> line) => DirectiveFence.IsClose(line);
}
