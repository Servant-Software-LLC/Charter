using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #237 — the skill-drift warning fired only on <c>charter --version</c>, so every execution verb was
/// silent on a machine whose installed skills were behind the tool.
///
/// <para>
/// That placement made the binary-side check depend on the thing it exists to catch. <c>--version</c> runs only
/// when something tells an agent to run it, and the thing that tells it is the <c>charter</c> skill's own
/// preamble — so an agent that went straight to <c>charter render</c> got no signal, and a skill too old to
/// carry the preamble could never trigger its own warning.
/// </para>
/// <para>
/// These drive every verb from the ONE catalog (<see cref="CharterCommands.Names"/>) rather than a hand-kept
/// list, so a verb added later is covered by construction instead of silently missed — the same rule that keeps
/// dispatch and help from disagreeing (#138). <c>--help</c> reaches dispatch without per-verb fixtures.
/// </para>
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","SkillDriftEveryVerb")].
/// </summary>
[Trait("Category", "SkillDriftEveryVerb")]
public class SkillDriftEveryVerbTests : IClassFixture<SkillDriftEveryVerbTests.StaleInstall>
{
    private const string Warning = "installed skill(s) are out of date";

    private readonly StaleInstall _stale;

    public SkillDriftEveryVerbTests(StaleInstall stale)
    {
        _stale = stale;
    }

    public static IEnumerable<object[]> EveryVerbButSkills() =>
        CharterCommands.Names.Where(name => name != "skills").Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(EveryVerbButSkills))]
    public void Every_verb_except_skills_warns_about_a_stale_install(string verb)
    {
        _stale.AssertGenuinelyStale();

        var result = CharterCliRunner.RunWith(null, _stale.Environment, verb, "--help");

        Assert.Contains(Warning, result.StdErr, StringComparison.Ordinal);
        Assert.Contains(
            "charter-format: installed " + StaleInstall.OldVersion, result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_still_warns_so_the_original_check_was_not_removed()
    {
        _stale.AssertGenuinelyStale();

        var result = CharterCliRunner.RunWith(null, _stale.Environment, "--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(Warning, result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void The_skills_verb_stays_quiet_because_it_is_the_remedy()
    {
        // Warning on the way INTO the fix reads as a failure of the fix. Silence proves nothing unless the
        // install really is stale, which is why the precondition comes first.
        _stale.AssertGenuinelyStale();

        var help = CharterCliRunner.RunWith(null, _stale.Environment, "skills", "--help");
        Assert.DoesNotContain(Warning, help.StdErr, StringComparison.Ordinal);

        var elsewhere = Path.Combine(_stale.WorkDir, "reinstall");
        var install = CharterCliRunner.RunWith(
            null, _stale.Environment, "skills", "install", "--target", elsewhere, "--force");
        Assert.Equal(0, install.ExitCode);
        Assert.DoesNotContain(Warning, install.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void The_warning_prints_once_per_invocation()
    {
        // Once, before dispatch — so a long-lived `review` or a `poll --watch` that re-arms inside one process
        // does not repeat it for the life of the session.
        _stale.AssertGenuinelyStale();

        var result = CharterCliRunner.RunWith(null, _stale.Environment, "render", "--help");

        var occurrences = result.StdErr.Split(Warning).Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void A_current_install_is_silent()
    {
        // The positive control: the warning is about DRIFT, not about skills being installed at all.
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string current = Path.Combine(workDir, "skills");
            Assert.Equal(0, CharterCliRunner.Run("skills", "install", "--target", current).ExitCode);
            Assert.Empty(SkillDriftCheck.FindStaleSkillsIn(new[] { current }, CharterVersion.Current));

            var result = CharterCliRunner.RunWith(
                null, new Dictionary<string, string> { ["CHARTER_SKILLS_DIR"] = current }, "render", "--help");

            Assert.DoesNotContain(Warning, result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void An_explicit_target_still_wins_over_the_isolation_variable()
    {
        // CHARTER_SKILLS_DIR replaces only the user-home DEFAULT. Were it to override --target, an install
        // someone aimed at a path would silently land somewhere else.
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string aimed = Path.Combine(workDir, "aimed");
            string variable = Path.Combine(workDir, "variable");

            var install = CharterCliRunner.RunWith(
                null, new Dictionary<string, string> { ["CHARTER_SKILLS_DIR"] = variable },
                "skills", "install", "--target", aimed);

            Assert.Equal(0, install.ExitCode);
            Assert.True(File.Exists(Path.Combine(aimed, "charter", "SKILL.md")), "the install did not land at --target");
            Assert.False(Directory.Exists(variable), "the install landed at CHARTER_SKILLS_DIR despite an explicit --target");
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// One real install, shared by the class, with <c>charter-format</c> re-stamped to an old version using the
    /// same stamper install uses — so the frontmatter is exactly what the drift reader parses.
    /// </summary>
    public sealed class StaleInstall : IDisposable
    {
        public const string OldVersion = "0.0.1-old";

        public StaleInstall()
        {
            WorkDir = CharterCliRunner.NewTempDirectory();
            SkillsDir = Path.Combine(WorkDir, "skills");

            var install = CharterCliRunner.Run("skills", "install", "--target", SkillsDir);
            if (install.ExitCode != 0)
            {
                throw new InvalidOperationException("fixture install failed: " + install.StdErr);
            }

            string skillMd = Path.Combine(SkillsDir, "charter-format", "SKILL.md");
            File.WriteAllText(skillMd, SkillFrontmatterStamper.Stamp(File.ReadAllText(skillMd), OldVersion));

            Environment = new Dictionary<string, string> { ["CHARTER_SKILLS_DIR"] = SkillsDir };
        }

        public string WorkDir { get; }

        public string SkillsDir { get; }

        public IReadOnlyDictionary<string, string> Environment { get; }

        /// <summary>
        /// Prove the fixture before any assertion leans on it. The tests asserting SILENCE would pass against an
        /// install that was never stale — certifying nothing while looking like coverage.
        /// </summary>
        public void AssertGenuinelyStale()
        {
            var stale = SkillDriftCheck.FindStaleSkillsIn(new[] { SkillsDir }, CharterVersion.Current);
            Assert.True(
                stale.Any(skill => skill.Name == "charter-format" && skill.InstalledVersion == OldVersion),
                "the fixture install is not stale, so no drift assertion in this class would mean anything");
        }

        public void Dispose() => CharterCliRunner.TryDeleteDirectory(WorkDir);
    }
}
