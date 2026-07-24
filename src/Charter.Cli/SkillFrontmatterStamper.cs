namespace Charter.Cli;

/// <summary>
/// Injects <c>metadata.charter-version</c> into a skill's <c>SKILL.md</c> frontmatter. The version is a
/// release fact, not an author-typed value, so <see cref="SkillsInstaller"/> stamps it into each INSTALLED
/// copy at install time — the bundled (embedded) source stays unstamped, since a <c>PackAsTool</c> package
/// ships a fresh <c>dotnet publish</c> that would otherwise carry a stale build-time stamp. A later
/// staleness check reads the same key back.
///
/// The transform is a surgical, line-oriented edit of the leading <c>---</c>-fenced YAML block: it
/// preserves every other key and their order (notably a multiline <c>description:</c>). Three cases:
/// a <c>metadata:</c> block with a <c>charter-version:</c> child (replaced in place); a <c>metadata:</c>
/// block without it (child inserted at the top of the block); no <c>metadata:</c> block (one appended to
/// the end of the frontmatter). A file with no leading frontmatter fence is returned unchanged.
///
/// Pure (string in, string out): the install step and unit tests exercise identical logic. Mirrors
/// Guardrails' <c>SkillFrontmatterStamper</c>.
/// </summary>
internal static class SkillFrontmatterStamper
{
    /// <summary>The frontmatter key carrying the tool version (under <c>metadata:</c>).</summary>
    public const string VersionKey = "charter-version";

    /// <summary>The top-level YAML key whose child is <see cref="VersionKey"/>.</summary>
    public const string MetadataKey = "metadata";

    /// <summary>
    /// Return <paramref name="content"/> with <c>metadata.charter-version</c> set to
    /// <paramref name="version"/>, preserving the original newline style and every other frontmatter key.
    /// Files without a leading <c>---</c> fence are returned verbatim.
    /// </summary>
    public static string Stamp(string content, string version)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(version);

        string newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = content.Split('\n');

        // Strip the trailing '\r' the '\n' split leaves under CRLF; the original newline is reattached on join.
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].EndsWith('\r'))
            {
                lines[i] = lines[i][..^1];
            }
        }

        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return content; // no frontmatter fence — nothing to stamp
        }

        int closeFence = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                closeFence = i;
                break;
            }
        }

        if (closeFence < 0)
        {
            return content; // opening fence with no close — leave it untouched rather than corrupt it
        }

        var frontmatter = new List<string>();
        for (int i = 1; i < closeFence; i++)
        {
            frontmatter.Add(lines[i]);
        }

        List<string> stamped = StampFrontmatterLines(frontmatter, version);

        var result = new List<string>(lines.Length + 2) { lines[0] };
        result.AddRange(stamped);
        result.Add(lines[closeFence]);
        for (int i = closeFence + 1; i < lines.Length; i++)
        {
            result.Add(lines[i]);
        }

        return string.Join(newline, result);
    }

    private static List<string> StampFrontmatterLines(List<string> frontmatter, string version)
    {
        int metadataLine = FindTopLevelKeyLine(frontmatter, MetadataKey);
        if (metadataLine < 0)
        {
            var appended = new List<string>(frontmatter)
            {
                $"{MetadataKey}:",
                $"  {VersionKey}: {version}",
            };
            return appended;
        }

        return SetChildUnderMetadata(frontmatter, metadataLine, version);
    }

    /// <summary>
    /// Index of a top-level (column-0) <c>key:</c> line, or -1. Indented lines are children of a previous
    /// key (e.g. a multiline description) and are skipped.
    /// </summary>
    private static int FindTopLevelKeyLine(IReadOnlyList<string> lines, string key)
    {
        string prefix = key + ":";
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            if (line.TrimEnd() == prefix || line.StartsWith(prefix + " ", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// With a <c>metadata:</c> block at <paramref name="metadataLine"/>, replace an existing
    /// <c>charter-version:</c> child in place, or insert one as the first child of the block (matching the
    /// block's child indentation).
    /// </summary>
    private static List<string> SetChildUnderMetadata(List<string> frontmatter, int metadataLine, string version)
    {
        int firstChild = metadataLine + 1;
        int afterBlock = firstChild;
        while (afterBlock < frontmatter.Count)
        {
            string line = frontmatter[afterBlock];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                break; // next top-level key
            }

            afterBlock++;
        }

        for (int i = firstChild; i < afterBlock; i++)
        {
            string trimmed = frontmatter[i].TrimStart();
            if (trimmed.StartsWith(VersionKey + ":", StringComparison.Ordinal))
            {
                string indent = frontmatter[i][..(frontmatter[i].Length - trimmed.Length)];
                var replaced = new List<string>(frontmatter);
                replaced[i] = $"{indent}{VersionKey}: {version}";
                return replaced;
            }
        }

        string childIndent = "  ";
        if (firstChild < afterBlock)
        {
            string firstChildLine = frontmatter[firstChild];
            string firstTrimmed = firstChildLine.TrimStart();
            childIndent = firstChildLine[..(firstChildLine.Length - firstTrimmed.Length)];
        }

        var inserted = new List<string>(frontmatter);
        inserted.Insert(firstChild, $"{childIndent}{VersionKey}: {version}");
        return inserted;
    }
}
