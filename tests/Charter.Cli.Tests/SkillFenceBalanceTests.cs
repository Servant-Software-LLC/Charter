using System.Text.RegularExpressions;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #207 — the bundled skills are SHIPPED ARTIFACTS, and a mis-nested code fence breaks them
/// silently.
/// </summary>
/// <remarks>
/// <para>
/// <c>skills/charter/references/authoring-plans.md</c> wrapped a worked example of a whole
/// <c>.charter.md</c> in a THREE-backtick fence, and the <c>:::diagram</c> inside that example contains a
/// <c>```mermaid</c> block. CommonMark closes a fence on the first line of the same character whose run is
/// at least as long and which carries no info string — so the inner block's closing fence closed the OUTER
/// example. Everything after it escaped: the <c>:::</c> that should have closed the diagram rendered as a
/// live "Unknown directive" box which swallowed a <c>## Decisions we need from you</c> heading and a whole
/// <c>:::question</c>, the following <c>:::warn</c> rendered as a real warn block, and the example's closing
/// fence — now an OPENER — swallowed the paragraph of real instructions after it and rendered them as code.
/// </para>
/// <para>
/// That is worse than a formatting slip because this file is loaded by an agent to learn how to author a
/// plan. An agent reading it saw what looked like an actual question block in its own instructions rather
/// than an illustration of one, and the closing guidance as an inert code sample. It is the shape that
/// produces a confidently wrong imitation.
/// </para>
/// <para>
/// <b>Why a guard and not just a fix.</b> The defect leaves the file rendering — it just renders wrongly —
/// so nothing failed, nothing warned, and no reviewer reading the source noticed. The same file already
/// demonstrated the fix ninety lines earlier (a four-backtick outer fence around a <c>```mermaid</c>), which
/// is the tell that this is a class the corpus reproduces rather than a one-off typo. Checking it is a
/// deterministic walk over a dozen files that binds no meaning — far cheaper than the drift guards either
/// side of it (<c>DocumentedCommandsTests</c>, <c>AgentsGuidanceTests</c>, <c>StatusVersionDriftTests</c>).
/// </para>
/// <para>
/// <b>Why this is not a backtick count.</b> A count misfires on exactly the shape the fix uses — a wide
/// outer fence legitimately holding a narrower inner one — so it would condemn the correct examples in this
/// very file. <see cref="MarkdownFences"/> runs the real CommonMark fenced-block state machine instead and
/// asks a question a count cannot: is this fence line INSIDE a block, and is its run at least as long as the
/// fence that opened that block? A four-backtick outer holding <c>```mermaid</c> answers no and is silent.
/// </para>
/// <para>
/// <b>The one shape it flags that still renders correctly</b>, stated so nobody has to rediscover it: an
/// outer fence holding a SINGLE <c>```lang</c> line with no matching bare close before the outer's own. That
/// renders as intended today — but only because nothing closed it, and one more line of example makes it
/// #207 again. The remedy the guard names (widen the outer fence) never changes rendering, so being told
/// about that shape costs nothing and removes the trap. No file in the repo is currently that shape.
/// </para>
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","SkillFenceBalance")].
/// </remarks>
[Trait("Category", "SkillFenceBalance")]
public class SkillFenceBalanceTests
{
    /// <summary>
    /// Every markdown file an agent loads as a skill: <c>skills/</c>, the tree <c>charter skills install</c>
    /// ships, and <c>.claude/skills/</c>, the knowledge skills every agent working in this repo loads to get
    /// its bearings. Both are tracked, so both resolve in a fresh clone and on CI. The failure mode is the
    /// same on either side — an agent reading leaked markdown as instruction — and #191 is the standing
    /// evidence that what those repo-local files say is believed and quoted back.
    /// </summary>
    private static IReadOnlyList<string> SkillDocumentPaths() =>
        new[] { RepositoryFiles.PathTo("skills"), RepositoryFiles.PathTo(".claude", "skills") }
            .SelectMany(root => Directory.GetFiles(root, "*.md", SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(RepositoryFiles.Root(), path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    public static TheoryData<string> SkillDocuments()
    {
        var data = new TheoryData<string>();
        foreach (string path in SkillDocumentPaths())
        {
            data.Add(path);
        }

        return data;
    }

    /// <summary>
    /// The theories below iterate a discovered file set, so an enumeration that silently found nothing would
    /// leave them green over an unchecked corpus. Pin that the sweep reaches the file #207 was filed against
    /// and the skill an agent actually loads.
    /// </summary>
    [Fact]
    public void TheSweep_ReachesBothSkillTrees()
    {
        IReadOnlyList<string> documents = SkillDocumentPaths();

        Assert.Contains("skills/charter/SKILL.md", documents);
        Assert.Contains("skills/charter/references/authoring-plans.md", documents);
        Assert.Contains(".claude/skills/charter-dev-knowledge/SKILL.md", documents);
        Assert.True(
            documents.Count >= 5,
            $"Only {documents.Count} skill markdown file(s) found. The sweep resolved the wrong directory, "
                + "and every fence theory below is passing over an empty set.");
    }

    [Theory]
    [MemberData(nameof(SkillDocuments))]
    public void EverySkillDocument_NestsItsFencesInAWiderOuterFence(string relativePath)
    {
        var findings = Check(relativePath, FenceDefect.NarrowOuterFence);

        Assert.True(
            findings.Count == 0,
            $"{relativePath} opens a code fence that is not wide enough to hold the fence nested inside it, "
                + "so CommonMark closes the OUTER block early and everything after it escapes as live "
                + "markdown (Charter #207). This renders — it just renders wrongly, which is why no other "
                + "test catches it.\n"
                + Describe(findings)
                + "\nRemedy: widen the OUTER fence so its run is strictly longer than every fence inside it "
                + "— four backticks around a ```mermaid or ```diff example. skills/charter/references/"
                + "authoring-plans.md has worked examples of the correct shape.");
    }

    [Theory]
    [MemberData(nameof(SkillDocuments))]
    public void EverySkillDocument_ClosesEveryFenceItOpens(string relativePath)
    {
        var findings = Check(relativePath, FenceDefect.UnclosedFence);

        Assert.True(
            findings.Count == 0,
            $"{relativePath} ends while still inside a code block, so the tail of the file renders as code "
                + "rather than as the prose it was written to be.\n"
                + Describe(findings)
                + "\nRemedy: close the fence, or widen an outer fence that a nested one closed early.");
    }

    /// <summary>
    /// The falsification: Charter #207 put back byte for byte, on the real shipped artifact rather than a
    /// fixture.
    /// <para>
    /// Both halves matter. The CONTROL asserts the file as shipped produces nothing — without it a checker
    /// that always reported a finding would sail through the mutation and the theories above would be
    /// noise. The MUTATION narrows the two fences bounding the sample skeleton back to three backticks,
    /// which is exactly the edit that fixed the bug, reversed. The guard must then name BOTH symptoms the
    /// defect actually had: the inner <c>```mermaid</c> the example can no longer hold, and the tail of the
    /// file left inside a code block by the example's own closing fence turning into an opener.
    /// </para>
    /// <para>
    /// Only those two lines are mutated, deliberately. Narrowing every fence in the file also turns the
    /// guard red, but from the FIRST example (a <c>```diff</c> ninety lines earlier), and the cascade shifts
    /// the block state so far that line 214 is never reached — a mutation that proves the guard fires while
    /// saying nothing about the defect it was built for.
    /// </para>
    /// </summary>
    [Fact]
    public void NarrowingTheSampleSkeletonsOuterFence_TurnsTheGuardRed()
    {
        string[] shipped = File.ReadAllLines(
            RepositoryFiles.PathTo("skills", "charter", "references", "authoring-plans.md"));

        // Control: as shipped, silent.
        Assert.Empty(MarkdownFences.Check(shipped));

        string[] mutated = NarrowTheFencesAround("## A sample `.charter.md` skeleton", shipped);
        var findings = MarkdownFences.Check(mutated);

        Assert.Contains(
            findings,
            finding => finding.Kind == FenceDefect.NarrowOuterFence && finding.Text.Trim() == "```mermaid");
        Assert.Contains(findings, finding => finding.Kind == FenceDefect.UnclosedFence);
    }

    /// <summary>
    /// Returns a copy of <paramref name="lines"/> with the two four-backtick fences that bound the example
    /// under <paramref name="heading"/> narrowed to three — the #207 shape. Fails loudly rather than
    /// silently mutating nothing if the file is ever restructured, because a mutation test that quietly
    /// stops mutating is a green test guarding nothing.
    /// </summary>
    private static string[] NarrowTheFencesAround(string heading, string[] lines)
    {
        int headingIndex = Array.IndexOf(lines, heading);
        Assert.True(headingIndex >= 0, $"authoring-plans.md no longer contains the heading '{heading}'.");

        int opening = Array.IndexOf(lines, "````", headingIndex);
        Assert.True(opening > 0, $"No four-backtick fence follows '{heading}'.");

        int closing = Array.IndexOf(lines, "````", opening + 1);
        Assert.True(closing > opening, $"The example under '{heading}' has no four-backtick closing fence.");

        string[] mutated = [.. lines];
        mutated[opening] = "```";
        mutated[closing] = "```";
        return mutated;
    }

    /// <summary>
    /// The precision half. A guard over shipped docs that cries wolf gets deleted, so pin the two shapes it
    /// must stay silent about: a wide outer fence holding a narrower inner one (the fix #207 applied), and a
    /// fence character that simply differs from the one that opened the block.
    /// </summary>
    [Fact]
    public void AWiderOuterFence_AndAForeignFenceCharacter_AreBothSilent()
    {
        Assert.Empty(MarkdownFences.Check(
        [
            "````",
            ":::diagram",
            "```mermaid",
            "flowchart LR",
            "```",
            ":::",
            "````",
        ]));

        Assert.Empty(MarkdownFences.Check(
        [
            "~~~",
            "```mermaid",
            "```",
            "~~~",
        ]));
    }

    private static IReadOnlyList<FenceFinding> Check(string relativePath, FenceDefect kind) =>
        MarkdownFences
            .Check(File.ReadAllLines(Path.Combine(RepositoryFiles.Root(), relativePath)))
            .Where(finding => finding.Kind == kind)
            .ToList();

    private static string Describe(IReadOnlyList<FenceFinding> findings) =>
        string.Join("\n", findings.Select(finding => $"  line {finding.Line}: {finding.Detail}"));
}

internal enum FenceDefect
{
    /// <summary>
    /// A fence line INSIDE a code block whose run is at least as long as the fence that opened it. Either it
    /// already closed that block early, or it is an opener whose own partner will.
    /// </summary>
    NarrowOuterFence,

    /// <summary>The file ends while still inside a code block.</summary>
    UnclosedFence,
}

internal readonly record struct FenceFinding(FenceDefect Kind, int Line, string Text, string Detail);

/// <summary>
/// The CommonMark fenced-code-block state machine, reduced to the two questions Charter #207 needs answered.
/// A fence opens on <c>^ {0,3}(`{3,}|~{3,})</c> — and a BACKTICK opener's info string may not itself contain
/// a backtick. It closes only on the same character, a run at least as long, and NO info string; that last
/// clause is the whole of #207, because it is why <c>```mermaid</c> does not close a <c>```</c> block but the
/// bare <c>```</c> three lines later does.
/// </summary>
internal static class MarkdownFences
{
    private static readonly Regex FenceLine = new(@"^ {0,3}(?<fence>`{3,}|~{3,})(?<info>.*)$");

    public static IReadOnlyList<FenceFinding> Check(IReadOnlyList<string> lines)
    {
        var findings = new List<FenceFinding>();
        bool open = false;
        char openChar = '\0';
        int openRun = 0;
        int openLine = 0;

        for (int index = 0; index < lines.Count; index++)
        {
            Match match = FenceLine.Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            string fence = match.Groups["fence"].Value;
            char character = fence[0];
            int run = fence.Length;
            string info = match.Groups["info"].Value;

            if (!open)
            {
                // An info string containing a backtick disqualifies a backtick opener, so this is prose.
                if (character == '`' && info.Contains('`', StringComparison.Ordinal))
                {
                    continue;
                }

                (open, openChar, openRun, openLine) = (true, character, run, index + 1);
                continue;
            }

            if (character != openChar || run < openRun)
            {
                // Not a candidate to close this block, and too narrow to be confused for one: ordinary
                // content. This is the branch that keeps a ```mermaid inside a ```` block silent.
                continue;
            }

            if (info.Trim().Length == 0)
            {
                open = false;
                continue;
            }

            findings.Add(new FenceFinding(
                FenceDefect.NarrowOuterFence,
                index + 1,
                lines[index],
                $"'{lines[index].Trim()}' is a run of {run} '{openChar}' inside a block opened at line "
                    + $"{openLine} with a run of only {openRun}, so that block cannot hold it"));
        }

        if (open)
        {
            findings.Add(new FenceFinding(
                FenceDefect.UnclosedFence,
                openLine,
                lines[openLine - 1],
                $"the code block opened at line {openLine} is never closed"));
        }

        return findings;
    }
}
