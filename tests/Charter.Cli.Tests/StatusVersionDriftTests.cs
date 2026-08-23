using System.Text.RegularExpressions;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #191 — the drift guard for the one section of <c>charter-domain-knowledge</c> that carries facts
/// with a shelf life.
/// </summary>
/// <remarks>
/// <para>
/// <c>.claude/skills/charter-domain-knowledge/SKILL.md</c> is loaded by EVERY agent working in this repo to
/// get its bearings, and it is marked SELF-UPDATING. Its Status section claimed <c>v0.7.0</c> and a test count
/// of 755 for seventeen releases while master ran on past it, so an agent calibrated to a version already long
/// superseded — and a version number is exactly the kind of fact an agent quotes back with confidence.
/// </para>
/// <para>
/// Rewriting today's numbers in only resets the clock, so the section is now written to a rule: <b>a fact
/// belongs there only if a test binds it, or its shelf life is longer than the gap between edits to the
/// file.</b> These tests are the binding half, and they are deliberately asymmetric — the two facts that
/// rotted fail in different ways and get opposite rulings:
/// </para>
/// <list type="bullet">
/// <item><description>
/// The <b>version</b> is bound. It has exactly one authority (<see cref="CharterVersion"/>, resolved from the
/// built tool's assembly and ultimately from <c>&lt;Version&gt;</c> in <c>Charter.Cli.csproj</c>), so the
/// binding is cheap, and it fires exactly when the fact goes wrong — on a version bump — and at no other
/// time. Same shape as <c>CharterFormatDriftTests</c> (#155) and <c>HeadlessRecordContractTests</c> (#173).
/// </description></item>
/// <item><description>
/// The <b>test count</b> is forbidden, not bound. It has no authority a document can cite — it is the output
/// of a run, and it differs per CI leg (the full solution vs the WebKit browser-only leg). Pinning it would
/// fail on every PR that adds a test: a gate whose only signal is "somebody wrote a test", which trains
/// people to bump the number without reading it. So the guard here is a NEGATIVE one — the fact may not come
/// back.
/// </description></item>
/// </list>
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","StatusVersionDrift")].
/// </remarks>
[Trait("Category", "StatusVersionDrift")]
public class StatusVersionDriftTests
{
    private static readonly string[] SkillPath =
        { ".claude", "skills", "charter-domain-knowledge", "SKILL.md" };

    /// <summary>
    /// A dotted release version as it appears in prose, in BOTH spellings: bare (<c>0.24.0</c>, what
    /// <c>&lt;Version&gt;</c> and <c>charter --version</c> say) and tag-style (<c>v0.24.0</c>, what git and
    /// the GitHub releases say). Matching only the bare form was the first cut and it was blind: <c>\b</c>
    /// finds no boundary between the <c>v</c> and the <c>0</c>, so every <c>v0.7.0</c> in the block this
    /// guard exists to prevent — and every tag reference anyone would naturally write — sailed straight past
    /// it. Caught by mutating the skill and re-running, not by reading the regex.
    /// </summary>
    private static readonly Regex VersionToken =
        new(@"(?<![\w.])v?\d+\.\d+\.\d+\b", RegexOptions.Compiled);

    /// <summary>
    /// A stated test count — the exact shape that rotted: three digits or more, then the word "tests" within
    /// a word or two ("755 tests green", "1097 passing tests"). The lookbehind excludes an issue reference,
    /// so "#173's contract test" is not mistaken for a count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run against <see cref="Plain"/> text, not the raw markdown. The historical line was
    /// <c>**755** tests green</c> — emphasis markers sit between the number and the word, so a gap of
    /// <c>\s+</c> over the raw source misses the one string this guard is named for.
    /// </para>
    /// <para>
    /// Deliberately not airtight — "baseline: 1239 green" would slip past, and no regex closes that. The
    /// guard pairs with <see cref="StatusSection_CarriesTheRuleAndNamesThisGuard"/>: the rule survives in the
    /// prose so a reader knows WHY the number is absent, and this catches the copy-paste that would put it
    /// back.
    /// </para>
    /// </remarks>
    private static readonly Regex StatedTestCount =
        new(@"(?<![#\w.])\d{3,}\s+(?:\w+\s+){0,2}tests?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void StatusSection_StatesTheVersionTheToolActuallyReports()
    {
        string status = StatusSection();
        string current = CharterVersion.Current;

        Assert.True(
            StatedVersions(status).Contains(current, StringComparer.Ordinal),
            $"charter-domain-knowledge's Status section does not state the tool's current version "
                + $"('{current}'). Every agent in this repo loads that file to get its bearings, and the "
                + "version is the fact it will quote back with confidence -- so the stated number and the "
                + "built tool must be one fact, not two copies. Update the Status section.");
    }

    /// <summary>
    /// The anti-rot half, and the assertion that actually prevents a recurrence. The old block named
    /// <c>v0.7.0</c> in five separate sentences — a shipped-in list, an unreleased list, a baseline commit —
    /// so bumping "the version" left four stale copies behind. One bound number is the whole budget: what
    /// shipped in which release is a question for <c>git log</c> and the GitHub releases, which are
    /// authoritative and cannot go stale.
    /// </summary>
    [Fact]
    public void StatusSection_StatesNoVersionOTHERThanTheCurrentOne()
    {
        string status = StatusSection();
        string current = CharterVersion.Current;

        string[] others = StatedVersions(status)
            .Where(value => value != current)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            others.Length == 0,
            "charter-domain-knowledge's Status section names version(s) other than the tool's current "
                + $"'{current}': {string.Join(", ", others)}. Only the bound version may be stated there. A "
                + "second version number is bound to nothing and goes stale on the next release -- name "
                + "`git log`, `git describe --tags` or the GitHub releases instead of quoting a number.");
    }

