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
/// It makes NO byte-preservation promise for a rewritten JSON body (it may be re-whitespaced), but it does
/// preserve the fence lines, every non-question block, prose, and any YAML front matter EXACTLY — those live
/// in the verbatim segments this kernel copies straight from the source.
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
    /// single atomic write: read the current markdown, splice answers with <see cref="Apply"/>, then persist
    /// the result through a uniquely-named temp file created IN THE PLAN'S OWN DIRECTORY and renamed over the
    /// original. Because the temp shares the plan's directory (and therefore its volume), the rename is atomic
    /// on Windows and Unix alike, so a concurrent reader — the review server's per-request
    /// <c>File.ReadAllText</c> — always sees a complete old-or-new file, never a half-written one. This is the
    /// single discrete writer the living-document model requires (§1.4): one invocation, one atomic replace,
    /// no torn read. Returns the rewritten markdown that was persisted. A failure before the rename leaves the
    /// original file untouched and removes the temp.
    /// </summary>
    public static string ApplyToFile(string planPath, IReadOnlyDictionary<string, IReadOnlyList<string>> answersById)
    {
        if (string.IsNullOrEmpty(planPath))
        {
            throw new ArgumentException("A plan path is required.", nameof(planPath));
        }

        var markdown = File.ReadAllText(planPath);
        var updated = Apply(markdown, answersById);
        AtomicWrite(planPath, updated);
        return updated;
    }

    /// <summary>
    /// Write <paramref name="contents"/> to <paramref name="destinationPath"/> atomically: a uniquely-named
    /// temp file in the SAME directory is written and flushed, then renamed over the destination
    /// (<see cref="File.Move(string, string, bool)"/>, a same-volume rename). The temp shares the destination's
    /// directory so the rename never crosses volumes; a failure before the rename leaves the destination
    /// untouched, and the temp is removed on a failed write so no orphan is left in the plan directory.
    /// </summary>
    private static void AtomicWrite(string destinationPath, string contents)
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

        obj["answer"] = answerArray;

        var opening = rawContent.Substring(0, bodyStart);
        var closing = rawContent.Substring(bodyEnd);
        var newline = opening.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        return opening + obj.ToJsonString() + newline + closing;
    }

    /// <summary>The string <c>id</c> of a question block's JSON body, or <c>null</c> when it is unreadable.</summary>
    private static string? ReadQuestionId(string rawContent)
        => TryLocateJsonBody(rawContent, out var bodyStart, out var bodyEnd)
            ? ReadId(ParseBody(rawContent.Substring(bodyStart, bodyEnd - bodyStart)))
            : null;

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
    /// Locate the JSON body span of a <c>:::question</c> container's raw content: <paramref name="bodyStart"/> is
    /// the index just after the opening fence line, and <paramref name="bodyEnd"/> is the start of the closing
    /// fence line. Returns <c>false</c> when the content is not a well-formed fenced container (so the caller
    /// leaves it untouched). Keeping the fence lines out of the span lets the caller rewrite ONLY the body while
    /// preserving the exact fences.
    /// </summary>
    private static bool TryLocateJsonBody(string rawContent, out int bodyStart, out int bodyEnd)
    {
        bodyStart = 0;
        bodyEnd = 0;

        if (string.IsNullOrEmpty(rawContent))
        {
            return false;
        }

        var firstNewline = rawContent.IndexOf('\n');
        if (firstNewline < 0)
        {
            return false;
        }

        bodyStart = firstNewline + 1;

        // The closing fence is the last non-blank line of the container span. Trimming trailing whitespace/
        // newlines first makes the last '\n' point at the start of that closing line regardless of a trailing
        // newline in the raw slice; the trimmed prefix shares indices with the original, so the offset is valid.
        var trimmedEnd = rawContent.TrimEnd();
        var closeLineStart = trimmedEnd.LastIndexOf('\n');
        if (closeLineStart < 0)
        {
            return false;
        }

        closeLineStart += 1;
        if (closeLineStart < bodyStart || !IsCloseFence(trimmedEnd.AsSpan(closeLineStart)))
        {
            return false;
        }

        bodyEnd = closeLineStart;
        return true;
    }

    /// <summary>True when <paramref name="line"/> is a container closing fence — three or more colons only.</summary>
    private static bool IsCloseFence(ReadOnlySpan<char> line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 3)
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (ch != ':')
            {
                return false;
            }
        }

        return true;
    }
}
