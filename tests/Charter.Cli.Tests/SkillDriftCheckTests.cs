using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Hermetic tests for the skill-version-drift check (Charter #32) surfaced on <c>charter --version</c>. They
/// exercise the testable core <see cref="SkillDriftCheck.FindStaleSkillsIn"/> against a TEMP skills dir so
/// they never touch the developer's real <c>~/.claude</c>. The load-bearing case installs skills with the
/// REAL binary (proving the reader parses what INSTALL stamped, per the Guardrails #169/#170 lesson) then
/// tampers one stamp to an old version — the check must report exactly that skill as stale. Matching, absent,
/// and unstamped installs report nothing.
/// </summary>
[Trait("Category", "Cli")]
public class SkillDriftCheckTests
{
    [Fact]
    public void FindStaleSkills_FreshInstall_AllMatching_ReportsNone()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string target = Path.Combine(workDir, "skills");
            var install = CharterCliRunner.Run("skills", "install", "--target", target);
            Assert.Equal(0, install.ExitCode);

            // Freshly installed skills carry THIS tool's stamp, so nothing is stale.
            var stale = SkillDriftCheck.FindStaleSkillsIn(new[] { target }, CharterVersion.Current);
            Assert.Empty(stale);
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void FindStaleSkills_OneTamperedStamp_ReportsExactlyThatSkill_WithVersionAndDirectory()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string target = Path.Combine(workDir, "skills");
            var install = CharterCliRunner.Run("skills", "install", "--target", target);
            Assert.Equal(0, install.ExitCode);

            // Rewrite charter-format's stamp to an OLD version, using the same stamper install uses so the
            // frontmatter shape stays exactly what the reader parses.
            const string tamperedSkill = "charter-format";
            const string oldVersion = "0.0.1-old";
            string skillMd = Path.Combine(target, tamperedSkill, "SKILL.md");
            File.WriteAllText(skillMd, SkillFrontmatterStamper.Stamp(File.ReadAllText(skillMd), oldVersion));

            var stale = SkillDriftCheck.FindStaleSkillsIn(new[] { target }, CharterVersion.Current);

            var reported = Assert.Single(stale); // only the tampered skill is stale; charter still matches
            Assert.Equal(tamperedSkill, reported.Name);
            Assert.Equal(oldVersion, reported.InstalledVersion);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(target, tamperedSkill)),
                Path.GetFullPath(reported.Directory));
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void FindStaleSkills_MissingRoot_ReportsNone_WithoutThrowing()
    {
        // A root that does not exist (nothing installed) yields no warning and never throws — the safe
        // no-skills-installed case.
        string missing = Path.Combine(Path.GetTempPath(), "charter-drift-missing-" + Guid.NewGuid().ToString("N"));

        var stale = SkillDriftCheck.FindStaleSkillsIn(new[] { missing }, "9.9.9");
        Assert.Empty(stale);
    }

    [Fact]
    public void FindStaleSkills_UnstampedSkill_IsNotReported()
    {
        // A SKILL.md with frontmatter but NO metadata.charter-version stamp can't be compared, so it is never
        // flagged — matching the "absent stamp -> no warning" rule.
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string skillDir = Path.Combine(workDir, "skills", "charter");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(
                Path.Combine(skillDir, "SKILL.md"),
                "---\nname: charter\ndescription: an unstamped skill\n---\n\n# Charter\n");

            var stale = SkillDriftCheck.FindStaleSkillsIn(new[] { Path.Combine(workDir, "skills") }, "9.9.9");
            Assert.Empty(stale);
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }
}
