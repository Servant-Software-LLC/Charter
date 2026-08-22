using Charter.Core;

namespace Charter.Core.Tests;

/// <summary>
/// Test-side helpers for reading a flattened handoff, now that <see cref="HandoffMarkdown.Emit"/> appends the
/// in-band provenance stamp (<c>&lt;!-- charter: plan-sha256=… --&gt;</c>, Charter #172/#187).
/// </summary>
/// <remarks>
/// The stamp is a real CommonMark block, so every assertion of the form "the flatten of this one directive is
/// exactly ONE block" now sees two. Those assertions are about the CONTENT — no fence leak, no early close, no
/// duplicated title — so they filter the stamp out here rather than each restating what the stamp looks like.
/// A test that is genuinely about the stamp asserts on the raw output instead (see <c>HandoffStampTests</c>).
/// </remarks>
internal static class HandoffOutput
{
    /// <summary>The flattened plan's blocks with the provenance stamp removed.</summary>
    public static IReadOnlyList<Block> ContentBlocks(string output)
        => BlockDocument.Parse(output).Blocks
            .Where(block => !block.RawContent.Contains(HandoffMarkdown.StampPrefix, StringComparison.Ordinal))
            .ToList();

    /// <summary>The flattened plan's text with the provenance stamp (and its blank line) removed.</summary>
    public static string WithoutStamp(string output)
    {
        var normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n')
            .Where(line => !line.Contains(HandoffMarkdown.StampPrefix, StringComparison.Ordinal))
            .ToList();

        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join("\n", lines);
    }
}
