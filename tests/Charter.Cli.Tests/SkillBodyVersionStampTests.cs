using System.IO;
using System.Linq;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #152 — the suspenders. The frontmatter stamp answers "what is on DISK", which is what
/// <c>charter --version</c> compares against the binary (Charter #32, already shipped). It cannot answer the
/// question that actually bites: <b>what did this session LOAD?</b>
///
/// <para>
/// Frontmatter is discovery metadata and does not reliably reach an agent's context, so a skill has no way to
/// report its own loaded version — and a session holding a copy from before the last
/// <c>charter skills install</c> keeps running it indefinitely while disk and binary agree perfectly. A stamp
/// in the BODY is prose the agent has definitely read, because reading the body is what loading a skill
/// means. These pin that the placeholder is filled at install and never ships to a reader as itself.
/// </para>
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","SkillBodyVersionStamp")].
/// </summary>
[Trait("Category", "SkillBodyVersionStamp")]
public class SkillBodyVersionStampTests
{
    [Fact]
    public void Stamp_FillsThePlaceholderInTheBody()
    {
        const string content =
            "---\nname: charter\n---\n\n# Charter\n\nThis skill is version `@CHARTER_VERSION@`.\n";

        string stamped = SkillFrontmatterStamper.Stamp(content, "1.2.3");

        Assert.Contains("This skill is version `1.2.3`.", stamped, StringComparison.Ordinal);
        Assert.DoesNotContain(SkillFrontmatterStamper.BodyVersionPlaceholder, stamped, StringComparison.Ordinal);
        // The frontmatter stamp still happens — the two are one pass, not alternatives.
        Assert.Equal("1.2.3", SkillFrontmatterStamper.ReadVersion(stamped));
    }

    /// <summary>
    /// A file with no frontmatter is returned verbatim by the frontmatter transform — but a body placeholder
    /// still has to be filled, or the skill would report its own version as the literal placeholder.
    /// </summary>
    [Fact]
    public void Stamp_FillsThePlaceholderEvenWithNoFrontmatter()
    {
        const string content = "# Charter\n\nThis skill is version `@CHARTER_VERSION@`.\n";

        string stamped = SkillFrontmatterStamper.Stamp(content, "9.9.9");

        Assert.Contains("version `9.9.9`", stamped, StringComparison.Ordinal);
    }

    /// <summary>
    /// Body ONLY. A <c>description:</c> that happened to quote the placeholder is frontmatter prose the
    /// author wrote deliberately, and rewriting it would be a silent edit to a field this transform has no
    /// business touching.
    /// </summary>
    [Fact]
    public void Stamp_LeavesAPlaceholderInsideFrontmatterAlone()
    {
        const string content =
            "---\nname: charter\ndescription: mentions @CHARTER_VERSION@ on purpose\n---\n\nBody @CHARTER_VERSION@\n";

        string stamped = SkillFrontmatterStamper.Stamp(content, "1.2.3");

        Assert.Contains("description: mentions @CHARTER_VERSION@ on purpose", stamped, StringComparison.Ordinal);
        Assert.Contains("Body 1.2.3", stamped, StringComparison.Ordinal);
    }

    /// <summary>
    /// End to end through the real installer: every shipped skill body carries this tool's version, and no
    /// installed file anywhere still shows the placeholder. The second half is what stops a silent regression
    /// where the marker is renamed in the skill but not in the stamper — the skill would then cheerfully
    /// report its version as "@CHARTER_VERSION@" forever.
    /// </summary>
    [Fact]
    public void Install_StampsEveryBodyAndLeavesNoPlaceholderBehind()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string target = Path.Combine(workDir, "skills");
            var install = CharterCliRunner.Run("skills", "install", "--target", target);
            Assert.Equal(0, install.ExitCode);

            var skillFiles = Directory.GetFiles(target, "SKILL.md", SearchOption.AllDirectories);
            Assert.NotEmpty(skillFiles);

            foreach (string file in skillFiles)
            {
                string text = File.ReadAllText(file);
                Assert.DoesNotContain(
                    SkillFrontmatterStamper.BodyVersionPlaceholder, text, StringComparison.Ordinal);
            }

            // The two DRIVING skills — the ones with execution steps — must carry the self-check. A
            // reference-only skill (charter-format) does not run commands, so it is not required to.
            foreach (string name in new[] { "charter", "charter-drain" })
            {
                string text = File.ReadAllText(Path.Combine(target, name, "SKILL.md"));
                Assert.Contains(
                    $"This skill is version `{CharterVersion.Current}`.", text, StringComparison.Ordinal);
                // ...and it must name BOTH remedies, because installing without restarting is the trap:
                // the files change, the warning clears, and the session keeps its loaded copy.
                Assert.Contains("charter skills install --force", text, StringComparison.Ordinal);
                Assert.Contains("restart this session", text, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }
}
