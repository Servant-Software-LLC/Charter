using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #194 — <c>charter skills install</c> said NOTHING when its target resolved inside a git working
/// tree. In the reported case <c>~/.claude/skills</c> was a Windows symlink into a personal dotfiles repo, so
/// a user-scope install wrote three skill folders straight through the link into that repository, where they
/// sat as untracked directories until they surfaced in <c>git status</c> much later with no memory of where
/// they came from.
///
/// <para>
/// The defect is the SILENCE, not the destination: the install did what it was told. #154's guard cannot
/// catch it, because that one keys on skills the repository already TRACKS and these were brand new.
/// </para>
/// <para>
/// Committing them is the wrong outcome — <c>SkillFrontmatterStamper</c> writes
/// <c>metadata.charter-version</c> into each <c>SKILL.md</c> and <c>SkillDriftCheck</c> diffs against it, so a
/// committed copy is a vendored snapshot that goes stale, diffs on every reinstall, and draws drift warnings.
/// Which is what makes the advisory worth printing, and why the paths it prints have to be right.
/// </para>
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","SkillsInstallGitAdvisory")].
/// </summary>
[Trait("Category", "SkillsInstallGitAdvisory")]
public class SkillsInstallGitAdvisoryTests
{
    /// <summary>Every skill folder bundled in the binary — all three landed in the reported incident.</summary>
    private static readonly string[] BundledSkills = ["charter", "charter-drain", "charter-format"];

    [Fact]
    public void Installing_into_a_git_working_tree_names_the_repository_root_and_the_rules_to_paste()
    {
        if (!TestGit.IsAvailable)
        {
            return;   // No git: the probe answers "no" and the install is silent BY DESIGN (see #154).
        }

        string repo = TestGit.NewRepo();
        try
        {
            string skills = Path.Combine(repo, "skills");

            var result = CharterCliRunner.Run("skills", "install", "--target", skills);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("git working tree", result.StdOut, StringComparison.Ordinal);

            // The root as GIT spells it — the oracle, not the test's own path string. macOS resolves
            // /var/folders to /private/var/folders and Windows reports forward slashes, so any hand-built
            // expectation here would be asserting the host's path conventions rather than the advisory.
            string root = TestGit.Run(repo, "rev-parse", "--show-toplevel").Trim();
            Assert.False(string.IsNullOrEmpty(root), "git could not report the repository root");
            Assert.Contains(root, result.StdOut, StringComparison.Ordinal);

            // Why committing them is wrong has to travel WITH the advisory; a bare "you are in a repo" tells
            // the reader nothing they can act on.
            Assert.Contains("tool-managed", result.StdOut, StringComparison.Ordinal);
            Assert.Contains(".gitignore", result.StdOut, StringComparison.Ordinal);

            // Anchored at the repository ROOT and correct for the RESOLVED target — not a guessed layout.
            foreach (string skill in BundledSkills)
            {
                Assert.Contains($"/skills/{skill}/", result.StdOut, StringComparison.Ordinal);
            }
        }
        finally
        {
            TestGit.TryDeleteRepo(repo);
        }
    }

