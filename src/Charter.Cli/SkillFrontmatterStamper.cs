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
/// The symmetric reader, <see cref="ReadVersion"/>, parses the same shape back out (the staleness check on
/// <c>charter --version</c> reads what INSTALL wrote), so read and write of the field stay in one place.
///
/// Pure (string in, string out): the install step and unit tests exercise identical logic. Mirrors
/// Guardrails' <c>SkillFrontmatterStamper</c>.
/// </summary>
internal static class SkillFrontmatterStamper
{
    /// <summary>The frontmatter key carrying the tool version (under <c>metadata:</c>).</summary>
    public const string VersionKey = "charter-version";

    /// <summary>
    /// The placeholder an authored skill BODY carries where its own version belongs (Charter #152). Install
    /// replaces every occurrence with the tool version.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The frontmatter stamp answers "what is on DISK", which is what <c>charter --version</c> compares
    /// against the binary. It cannot answer the question that actually bites: <b>what did this session
    /// LOAD?</b> Frontmatter is discovery metadata and does not reliably reach the agent's context, so a
    /// skill has no way to read its own loaded version — and a session holding a copy from before the last
    /// <c>charter skills install</c> runs it indefinitely while disk and binary agree perfectly.
    /// </para>
    /// <para>
    /// A stamp in the BODY is prose the agent has definitely read, because reading the body is what loading
    /// a skill means. That makes the loaded version self-reportable, and the three-way comparison possible.
    /// </para>
    /// </remarks>
    public const string BodyVersionPlaceholder = "@CHARTER_VERSION@";

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
        string[] lines = SplitLines(content);

        if (!TryFindFrontmatterFence(lines, out int closeFence))
        {
            // No frontmatter to stamp — but a body placeholder still must be filled, or the skill would
            // report its own version as the literal placeholder.
            return content.Replace(BodyVersionPlaceholder, version, StringComparison.Ordinal);
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
            // Body only: the placeholder is filled BELOW the frontmatter fence, so a `description:` that
            // happened to quote it stays verbatim rather than being silently rewritten.
            result.Add(lines[i].Replace(BodyVersionPlaceholder, version, StringComparison.Ordinal));
        }

        return string.Join(newline, result);
    }

    /// <summary>
    /// Read <c>metadata.charter-version</c> back out of <paramref name="content"/>'s frontmatter — the exact
    /// value <see cref="Stamp"/> wrote — or <c>null</c> when there is no frontmatter fence, no
    /// <c>metadata:</c> block, or no <c>charter-version:</c> child within it. Parses the same line-oriented
    /// shape the stamper produces, so the staleness check reads precisely what install stamped.
    /// </summary>
    public static string? ReadVersion(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        string[] lines = SplitLines(content);
        if (!TryFindFrontmatterFence(lines, out int closeFence))
        {
            return null;
        }

        var frontmatter = new List<string>();
        for (int i = 1; i < closeFence; i++)
        {
            frontmatter.Add(lines[i]);
        }

        int metadataLine = FindTopLevelKeyLine(frontmatter, MetadataKey);
        if (metadataLine < 0)
        {
            return null;
        }

        // Scan the metadata block's children (until the next top-level key), mirroring exactly where
        // SetChildUnderMetadata WRITES the child.
        for (int i = metadataLine + 1; i < frontmatter.Count; i++)
        {
            string line = frontmatter[i];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                break; // next top-level key: the metadata block held no charter-version child
            }

            string trimmed = line.TrimStart();
            if (trimmed.StartsWith(VersionKey + ":", StringComparison.Ordinal))
            {
                return trimmed[(VersionKey.Length + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>Split into lines, stripping the trailing <c>'\r'</c> the <c>'\n'</c> split leaves under CRLF,
    /// so CRLF and LF frontmatter parse identically.</summary>
    private static string[] SplitLines(string content)
    {
        string[] lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].EndsWith('\r'))
            {
                lines[i] = lines[i][..^1];
            }
        }

        return lines;
    }

    /// <summary>
    /// Locate the closing <c>---</c> of a leading frontmatter fence. Returns <c>false</c> when there is no
    /// opening <c>---</c> line or no closing one (an unclosed fence is left untouched rather than corrupted).
    /// </summary>
    private static bool TryFindFrontmatterFence(string[] lines, out int closeFence)
    {
        closeFence = -1;
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return false;
        }

        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                closeFence = i;
                return true;
            }
        }

        return false;
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
