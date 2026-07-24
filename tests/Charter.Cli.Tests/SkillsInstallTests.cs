using System.Text.RegularExpressions;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Process-level tests for the <c>charter skills install</c> verb (Charter #18) — it extracts the two
/// skills bundled inside the binary (<c>charter</c> and <c>charter-format</c>, embedded as resources) into
/// Claude Code's skills directory, version-stamped so staleness is detectable. Exercised against the REAL
/// built binary (via <see cref="CharterCliRunner"/>) so the embed → extract path is proven end-to-end, not
/// mocked. Also pins the verb-group surface: bare <c>skills</c> and <c>skills --help</c> succeed while an
/// unknown subcommand errors.
/// </summary>
[Trait("Category", "Cli")]
public class SkillsInstallTests
{
    [Fact]
    public void SkillsInstall_Project_WritesBothSkillTrees_UnderClaudeSkills()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            // --project resolves to <cwd>/.claude/skills, so run with the working directory set to the temp dir.
            var result = CharterCliRunner.RunIn(workDir, "skills", "install", "--project");

            Assert.Equal(0, result.ExitCode);

            string skillsRoot = Path.Combine(workDir, ".claude", "skills");
            Assert.True(File.Exists(Path.Combine(skillsRoot, "charter", "SKILL.md")), "charter/SKILL.md missing");
            Assert.True(
                File.Exists(Path.Combine(skillsRoot, "charter-format", "SKILL.md")),
                "charter-format/SKILL.md missing");

            // The nested reference tree must be reconstructed too (not just the top-level SKILL.md).
            Assert.True(
                File.Exists(Path.Combine(skillsRoot, "charter", "references", "authoring-plans.md")),
                "charter/references/authoring-plans.md missing");
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void SkillsInstall_StampsCharterVersion_IntoInstalledSkillMd()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string target = Path.Combine(workDir, "skills");
            var result = CharterCliRunner.Run("skills", "install", "--target", target);

            Assert.Equal(0, result.ExitCode);

            // Both installed SKILL.md files carry a metadata.charter-version stamp (the staleness signal).
            foreach (string skill in new[] { "charter", "charter-format" })
            {
                string skillMd = File.ReadAllText(Path.Combine(target, skill, "SKILL.md"));
                Assert.Contains("metadata:", skillMd);
                Assert.Matches(new Regex(@"charter-version:\s*\d[\w.\-]*"), skillMd);
            }
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void SkillsInstall_SecondRunWithoutForce_Skips_ThenForceReinstalls()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string target = Path.Combine(workDir, "skills");

            var first = CharterCliRunner.Run("skills", "install", "--target", target);
            Assert.Equal(0, first.ExitCode);
            Assert.Contains("installed", first.StdOut);

            var second = CharterCliRunner.Run("skills", "install", "--target", target);
            Assert.Equal(0, second.ExitCode);
            Assert.Contains("skipped", second.StdOut);

            var forced = CharterCliRunner.Run("skills", "install", "--target", target, "--force");
            Assert.Equal(0, forced.ExitCode);
            Assert.Contains("installed", forced.StdOut);
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Skills_Bare_ExitsZero()
    {
        var result = CharterCliRunner.Run("skills");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("install", result.StdOut);
    }

    [Fact]
    public void Skills_Help_ExitsZero_ListsInstall()
    {
        var result = CharterCliRunner.Run("skills", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("install", result.StdOut);
    }

    [Fact]
    public void Skills_UnknownSubcommand_ExitsNonZero()
    {
        var result = CharterCliRunner.Run("skills", "bogus");

        Assert.NotEqual(0, result.ExitCode);
    }
}