    /// <summary>
    /// The negative ruling, kept honest by a test. A test count reads as calibration and is decoration:
    /// nothing in this repo is decided differently by a count in the low thousands than by one a hundred
    /// higher, and the number moved by well over a hundred between #191 being filed and being fixed.
    /// </summary>
    [Fact]
    public void StatusSection_StatesNoTestCount()
    {
        string status = StatusSection();

        Match match = StatedTestCount.Match(Plain(status));

        Assert.False(
            match.Success,
            $"charter-domain-knowledge's Status section states a test count ('{match.Value.Trim()}'). That "
                + "fact has no authority a document can cite -- it is the output of a run, and it differs "
                + "between the full-solution CI leg and the WebKit browser-only one. Point at the run and at "
                + ".github/workflows/ci.yml instead; charter-dev-knowledge carries the commands.");
    }

    /// <summary>
    /// The reasoning is the deliverable, not the numbers. A bare bound version with the rule deleted is an
    /// open invitation to re-add a test count beside it, which is how the section rotted the first time — so
    /// the rule and the name of this guard must both survive in the file a reader actually reads.
    /// </summary>
    [Fact]
    public void StatusSection_CarriesTheRuleAndNamesThisGuard()
    {
        string status = StatusSection();

        Assert.Contains("shelf life", status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(StatusVersionDriftTests), status, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    /// <summary>
    /// Every release version the text states, normalised to the bare form so a tag reference (<c>v0.24.0</c>)
    /// and the csproj's own spelling (<c>0.24.0</c>) count as ONE fact rather than two — otherwise naming the
    /// tag would trip the "no second version" guard for saying the same true thing twice.
    /// </summary>
    private static IReadOnlyList<string> StatedVersions(string text) =>
        VersionToken.Matches(Plain(text))
            .Select(match => match.Value.TrimStart('v'))
            .ToArray();

    /// <summary>
    /// The text with markdown emphasis markers removed, so a fact wrapped in <c>**bold**</c>, <c>`code`</c>
    /// or <c>_italics_</c> reads to these guards exactly as the bare fact does. Every stale claim this file
    /// existed to prevent was emphasised — which is what made it worth quoting, and what would otherwise
    /// have hidden it from the scan.
    /// </summary>
    private static string Plain(string text) =>
        new(text.Where(c => c is not ('*' or '`' or '_')).ToArray());

    /// <summary>
    /// The Status section's body: everything from the <c>## Status</c> heading to the next <c>## </c> heading
    /// or end of file. Scoped deliberately — the rest of the skill legitimately names other versions (the
    /// Guardrails <c>1.0.0-preview.48</c> floor in invariant 5), and those are not this guard's business.
    /// </summary>
    private static string StatusSection()
    {
        string[] lines = RepositoryFiles.ReadAllText(SkillPath)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        var body = new List<string>();
        bool inStatus = false;

        foreach (string line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inStatus)
                {
                    break;
                }

                inStatus = line.StartsWith("## Status", StringComparison.Ordinal);
                continue;
            }

            if (inStatus)
            {
                body.Add(line);
            }
        }

        Assert.True(
            body.Count > 0,
            "charter-domain-knowledge/SKILL.md has no '## Status' section -- these guards are bound to it.");

        return string.Join("\n", body);
    }
}
