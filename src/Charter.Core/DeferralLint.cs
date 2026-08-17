using System.Text.RegularExpressions;

namespace Charter.Core;

/// <summary>
/// The untracked-deferral lint (Charter #156): paragraphs that defer work without naming anything that
/// tracks it.
/// </summary>
/// <remarks>
/// <para>
/// Deferring work inside a plan is normal and good — it is how a stage stays shippable. But a deferral
/// written in prose has no owner and no expiry: the plan is handed off, executed and archived, and the
/// deferred item survives only as a sentence in a document nobody re-reads. On the page,
/// <i>"deferred to #228, which is open"</i> and <i>"deferred into the void"</i> look identical, so a reviewer
/// has to think to interrogate the silence. One did, which is why this exists.
/// </para>
/// <para>
/// <b>Charter deliberately does NOT check whether the referenced issue exists, is open, or is about the
/// right thing.</b> That is a network lookup against one vendor's tracker, and it would drag auth, rate
/// limits, pagination and offline behaviour into a binary whose whole posture is that it reads git and
/// writes files. The division of labour is the project's standing one — the LLM lives in the agent, never in
/// the binary: Charter owns the CONVENTION (a deferral names its tracking issue) and the PROMPT (a warning
/// when one does not); the authoring agent owns the LOOKUP, using whatever tooling it already has. That also
/// makes the convention work for GitLab, Jira, Azure Boards or a text file, none of which a GitHub
/// integration would have served.
/// </para>
/// <para>
/// <b>Deliberately conservative.</b> A lint that cries wolf is one people learn to skip, and this one has no
/// way to be sure a sentence is a deferral. It fires only on phrases that are hard to write by accident, and
/// it skips headings — <c>## Scope / non-goals</c> is a section label, not a deferral, and warning about it
/// would train the reader to ignore the warning that follows.
/// </para>
/// </remarks>
public static class DeferralLint
{
    /// <summary>
    /// Phrases that mark work being pushed out of the current plan. Kept tight on purpose: each is awkward to
    /// write unless you mean it, so a hit is very likely a real deferral.
    /// </summary>
    private static readonly Regex DeferralPhrase = new(
        @"\b(out of scope|not in scope|defer(?:red|ring|s)?\s+(?:to|until|out)|is deferred|are deferred|non-goals?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// What counts as naming a tracker: a <c>#123</c> reference, or any URL that looks like an issue/ticket.
    /// Vendor-agnostic by construction — the point is that SOMETHING is named, not that GitHub was used.
    /// </summary>
    private static readonly Regex IssueReference = new(
        @"(#\d+)|(https?://\S*(issue|ticket|browse|workitem)\S*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The 1-based line number and text of each paragraph in <paramref name="markdown"/> that defers work
    /// without naming anything tracking it, in document order.
    /// </summary>
    /// <remarks>
    /// Works on blank-line-separated paragraphs of the RAW markdown rather than on parsed blocks, so it sees
    /// prose and directive bodies alike — a deferral is as likely to be written inside a <c>:::note</c> as in
    /// a paragraph, and both deserve the same question.
    /// </remarks>
    public static IReadOnlyList<UntrackedDeferral> Find(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return Array.Empty<UntrackedDeferral>();
        }

        var found = new List<UntrackedDeferral>();
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // Reported per PARAGRAPH so the warning points at the sentence that defers; judged per SECTION so a
        // deferral counts as tracked when its section names an issue anywhere. Calibrated against real
        // charters, where the two differed: a numbered list said "out of scope here" while its `#223` sat in
        // the sentence immediately above it, and paragraph-scoped judging called that untracked. A human
        // reading the section plainly sees the tracking, so the lint has to read it the same way.
        //
        // The trade, stated plainly: a long section citing one unrelated issue can mask a genuinely
        // untracked deferral inside it. That is the right way to be wrong — a lint people learn to skip
        // catches nothing at all, and this one can never be certain a sentence is a deferral.
        foreach (var (sectionStart, sectionEnd) in Sections(lines))
        {
            var sectionText = string.Join("\n", lines[(sectionStart - 1)..(sectionEnd - 1)]);
            if (IssueReference.IsMatch(sectionText))
            {
                continue;
            }

            foreach (var (startLine, paragraph) in Paragraphs(lines, sectionStart, sectionEnd))
            {
                // A heading is a section LABEL, not a statement that defers anything. `## Scope / non-goals`
                // is the canonical false positive, and warning about it would spend attention on nothing.
                if (IsHeading(paragraph[0]))
                {
                    continue;
                }

                var text = string.Join("\n", paragraph);
                if (DeferralPhrase.IsMatch(text))
                {
                    found.Add(new UntrackedDeferral(startLine, Excerpt(text)));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// A markdown ATX heading: one to six <c>#</c> then whitespace. The space is load-bearing — a line
    /// wrapped so it begins <c>#223, where a judge…</c> is an ISSUE REFERENCE, and reading it as a heading
    /// split the section in two and hid the very reference that proved the deferral was tracked. Found by
    /// running the lint over real charters, not by reasoning about it.
    /// </summary>
    private static bool IsHeading(string line)
    {
        var t = line.TrimStart();
        var hashes = 0;
        while (hashes < t.Length && t[hashes] == '#')
        {
            hashes++;
        }

        return hashes is >= 1 and <= 6 && hashes < t.Length && char.IsWhiteSpace(t[hashes]);
    }

    /// <summary>Heading-to-heading spans (1-based, End exclusive); text before the first heading is a section.</summary>
    private static IEnumerable<(int Start, int End)> Sections(string[] lines)
    {
        var start = 1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsHeading(lines[i]) || i + 1 == start)
            {
                continue;
            }

            yield return (start, i + 1);
            start = i + 1;
        }

        if (start <= lines.Length)
        {
            yield return (start, lines.Length + 1);
        }
    }

    private static IEnumerable<(int StartLine, List<string> Lines)> Paragraphs(
        string[] lines, int sectionStart, int sectionEnd)
    {
        var paragraph = new List<string>();
        var startLine = sectionStart;

        for (var i = sectionStart - 1; i < sectionEnd - 1 && i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
            {
                if (paragraph.Count > 0)
                {
                    yield return (startLine, new List<string>(paragraph));
                    paragraph.Clear();
                }

                continue;
            }

            if (paragraph.Count == 0)
            {
                startLine = i + 1;
            }

            paragraph.Add(lines[i]);
        }

        if (paragraph.Count > 0)
        {
            yield return (startLine, paragraph);
        }
    }

    /// <summary>A single line of the paragraph, short enough for a console warning to stay readable.</summary>
    private static string Excerpt(string text)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= 120 ? flat : flat[..117] + "...";
    }
}

/// <summary>One paragraph that defers work with nothing named to track it.</summary>
/// <param name="SourceLine">1-based line where the paragraph starts, so a reviewer can go straight to it.</param>
/// <param name="Excerpt">The paragraph, flattened and truncated for a one-line warning.</param>
public sealed record UntrackedDeferral(int SourceLine, string Excerpt);
