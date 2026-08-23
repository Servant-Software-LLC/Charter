using Charter.Core;

namespace Charter.Core.Tests;

/// <summary>
/// Test-side helpers for reading a flattened handoff, now that <see cref="HandoffMarkdown.Emit"/> appends the
/// in-band provenance stamp (<c>&lt;!-- charter: plan-sha256=… --&gt;</c>, Charter #172/#187).
/// </summary>
/// <remarks>
/// The stamps are real CommonMark blocks, so every assertion of the form "the flatten of this one directive is
/// exactly ONE block" now sees more. Those assertions are about the CONTENT — no fence leak, no early close, no
/// duplicated title — so they filter the stamps out here rather than each restating what a stamp looks like.
/// A test that is genuinely about a stamp asserts on the raw output instead (<c>HandoffProvenanceTests</c>).
/// There are TWO since Charter #187 — <c>answers-sha256</c> and <c>plan-sha256</c> — and both are filtered from
/// the ONE list below, so a third would be a one-line change here rather than a sweep of every caller.
/// </remarks>
internal static class HandoffOutput
{
    /// <summary>Every provenance-stamp prefix the flatten appends.</summary>
    private static readonly string[] StampPrefixes =
    {
        HandoffMarkdown.StampPrefix, HandoffMarkdown.AnswersStampPrefix,
    };

    /// <summary>The flattened plan's blocks with the provenance stamps removed.</summary>
    public static IReadOnlyList<Block> ContentBlocks(string output)
        => BlockDocument.Parse(output).Blocks
            .Where(block => !IsStamp(block.RawContent))
            .ToList();

    /// <summary>The flattened plan's text with the provenance stamps (and their blank lines) removed.</summary>
    public static string WithoutStamp(string output)
    {
        var normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n')
            .Where(line => !IsStamp(line))
            .ToList();

        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join("\n", lines);
    }

    /// <summary>True when <paramref name="text"/> carries any provenance stamp.</summary>
    private static bool IsStamp(string text)
        => StampPrefixes.Any(prefix => text.Contains(prefix, StringComparison.Ordinal));
}
