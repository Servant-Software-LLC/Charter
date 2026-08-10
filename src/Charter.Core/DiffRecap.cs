using System.Text;

namespace Charter.Core;

/// <summary>
/// The deterministic, best-effort transform behind <c>charter recap</c>: turn a unified diff into a valid
/// <c>.charter.md</c> <em>seed</em> an agent then enriches (Charter #1). It is the mirror image of
/// <see cref="MarkdownConvert"/> — same contract, different input: pass the mechanical facts through into
/// blocks, promote nothing that needs judgment, and REPORT what was left so the agent knows its job.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this deliberately does NOT do.</b> A recap worth reviewing needs a summary, a theme-level grouping,
/// an architecture <c>:::diagram</c>, and the questions the change actually raises. All four require judgment,
/// and Charter's binary holds no model — the LLM lives in the agent driving it, never in here. So this pass
/// emits only what a diff literally states (what changed, by how much, in which files, over which commits) and
/// names the rest on <see cref="RecapResult.Notes"/> for the authoring agent. That is the same division of
/// labour <c>charter convert</c> uses, and the reason neither verb is a "generator".
/// </para>
/// <para>
/// <b>Scope boundary with Guardrails.</b> A recap describes a DIFF so a human can annotate it in place. It is
/// not an execution report — task outcomes, timings, retries and gate results belong to Guardrails'
/// <c>uber-report</c>, which owns that data and would only be duplicated (and contradicted) here. Charter owns
/// the render + review surface over a change; it does not narrate the run that produced it.
/// </para>
/// <para>
/// The transform is pure and deterministic — no I/O, no git — so it is testable against fixture diffs. Reading
/// git is the CLI's job (and is strictly read-only; see <c>docs/plans/03-git-mediated-team-review.md</c> §5.1).
/// It emits LF and does NOT stamp the <c>charter-format-version</c> marker; the CLI applies
/// <see cref="CharterFormat.EnsureVersionMarker(string)"/> after this pass, keeping that helper the single
/// source of truth for the frontmatter shape.
/// </para>
/// </remarks>
public static class DiffRecap
{
    /// <summary>
    /// Per-file cap on emitted diff lines. A recap of a large branch must stay a document a human can review in
    /// a browser, not a 40 000-line dump — but a cap that hides its own effect would make the recap quietly
    /// WRONG (the reviewer would annotate a change believing they had seen all of it). So the cap is always
    /// reported: in <see cref="RecapFile.OmittedLines"/>, in the rendered block, and on the CLI's stderr.
    /// </summary>
    public const int DefaultMaxDiffLinesPerFile = 400;

    /// <summary>
    /// Build the seed. <paramref name="range"/> is the revision range as the user typed it (it appears verbatim
    /// in the title and overview, so a reviewer can reproduce it); <paramref name="unifiedDiff"/> is raw
    /// <c>git diff</c> output; <paramref name="commits"/> may be empty when the range is a working-tree diff.
    /// </summary>
    public static RecapResult Build(
        string range,
        string unifiedDiff,
        IReadOnlyList<RecapCommit>? commits = null,
        int maxDiffLinesPerFile = DefaultMaxDiffLinesPerFile)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(unifiedDiff);

        commits ??= Array.Empty<RecapCommit>();
        if (maxDiffLinesPerFile <= 0)
        {
            maxDiffLinesPerFile = int.MaxValue;
        }

