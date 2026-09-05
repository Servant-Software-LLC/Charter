using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #247 and #248. Bundled SKILLS are the second kind of shipped capability, and every guard the repo
/// had was written for the first kind (verbs), so a skill could ship, install, and be version-stamped while
/// no test and no document knew it existed. <c>charter-drain</c> did exactly that.
///
/// <para>
/// Two independent holes, both closed here:
/// <list type="number">
///   <item><b>#247, the code.</b> <see cref="SkillDriftCheck"/> held a literal <c>{ "charter",
///   "charter-format" }</c> while <see cref="SkillsInstaller"/> DISCOVERED the set from the
///   <c>skills/**</c> resource glob. With all three installed and stale, <c>charter --version</c> reported
///   two.</item>
///   <item><b>#248, the docs.</b> <see cref="DocumentedCommandsTests"/> covers catalog verbs only, so a
///   bundled skill was named in no document a human reads.</item>
/// </list>
/// </para>
/// </summary>
[Trait("Category", "Cli")]
public class BundledSkillsTests
{
    /// <summary>One case per bundled skill, so a failure names the skill rather than the whole set.</summary>
    public static IEnumerable<object[]> BundledSkills =>
        SkillsInstaller.BundledSkillNames.Select(name => new object[] { name });

    /// <summary>
    /// The set is DERIVED, so this cannot fail by drifting — it fails if someone re-introduces a literal that
    /// happens to omit a skill, and it pins the fact the bug was about: the third skill is in the set.
    /// </summary>
    [Fact]
    public void EveryBundledSkillFolderIsDiscovered()
    {
        string[] onDisk = Directory
            .GetDirectories(RepositoryFiles.PathTo("skills"))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()!;

        Assert.Equal(onDisk, SkillsInstaller.BundledSkillNames.OrderBy(n => n, StringComparer.Ordinal));

        // Named explicitly: charter-drain is the one that was missing, and a set assertion alone would go
        // green again the day someone drops it from BOTH sides.
        Assert.Contains("charter-drain", SkillsInstaller.BundledSkillNames);
    }

    /// <summary>
    /// The drift check must ask about every skill the installer WRITES. Falsified by construction: point
    /// <see cref="SkillDriftCheck.FindStaleSkillsIn"/> at a root holding a stale copy of each bundled skill
    /// and require every one of them back. Against the pre-fix literal this fails naming <c>charter-drain</c>.
    /// </summary>
    [Fact]
    public void DriftCheck_SeesEveryBundledSkill()
    {
        string root = Path.Combine(Path.GetTempPath(), "charter-drift-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            foreach (string skill in SkillsInstaller.BundledSkillNames)
            {
                string dir = Path.Combine(root, skill);
                Directory.CreateDirectory(dir);
                File.WriteAllText(
                    Path.Combine(dir, "SKILL.md"),
                    "---\nname: " + skill + "\nmetadata:\n  charter-version: 0.0.1-stale\n---\n\n# " + skill + "\n");
            }

            IReadOnlyList<SkillDriftCheck.StaleSkill> stale =
                SkillDriftCheck.FindStaleSkillsIn(new[] { root }, "9.9.9");

            Assert.Equal(
                SkillsInstaller.BundledSkillNames.OrderBy(n => n, StringComparer.Ordinal),
                stale.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp directory; never fail a passing assertion over it.
            }
        }
    }

    /// <summary>
    /// Every bundled skill must be named in the README's <c>skills install</c> bullet. #138's rule — a
    /// capability its reader cannot find does not exist as far as that reader is concerned — applied to
    /// skills instead of verbs.
    /// </summary>
    /// <remarks>
    /// SCOPED TO THE BULLET, not the whole file, and deliberately. A whole-file <c>Contains</c> would pass
    /// vacuously for the skill literally named <c>charter</c>, whose name occurs on nearly every line of this
    /// README — it would assert nothing for the very skill it names. The bullet describing
    /// <c>charter skills install</c> is also where the fact belongs: it is where a human learns what just
    /// landed on their disk.
    /// </remarks>
    [Theory]
    [MemberData(nameof(BundledSkills))]
    public void ReadmeInstallBullet_NamesEveryBundledSkill(string skill)
    {
        Assert.True(
            SkillsInstallBullet().Contains($"`{skill}`", StringComparison.Ordinal),
            $"README.md's `charter skills install` bullet does not name the bundled skill '{skill}', which "
                + "that command writes to the user's disk and stamps with this tool's version. Name it there "
                + "(backticked) with a clause on what it is for. Charter #248.");
    }

    /// <summary>
    /// The README bullet documenting <c>charter skills install</c>, up to the next verb bullet. Fails loudly
    /// if the bullet cannot be found, rather than returning an empty string every assertion above would pass
    /// against.
    /// </summary>
    private static string SkillsInstallBullet()
    {
        const string marker = "`charter skills install";
        string readme = RepositoryFiles.ReadAllText("README.md");

        int start = readme.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(
            start >= 0,
            "README.md no longer documents `charter skills install`, so this test is scanning nothing. "
                + "DocumentedCommandsTests should have caught that first -- fix the README, do not relax this.");

        int next = readme.IndexOf("\n- `charter ", start, StringComparison.Ordinal);
        return next < 0 ? readme[start..] : readme[start..next];
    }
}
