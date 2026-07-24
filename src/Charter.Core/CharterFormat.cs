using System.Globalization;

namespace Charter.Core;

/// <summary>
/// The status of a <c>.charter.md</c>'s <c>charter-format-version</c> frontmatter marker as judged by
/// <see cref="CharterFormat.ValidateVersionMarker(string)"/>.
/// </summary>
public enum VersionMarkerStatus
{
    /// <summary>The marker is present and its version is within <c>[MinVersion, Version]</c>.</summary>
    Ok,

    /// <summary>No <c>charter-format-version</c> marker is present (the plan was never stamped).</summary>
    Missing,

    /// <summary>A marker is present but its value is not a supported format version (non-integer, or outside
    /// the <c>[MinVersion, Version]</c> range this build understands).</summary>
    Unsupported,
}

/// <summary>
/// The outcome of validating a <c>.charter.md</c>'s <c>charter-format-version</c> marker: the
/// <see cref="Status"/>, the parsed <see cref="Version"/> (null when absent or unparseable), and a
/// human-readable <see cref="Message"/> suitable for a CLI warning line.
/// </summary>
public sealed record VersionMarkerResult(VersionMarkerStatus Status, int? Version, string Message);

/// <summary>
/// The single source of truth for the Charter <c>.charter.md</c> FORMAT version — the current version and the
/// oldest still-supported version — together with the plain-YAML <c>charter-format-version</c> frontmatter
/// marker's validation lint (<see cref="ValidateVersionMarker(string)"/>) and stamp helper
/// (<see cref="EnsureVersionMarker(string)"/>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Version"/> / <see cref="MinVersion"/> are the ONE place these numbers live in code: the
/// <c>charter-format</c> skill declares the same <c>format-version</c> / <c>format-min</c> in its frontmatter,
/// and the drift test binds the skill's declaration to these constants — so a catalog change that bumps the
/// version can never leave the skill, the code, and the test out of step (Architecture B §2.2, §2.4).
/// </para>
/// <para>
/// Charter's job here is to VALIDATE and (optionally) STAMP the marker. The range-<em>enforcement</em> of a
/// file's version against an installed skill is the interactive breakdown session's concern (Guardrails #391),
/// not Charter's — these helpers never load a skill and never touch the filesystem.
/// </para>
/// </remarks>
public static class CharterFormat
{
    /// <summary>The current <c>.charter.md</c> format version this build authors and understands (skillMax).</summary>
    public const int Version = 1;

    /// <summary>The oldest <c>.charter.md</c> format version this build still understands (skillMin).</summary>
    public const int MinVersion = 1;

    /// <summary>The plain-YAML frontmatter key that stamps a plan's format version.</summary>
    public const string MarkerKey = "charter-format-version";

    /// <summary>
    /// Read the <c>charter-format-version</c> frontmatter marker of <paramref name="markdown"/> and judge it:
    /// <see cref="VersionMarkerStatus.Missing"/> when no marker is present,
    /// <see cref="VersionMarkerStatus.Unsupported"/> when a marker is present but its value is not an integer in
    /// <c>[<see cref="MinVersion"/>, <see cref="Version"/>]</c>, and <see cref="VersionMarkerStatus.Ok"/>
    /// otherwise. Deterministic and pure — no I/O, never throws (a pathological document simply reads as
    /// <see cref="VersionMarkerStatus.Missing"/>).
    /// </summary>
    public static VersionMarkerResult ValidateVersionMarker(string markdown)
    {
        var (present, rawValue) = ReadMarker(markdown);
        if (!present)
        {
            return new VersionMarkerResult(
                VersionMarkerStatus.Missing,
                null,
                $"no '{MarkerKey}' frontmatter marker; this plan is not stamped with a Charter format version " +
                $"(expected {MinVersion}..{Version}).");
        }

        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
        {
            return new VersionMarkerResult(
                VersionMarkerStatus.Unsupported,
                null,
                $"'{MarkerKey}: {rawValue}' is not a valid integer format version (supported: {MinVersion}..{Version}).");
        }

        if (version < MinVersion || version > Version)
        {
            return new VersionMarkerResult(
                VersionMarkerStatus.Unsupported,
                version,
                $"'{MarkerKey}: {version}' is outside the supported format range {MinVersion}..{Version}; " +
                "update the charter-format skill or re-author the plan against a current format.");
        }

        return new VersionMarkerResult(VersionMarkerStatus.Ok, version, $"{MarkerKey}: {version}");
    }

