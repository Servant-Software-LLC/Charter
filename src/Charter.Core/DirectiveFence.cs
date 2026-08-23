namespace Charter.Core;

/// <summary>
/// <b>The ONE definition of a <c>:::</c> directive container's FENCE LINES</b> — what opens one and what
/// closes one — shared by every seam that has to strip a container's own fence pair off its
/// <see cref="Block.RawContent"/>: <see cref="QuestionResolution.QuestionBody"/> (and thus the forensic
/// record, the identity fingerprint and the lints) and <see cref="HandoffMarkdown"/>'s flatten.
/// </summary>
/// <remarks>
/// <para>
/// It exists because those two forked, and the fork was load-bearing TWICE. #172: the flatten recognised an
/// opener only as <c>^:::\w+</c>, so a <c>::::question</c>'s body was unreadable and the handoff DROPPED the
/// question's id, title and target while the record read it perfectly. #190: the same two regexes were still
/// there for every other container, so a <c>::::note</c> flattened with both its fence lines still in the
/// body — and a <c>::::comparison</c> / <c>::::custom-html</c>, which emit their inner lines verbatim, put a
/// live <c>::::</c> directive line at column zero in the plain-CommonMark handoff (invariant 5), while a
/// <c>::::diff</c>'s leaked opener defeated the unwrap and double-fenced the block.
/// </para>
/// <para>
/// <b>Any fence length is ordinary authoring, not a defect.</b> CommonMark directive containers nest by fence
/// length, and <c>charter-format</c> tells an author to open with <c>::::</c> whenever a body line would
/// itself start with <c>:::</c> — a diff OF a Charter plan, a callout carrying a diagram. So the widest
/// recognition is the CORRECT one: these predicates agree with what Markdig parsed and with what a reviewer
/// sees on the rendered page.
/// </para>
/// <para>
/// They are deliberately span-based and allocation-free, and they take a LINE — never a whole document. A
/// caller decides WHICH lines to test; every current caller tests only a container span's first and last
/// line, which is what keeps an indented colon run inside a <c>:::diff</c> body content rather than a fence.
/// </para>
/// </remarks>
internal static class DirectiveFence
{
    /// <summary>The shortest colon run that can fence a directive container.</summary>
    private const int MinimumColons = 3;

    /// <summary>
    /// True when <paramref name="line"/> OPENS a directive container — a run of three or more colons followed
    /// by a directive name (<c>:::note</c>, <c>::::question</c>, <c>:::: diff</c>).
    /// </summary>
    public static bool IsOpen(ReadOnlySpan<char> line)
    {
        var trimmed = line.TrimStart();
        var colons = ColonRun(trimmed);
        return colons >= MinimumColons && trimmed[colons..].Trim().Length > 0;
    }

    /// <summary>
    /// True when <paramref name="line"/> CLOSES a directive container — a line that is nothing but a run of
    /// three or more colons.
    /// </summary>
    public static bool IsClose(ReadOnlySpan<char> line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= MinimumColons && ColonRun(trimmed) == trimmed.Length;
    }

    /// <summary>The length of the leading run of <c>:</c> characters in <paramref name="text"/>.</summary>
    private static int ColonRun(ReadOnlySpan<char> text)
    {
        var colons = 0;
        while (colons < text.Length && text[colons] == ':')
        {
            colons++;
        }

        return colons;
    }
}