    /// <summary>
    /// The advisory's substance, not its wording: the rules it hands the reader, pasted verbatim into the
    /// repository it named, must actually ignore the skills.
    ///
    /// <para>
    /// A string assertion on the rule text would pass against rules anchored at the wrong place — and the
    /// wrong place is the LIKELY failure, because git reports the root of the physical destination while the
    /// caller holds the nominal path. This applies them and asks git.
    /// </para>
    /// </summary>
    [Fact]
    public void The_rules_the_advisory_prints_really_do_ignore_the_installed_skills()
    {
        if (!TestGit.IsAvailable)
        {
            return;
        }

        string repo = TestGit.NewRepo();
        try
        {
            // Deliberately NOT at the repository root: rules anchored by a guessed layout would still read
            // plausibly and would still fail here.
            var result = CharterCliRunner.Run(
                "skills", "install", "--target", Path.Combine(repo, "nested", "skills"));
            Assert.Equal(0, result.ExitCode);

            Assert.Contains(
                "?? nested/",
                TestGit.Run(repo, "status", "--porcelain"),
                StringComparison.Ordinal);

            string[] rules = result.StdOut
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith('/') && line.EndsWith('/'))
                .ToArray();
            Assert.Equal(BundledSkills.Length, rules.Length);

            string root = TestGit.Run(repo, "rev-parse", "--show-toplevel").Trim();
            File.WriteAllText(Path.Combine(root, ".gitignore"), string.Join('\n', rules) + "\n");

            // Everything the install wrote is now ignored; only the .gitignore the reader just added remains.
            Assert.Equal("?? .gitignore", TestGit.Run(repo, "status", "--porcelain").Trim());
        }
        finally
        {
            TestGit.TryDeleteRepo(repo);
        }
    }

    /// <summary>
    /// The overwhelmingly common case, and the one the advisory must not spam: an install directory in no
    /// repository at all.
    /// </summary>
    [Fact]
    public void A_target_in_no_repository_says_nothing_about_git()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            var result = CharterCliRunner.Run("skills", "install", "--target", Path.Combine(workDir, "skills"));

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("working tree", result.StdOut, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".gitignore", result.StdOut, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// The tempting fix — have the installer drop a self-ignoring <c>.gitignore</c> (<c>*</c>) into each skill
    /// folder it writes — would silently break <c>--project</c>, whose ENTIRE purpose is putting the skill in
    /// the repository so teammates get it. A <c>.gitignore</c> in a subdirectory overrides a negation in the
    /// repository root, because the deeper file wins.
    ///
    /// <para>
    /// This test is the issue's own experiment, pinned. Against the common project convention
    /// (<c>.claude/*</c> then <c>!.claude/skills/</c>), a tool-laid <c>*</c> makes the installed skill vanish
    /// from <c>git add -A -n</c> while a hand-authored sibling still adds — and the symptom of an over-broad
    /// ignore rule is that NOTHING appears at all. So the assertion is behavioural: git must still be willing
    /// to add the skill. Checking only "no .gitignore file exists" would pass against any number of other ways
    /// to break delivery.
    /// </para>
    /// </summary>
    [Fact]
    public void Project_installs_lay_no_ignore_rules_and_the_skill_still_reaches_teammates()
    {
        if (!TestGit.IsAvailable)
        {
            return;
        }

        string repo = TestGit.NewRepo();
        try
        {
            File.WriteAllText(
                Path.Combine(repo, ".gitignore"), ".claude/*\n!.claude/skills/\n!.claude/agents/\n");

            // A hand-authored skill beside the installed ones: the control that proves the negation works at
            // all, so a failure below means the TOOL broke delivery rather than the fixture being wrong.
            string mine = Path.Combine(repo, ".claude", "skills", "my-own-skill");
            Directory.CreateDirectory(mine);
            File.WriteAllText(Path.Combine(mine, "SKILL.md"), "---\nname: my-own-skill\n---\n");

            var result = CharterCliRunner.RunIn(repo, "skills", "install", "--project");
            Assert.Equal(0, result.ExitCode);

            string wouldAdd = TestGit.Run(repo, "add", "-A", "-n");
            Assert.Contains(".claude/skills/my-own-skill/SKILL.md", wouldAdd, StringComparison.Ordinal);
            foreach (string skill in BundledSkills)
            {
                Assert.Contains($".claude/skills/{skill}/SKILL.md", wouldAdd, StringComparison.Ordinal);
            }

            // Nothing was written that could ignore them — under --project or anywhere else.
            string skillsRoot = Path.Combine(repo, ".claude", "skills");
            Assert.Empty(Directory.GetFiles(skillsRoot, ".gitignore", SearchOption.AllDirectories));

            // And no advisory: --project MEANS "share this with teammates", so telling the operator how to
            // ignore what they just deliberately committed to the repo is noise, not help.
            Assert.DoesNotContain("working tree", result.StdOut, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestGit.TryDeleteRepo(repo);
        }
    }

    /// <summary>
    /// The whole point of #194. <c>~/.claude/skills</c> was a SYMLINK into a repository — the target's nominal
    /// path was under the home directory and had no relationship to any work tree, so a check that reasoned
    /// about the path string would be a silent no-op exactly where the defect lives.
    ///
    /// <para>
    /// The link here is deliberately two levels deep in the repository, so the printed rules can only be
    /// right if the prefix came from the RESOLVED destination: a check that took the target's own path, or
    /// assumed the skills folder sits at the root, prints the wrong rules while still finding the repository.
    /// </para>
    /// </summary>
    [Fact]
    public void A_target_reached_through_a_directory_link_resolves_to_the_repository_it_really_lands_in()
    {
        if (!TestGit.IsAvailable)
        {
            return;
        }

        string repo = TestGit.NewRepo();
        string home = CharterCliRunner.NewTempDirectory();
        string link = Path.Combine(home, "skills");
        try
        {
            string real = Path.Combine(repo, "nested", "skills");
            Directory.CreateDirectory(real);

            if (!TryCreateDirectoryLink(link, real))
            {
                // Windows refuses symbolic links without Developer Mode or elevation, and junctions are the
                // documented unprivileged stand-in; if neither is available this host cannot express the
                // defect's shape at all, and asserting anything here would assert the host's privileges.
                return;
            }

            var result = CharterCliRunner.Run("skills", "install", "--target", link);

            Assert.Equal(0, result.ExitCode);

            string root = TestGit.Run(repo, "rev-parse", "--show-toplevel").Trim();
            Assert.Contains("git working tree", result.StdOut, StringComparison.Ordinal);
            Assert.Contains(root, result.StdOut, StringComparison.Ordinal);

            foreach (string skill in BundledSkills)
            {
                Assert.Contains($"/nested/skills/{skill}/", result.StdOut, StringComparison.Ordinal);
            }
        }
        finally
        {
            TryRemoveDirectoryLink(link);
            CharterCliRunner.TryDeleteDirectory(home);
            TestGit.TryDeleteRepo(repo);
        }
    }

    /// <summary>
    /// The fail-to-"no" contract #154 established, applied to the third question. The target here really IS
    /// inside a work tree, so silence can only come from the probe being unable to answer — which makes this
    /// falsifiable rather than a test that would pass against any code at all.
    /// </summary>
    [Fact]
    public void When_git_cannot_be_run_the_install_is_silent_and_still_succeeds()
    {
        if (!TestGit.IsAvailable)
        {
            return;
        }

        string? dotnetDir = DirectoryOfDotnetOnPath();
        if (dotnetDir is null)
        {
            return;   // Cannot rebuild a PATH that still launches the CLI; nothing to assert.
        }

        string repo = TestGit.NewRepo();
        try
        {
            // A PATH that can still start the CLI but from which `git` has genuinely gone.
            string path = OperatingSystem.IsWindows()
                ? dotnetDir + Path.PathSeparator + Environment.GetFolderPath(Environment.SpecialFolder.System)
                : dotnetDir;

            var result = CharterCliRunner.RunWith(
                workingDirectory: null,
                environment: new Dictionary<string, string> { ["PATH"] = path },
                "skills", "install", "--target", Path.Combine(repo, "skills"));

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("working tree", result.StdOut, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Unhandled exception", result.StdErr, StringComparison.OrdinalIgnoreCase);

            // Still a real install: degrading the probe must not degrade the command.
            Assert.True(
                File.Exists(Path.Combine(repo, "skills", "charter", "SKILL.md")),
                "the install itself must be unaffected when the git probe cannot answer");
        }
        finally
        {
            TestGit.TryDeleteRepo(repo);
        }
    }

    private static string? DirectoryOfDotnetOnPath()
    {
        string exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string[] directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return directories.FirstOrDefault(directory =>
        {
            try
            {
                return File.Exists(Path.Combine(directory, exe));
            }
            catch (Exception)
            {
                return false;   // A malformed PATH entry is not this test's problem.
            }
        });
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            return TryCreateJunction(link, target);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A junction is the reparse point Windows lets an unprivileged user create; git resolves the process
    /// working directory through it exactly as it does a symbolic link.
    /// </summary>
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var startInfo = new ProcessStartInfo("cmd")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in new[] { "/c", "mklink", "/J", link, target })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(30_000);
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryRemoveDirectoryLink(string link)
    {
        try
        {
            if (Directory.Exists(link))
            {
                // Deletes the LINK, never its contents: .NET does not follow reparse points here.
                Directory.Delete(link);
            }
        }
        catch (Exception)
        {
            // Best-effort temp cleanup.
        }
    }
}