    /// <summary>
    /// Ensure <paramref name="markdown"/> carries a <c>charter-format-version</c> marker, surgically and
    /// content-preservingly: if a marker is already present it is returned UNCHANGED (a no-op); if a leading
    /// YAML frontmatter block exists without the key, the key is inserted just before that block's closing
    /// fence; otherwise a fresh <c>---\ncharter-format-version: <see cref="Version"/>\n---</c> block is prepended.
    /// Every other byte — prose, <c>:::</c> blocks, existing frontmatter keys — is preserved.
    /// </summary>
    public static string EnsureVersionMarker(string markdown)
    {
        markdown ??= string.Empty;

        var (present, _) = ReadMarker(markdown);
        if (present)
        {
            return markdown;
        }

        var newline = DetectNewline(markdown);

        // A leading frontmatter block (opening "---" on line 1) without the marker: add the key inside it.
        if (StartsWithFrontMatterFence(markdown))
        {
            var insertAt = ClosingFenceStart(markdown);
            if (insertAt >= 0)
            {
                return markdown[..insertAt] + MarkerKey + ": " + VersionText() + newline + markdown[insertAt..];
            }
        }

        // No usable frontmatter block: prepend a fresh one carrying only the marker.
        return "---" + newline + MarkerKey + ": " + VersionText() + newline + "---" + newline + markdown;
    }

    private static string VersionText() => Version.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Read the raw string value of the <c>charter-format-version</c> key from <paramref name="markdown"/>'s
    /// leading YAML frontmatter block. Returns <c>(false, null)</c> when there is no closed frontmatter block or
    /// the key is absent. Recognizes only the canonical block — an opening <c>---</c> fence on the first line
    /// closed by a later <c>---</c> or <c>...</c> line — which is exactly the shape the Markdig pipeline strips
    /// and <see cref="EnsureVersionMarker(string)"/> writes.
    /// </summary>
    private static (bool Present, string? Value) ReadMarker(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return (false, null);
        }

        var lines = SplitLines(markdown);
        if (lines.Length < 2 || !IsOpenFence(lines[0]))
        {
            return (false, null);
        }

        string? value = null;
        var closed = false;
        for (var i = 1; i < lines.Length; i++)
        {
            if (IsCloseFence(lines[i]))
            {
                closed = true;
                break;
            }

            if (value is null && TryReadKey(lines[i], out var read))
            {
                value = read;
            }
        }

        return closed && value is not null ? (true, value) : (false, null);
    }

    /// <summary>The character index where the leading frontmatter block's closing fence line starts, or -1 when
    /// there is no opening fence or no closing fence. Computed on the ORIGINAL string so a splice preserves the
    /// document's exact bytes and newline style.</summary>
    private static int ClosingFenceStart(string markdown)
    {
        var pos = NextLineStart(markdown, 0);
        while (pos < markdown.Length)
        {
            var contentEnd = LineContentEnd(markdown, pos);
            var content = markdown[pos..contentEnd].Trim();
            if (content is "---" or "...")
            {
                return pos;
            }

            pos = NextLineStart(markdown, pos);
        }

        return -1;
    }

    private static bool StartsWithFrontMatterFence(string markdown)
    {
        var lines = SplitLines(markdown);
        return lines.Length >= 1 && IsOpenFence(lines[0]);
    }

    /// <summary>True when the line is the canonical opening frontmatter fence — exactly three dashes (trimmed).
    /// Kept strict so this reader is never MORE lenient than Markdig's stripper, which would let an unstripped
    /// marker leak into a render.</summary>
    private static bool IsOpenFence(string line) => line.Trim() == "---";

    private static bool IsCloseFence(string line)
    {
        var trimmed = line.Trim();
        return trimmed is "---" or "...";
    }

    private static bool TryReadKey(string line, out string value)
    {
        value = string.Empty;
        var colon = line.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        if (!string.Equals(line[..colon].Trim(), MarkerKey, StringComparison.Ordinal))
        {
            return false;
        }

        value = line[(colon + 1)..].Trim();
        return true;
    }

    private static string[] SplitLines(string markdown)
        => markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    /// <summary>The newline style of <paramref name="markdown"/> — CRLF when the first line break is CRLF, else
    /// LF — so an inserted line matches the document rather than mixing styles.</summary>
    private static string DetectNewline(string markdown)
    {
        var nl = markdown.IndexOf('\n');
        return nl > 0 && markdown[nl - 1] == '\r' ? "\r\n" : "\n";
    }

    /// <summary>The index just past the next <c>\n</c> at or after <paramref name="from"/> (or the string length
    /// when there is none) — the start of the following physical line.</summary>
    private static int NextLineStart(string markdown, int from)
    {
        var nl = markdown.IndexOf('\n', from);
        return nl < 0 ? markdown.Length : nl + 1;
    }

    /// <summary>The index where the physical line starting at <paramref name="from"/> ends — at the first
    /// <c>\r</c> or <c>\n</c>, or the string length.</summary>
    private static int LineContentEnd(string markdown, int from)
    {
        var i = from;
        while (i < markdown.Length && markdown[i] != '\n' && markdown[i] != '\r')
        {
            i++;
        }

        return i;
    }
}