        var files = ParseFiles(unifiedDiff, maxDiffLinesPerFile);
        var markdown = Emit(range, files, commits);
        return new RecapResult(markdown, files, BuildNotes(files));
    }

    // ---------------------------------------------------------------------------------------------------
    // Parsing
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Split raw <c>git diff</c> output into per-file records. Only the lines a reviewer reads are kept: the
    /// <c>@@</c> hunk headers and their bodies. The <c>diff --git</c>/<c>index</c>/<c>---</c>/<c>+++</c>
    /// preamble is consumed into structure (path, change kind, binary-ness) rather than shown, because it is
    /// noise the file heading already states — and because a <c>---</c>/<c>+++</c> line inside the block would
    /// be counted as a removed/added line by every consumer that reads the leading marker.
    /// </summary>
    private static IReadOnlyList<RecapFile> ParseFiles(string unifiedDiff, int maxDiffLinesPerFile)
    {
        var normalized = unifiedDiff.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var files = new List<RecapFile>();

        string? path = null;
        string? oldPath = null;
        var change = RecapChange.Modified;
        var binary = false;
        var body = new List<string>();
        var inHunk = false;

        void Flush()
        {
            if (path is null)
            {
                return;
            }

            // Trim BEFORE capping. git's output ends in a newline, so the final split yields a blank the cap
            // would otherwise count as hidden content — reporting "1 line not shown" for a file shown in full.
            var kept = TrimTrailingBlank(body);
            var omitted = 0;
            if (kept.Count > maxDiffLinesPerFile)
            {
                omitted = kept.Count - maxDiffLinesPerFile;
                kept = kept.GetRange(0, maxDiffLinesPerFile);
            }

            var added = 0;
            var removed = 0;
            foreach (var line in body)
            {
                if (line.StartsWith('+'))
                {
                    added++;
                }
                else if (line.StartsWith('-'))
                {
                    removed++;
                }
            }

            files.Add(new RecapFile(path, oldPath, change, added, removed, binary, kept, omitted));
            path = null;
            oldPath = null;
            change = RecapChange.Modified;
            binary = false;
            body = new List<string>();
            inHunk = false;
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                path = ParseGitHeaderPath(line);
                continue;
            }

            if (path is null)
            {
                continue;   // preamble before the first file (or an empty diff)
            }

            if (!inHunk)
            {
                if (line.StartsWith("new file mode", StringComparison.Ordinal))
                {
                    change = RecapChange.Added;
                    continue;
                }

                if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
                {
                    change = RecapChange.Deleted;
                    continue;
                }

                if (line.StartsWith("rename from ", StringComparison.Ordinal))
                {
                    change = RecapChange.Renamed;
                    oldPath = line["rename from ".Length..].Trim();
                    continue;
                }

                if (line.StartsWith("rename to ", StringComparison.Ordinal))
                {
                    change = RecapChange.Renamed;
                    path = line["rename to ".Length..].Trim();
                    continue;
                }

                // "Binary files a/x and b/x differ", or "GIT binary patch" under --binary.
                if (line.StartsWith("Binary files ", StringComparison.Ordinal)
                    || line.StartsWith("GIT binary patch", StringComparison.Ordinal))
                {
                    binary = true;
                    continue;
                }
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = true;
                body.Add(line);
                continue;
            }

            if (!inHunk)
            {
                continue;   // index / mode / --- / +++ and anything else in the preamble
            }

            // "\ No newline at end of file" is git's annotation, not a diff line; keeping it would render as a
            // removed line (it starts with a backslash, not a marker) and confuse the +/- counts.
            if (line.StartsWith(@"\ No newline", StringComparison.Ordinal))
            {
                continue;
            }

            body.Add(line);
        }

        Flush();
        return files;
    }

    /// <summary>
    /// Pull the new path out of a <c>diff --git a/x b/y</c> header. Git quotes and C-escapes a path containing
    /// unusual bytes, and a path may itself contain <c> b/</c>, so the halves are split on the LAST separator
    /// that yields two equal-length sides — falling back to the <c>b/</c> suffix, then to the raw remainder,
    /// because a path Charter cannot parse must still appear in the recap rather than vanish from it.
    /// </summary>
    private static string ParseGitHeaderPath(string headerLine)
    {
        var rest = headerLine["diff --git ".Length..].Trim();

        if (rest.StartsWith("a/", StringComparison.Ordinal))
        {
            var half = (rest.Length - 1) / 2;
            if (half > 2
                && rest.Length % 2 == 1
                && rest[half] == ' '
                && rest.AsSpan(half + 1).StartsWith("b/"))
            {
                return rest[(half + 3)..];
            }
        }

        var marker = rest.LastIndexOf(" b/", StringComparison.Ordinal);
        return marker >= 0 ? rest[(marker + 3)..] : rest;
    }

    private static List<string> TrimTrailingBlank(List<string> lines)
    {
        var end = lines.Count;
        while (end > 0 && lines[end - 1].Trim().Length == 0)
        {
            end--;
        }

        return end == lines.Count ? lines : lines.GetRange(0, end);
    }

    // ---------------------------------------------------------------------------------------------------
    // Emission
    // ---------------------------------------------------------------------------------------------------

    private static string Emit(string range, IReadOnlyList<RecapFile> files, IReadOnlyList<RecapCommit> commits)
    {
        var added = files.Sum(f => f.Added);
        var removed = files.Sum(f => f.Removed);

        var seed = new StringBuilder();
        seed.Append("# Recap: ").Append(Inline(range)).Append("\n\n");

        seed.Append("| | |\n|---|---|\n");
        seed.Append("| Range | ").Append(Code(range)).Append(" |\n");
        seed.Append("| Files changed | ").Append(files.Count).Append(" |\n");
        seed.Append("| Lines | +").Append(added).Append(" / -").Append(removed).Append(" |\n");
        if (commits.Count > 0)
        {
            seed.Append("| Commits | ").Append(commits.Count).Append(" |\n");
        }

        seed.Append('\n');

        if (commits.Count > 0)
        {
            seed.Append("## Commits\n\n| Commit | Subject |\n|---|---|\n");
            foreach (var commit in commits)
            {
                seed.Append("| ").Append(Code(commit.ShortSha))
                    .Append(" | ").Append(Cell(commit.Subject)).Append(" |\n");
            }

            seed.Append('\n');
        }

        seed.Append("## Changes by file\n\n");
        foreach (var file in files)
        {
            seed.Append("### ").Append(Code(file.Path)).Append(" — ").Append(Describe(file)).Append("\n\n");

            if (file.OldPath is { Length: > 0 })
            {
                seed.Append("Renamed from ").Append(Code(file.OldPath)).Append(".\n\n");
            }

            if (file.IsBinary)
            {
                seed.Append("Binary file — git records no textual diff for it.\n\n");
                continue;
            }

            if (file.DiffLines.Count == 0)
            {
                seed.Append("No textual change (mode or metadata only).\n\n");
                continue;
            }

            var container = ContainerFence(file.DiffLines);
            var fence = CodeFence(file.DiffLines);
            seed.Append(container).Append("diff\n").Append(fence).Append("diff\n");
            foreach (var line in file.DiffLines)
            {
                seed.Append(line).Append('\n');
            }

            seed.Append(fence).Append('\n').Append(container).Append("\n\n");

            if (file.OmittedLines > 0)
            {
                seed.Append(":::warn\n")
                    .Append(file.OmittedLines)
                    .Append(" further diff line(s) in this file are not shown — the recap caps each file at ")
                    .Append(file.DiffLines.Count)
                    .Append(" lines. Review the rest with `git diff` before approving.\n:::\n\n");
            }
        }

        return seed.ToString();
    }

    /// <summary>
    /// The two fences a recap's diff body needs. Both are computed from the content, and BOTH are required —
    /// each defeats a different way arbitrary repository text silently destroys a <c>:::diff</c> block.
    /// <para>
    /// <b>The code fence</b> (backticks) makes the body opaque, which is what stops a line reading
    /// <c>:::note</c> from OPENING a nested directive and swallowing the tail of the diff. It is widened past
    /// three when the body itself contains a code fence — diffing any markdown file does that.
    /// </para>
    /// <para>
    /// <b>The container fence</b> (colons) is widened for a reason that is easy to get wrong: a container's
    /// close check runs BEFORE the inner code fence consumes the line, so being inside a code block does NOT
    /// protect it. A context line whose trimmed text is <c>:::</c> therefore closes a three-colon container
    /// even when fenced, and every later line drops out of the diff with no error. Diffing any
    /// <c>.charter.md</c> — Charter's own <c>examples/</c>, for one — produces exactly that line. Verified by
    /// rendering, not by reading the parser.
    /// </para>
    /// <para>
    /// Both are only ever as long as the content requires, so an ordinary source diff still emits the plain
    /// <c>:::diff</c> + ` ```diff ` a human would have written by hand.
    /// </para>
    /// </summary>
    internal static string CodeFence(IReadOnlyList<string> bodyLines) => Fence(bodyLines, '`');

    /// <inheritdoc cref="CodeFence"/>
    internal static string ContainerFence(IReadOnlyList<string> bodyLines) => Fence(bodyLines, ':');

    private static string Fence(IReadOnlyList<string> bodyLines, char delimiter)
    {
        var longest = 0;
        foreach (var line in bodyLines)
        {
            var trimmed = line.TrimStart();
            var run = 0;
            while (run < trimmed.Length && trimmed[run] == delimiter)
            {
                run++;
            }

            if (run > longest)
            {
                longest = run;
            }
        }

        return new string(delimiter, Math.Max(3, longest + 1));
    }

    private static string Describe(RecapFile file)
    {
        var kind = file.Change switch
        {
            RecapChange.Added => "added",
            RecapChange.Deleted => "deleted",
            RecapChange.Renamed => "renamed",
            _ => "modified",
        };

        return file.IsBinary ? kind : $"{kind} (+{file.Added} / -{file.Removed})";
    }

    private static IReadOnlyList<string> BuildNotes(IReadOnlyList<RecapFile> files)
    {
        var notes = new List<string>();

        var truncated = files.Where(f => f.OmittedLines > 0).ToList();
        if (truncated.Count > 0)
        {
            var total = truncated.Sum(f => f.OmittedLines);
            notes.Add(
                $"{truncated.Count} file(s) were capped, hiding {total} diff line(s) in total: "
                + string.Join(", ", truncated.Select(f => f.Path)));
        }

        var binary = files.Where(f => f.IsBinary).ToList();
        if (binary.Count > 0)
        {
            notes.Add(
                $"{binary.Count} binary file(s) have no reviewable diff: "
                + string.Join(", ", binary.Select(f => f.Path)));
        }

        return notes;
    }

    // ---------------------------------------------------------------------------------------------------
    // Escaping — a recap is generated from arbitrary repository content, so every interpolated string is a
    // potential markdown injection into the seed. Each helper below is the guard for one context.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Wrap text as inline code with a backtick delimiter long enough to survive backticks INSIDE it
    /// (a path or commit subject may legitimately contain them), padding with spaces per CommonMark when the
    /// content starts or ends with a backtick.</summary>
    private static string Code(string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            if (run > longest)
            {
                longest = run;
            }
        }

        var delimiter = new string('`', longest + 1);
        var pad = text.StartsWith('`') || text.EndsWith('`') ? " " : string.Empty;
        return delimiter + pad + text + pad + delimiter;
    }

    /// <summary>Escape a value for a PIPE TABLE cell: a literal <c>|</c> would open a new column, and a
    /// newline would end the row (and the table).</summary>
    private static string Cell(string text)
        => Inline(text).Replace("|", @"\|", StringComparison.Ordinal);

    /// <summary>Collapse a value to a single line for inline contexts. Escaping every markdown metacharacter
    /// would make headings unreadable; collapsing the one character that breaks the STRUCTURE is enough.</summary>
    private static string Inline(string text)
        => text.Replace("\r\n", " ", StringComparison.Ordinal)
               .Replace('\r', ' ')
               .Replace('\n', ' ')
               .Trim();
}

