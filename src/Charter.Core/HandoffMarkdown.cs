namespace Charter.Core;

/// <summary>
/// Converts a reviewed Charter deliverable into canonical plain CommonMark for the Guardrails handoff
/// (invariant 5 — no MDX crosses the handoff). Every <c>:::</c> directive FENCE LINE is rewritten to plain
/// markdown Guardrails can parse — a <c>:::note</c>/<c>:::warn</c> to a labeled blockquote, a
/// <c>:::diagram</c> to a fenced <c>mermaid</c> code block, a <c>:::diff</c> to a fenced <c>diff</c> code
/// block, a <c>:::comparison</c> to its already-plain inner list, and a <c>:::question</c> to a resolved
/// (answered) or clearly-flagged open (unanswered) block. Prose, headings, lists, tables, and fenced code
/// blocks pass through VERBATIM — a mid-sentence mention of directive syntax (e.g. talking <em>about</em>
/// <c>:::note</c> in prose) is not a directive and is never rewritten.
/// </summary>
/// <remarks>
/// STUB (TDD red). <c>Emit</c> throws <see cref="NotImplementedException"/> so the authored
/// HandoffMarkdownTests compile and fail; task <c>05-implement-handoff-markdown</c> fills in the real logic.
/// Do not implement the conversion here.
/// </remarks>
public static class HandoffMarkdown
{
    /// <summary>
    /// Emit the plain-markdown handoff for <paramref name="markdown"/>, resolving each <c>:::question</c>
    /// against <paramref name="answers"/> (question id → the selected/submitted answer value(s), the same
    /// shape as <c>Charter.Server.Answer.Values</c> — a plain dictionary, since Charter.Core does not depend
    /// on Charter.Server). A <c>null</c> lookup, or a question id missing from it, means that question was
    /// never answered and is emitted as a clearly-flagged open question.
    /// </summary>
    public static string Emit(string markdown, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers = null)
        => throw new NotImplementedException();
}