/// <summary>How a file changed between the two ends of the range.</summary>
public enum RecapChange
{
    /// <summary>Present on both sides, with content changes.</summary>
    Modified,

    /// <summary>Not present on the left side.</summary>
    Added,

    /// <summary>Not present on the right side.</summary>
    Deleted,

    /// <summary>Present on both sides at different paths.</summary>
    Renamed,
}

/// <summary>
/// One file in a recap: its <paramref name="Path"/> on the right-hand side, the <paramref name="OldPath"/> it
/// was renamed from (else <see langword="null"/>), how it changed, its line counts, and the hunk body actually
/// emitted.
/// </summary>
/// <param name="Path">The path as of the right-hand side of the range.</param>
/// <param name="OldPath">The pre-rename path, or <see langword="null"/> when not a rename.</param>
/// <param name="Change">Added / Modified / Deleted / Renamed.</param>
/// <param name="Added">Count of <c>+</c> lines across the WHOLE file's diff, before any cap.</param>
/// <param name="Removed">Count of <c>-</c> lines across the WHOLE file's diff, before any cap.</param>
/// <param name="IsBinary">True when git reported no textual diff.</param>
/// <param name="DiffLines">The hunk lines emitted into the block (already capped).</param>
/// <param name="OmittedLines">Lines the cap removed; <c>0</c> when the file is shown in full.</param>
public sealed record RecapFile(
    string Path,
    string? OldPath,
    RecapChange Change,
    int Added,
    int Removed,
    bool IsBinary,
    IReadOnlyList<string> DiffLines,
    int OmittedLines);

/// <summary>One commit in the range, as the recap's commit table shows it.</summary>
/// <param name="ShortSha">The abbreviated hash.</param>
/// <param name="Subject">The commit subject line.</param>
public sealed record RecapCommit(string ShortSha, string Subject);

/// <summary>
/// The result of <see cref="DiffRecap.Build"/>: the seed <paramref name="Markdown"/> (no version marker — the
/// CLI stamps it), the <paramref name="Files"/> it described, and <paramref name="Notes"/> — the things a
/// reader of the seed alone could not know were incomplete (capped files, binary files). Notes are reported;
/// they are never the difference between a valid and an invalid seed.
/// </summary>
public sealed record RecapResult(string Markdown, IReadOnlyList<RecapFile> Files, IReadOnlyList<string> Notes);
