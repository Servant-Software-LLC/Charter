using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #154 — <c>charter skills install --force</c> deletes its destination and re-extracts. Harmless for
/// <c>~/.claude/skills</c>, which holds installed copies and nothing else; destructive for a repository that
/// tracks its skills as SOURCE.
///
/// <para>
/// The path there is not obscure: <c>charter --version</c> PRINTS <c>charter skills install --force</c> as the
/// remedy for stale skills, so the natural response is to run it wherever you happen to be standing. This
/// repository tracks its own skills, and running that command here really does overwrite them.
/// </para>
/// <para>
/// The guard keys on <b>tracked AND dirty</b>, never tracked alone: overwriting a CLEAN tracked folder loses
/// nothing git cannot give back, so refusing there would be noise — and a guard that fires when nothing is at
/// stake teaches people to pass the override reflexively.
/// </para>
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","SkillsInstallOverwriteGuard")].
/// </summary>
[Trait("Category", "SkillsInstallOverwriteGuard")]
public class SkillsInstallOverwriteGuardTests
{
    [Fact]
    public void A_dirty_tracked_skill_folder_is_refused_and_its_uncommitted_work_survives()
    {
        if (!GitAvailable())
        {
            // Without git the guard is inert BY DESIGN — the probe answers "no" and the install proceeds
            // exactly as it did before this existed. There is nothing to assert, and asserting the refusal
            // here would be asserting that git is installed.
            return;
        }

        string repo = NewGitRepo();
        try
        {
            string skills = Path.Combine(repo, "skills");
            string mine = Path.Combine(skills, "charter");
            Directory.CreateDirectory(mine);
            File.WriteAllText(Path.Combine(mine, "SKILL.md"), "---\nname: charter\n---\n\n# committed\n");
            Git(repo, "add", "-A");
            Git(repo, "commit", "-m", "authored skills");

            // The work git could not bring back.
            File.AppendAllText(Path.Combine(mine, "SKILL.md"), "\nuncommitted sentence\n");

            var result = CharterCliRunner.Run("skills", "install", "--target", skills, "--force");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("refusing to overwrite", result.StdErr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(mine, result.StdErr, StringComparison.Ordinal);
            // It must say what --force would DO, not merely decline — the operator has to weigh it.
            Assert.Contains("destroying work git cannot restore", result.StdErr, StringComparison.Ordinal);
            Assert.Contains("--overwrite-tracked", result.StdErr, StringComparison.Ordinal);

            // THE assertion: nothing was deleted.
            Assert.Contains(
                "uncommitted sentence",
                File.ReadAllText(Path.Combine(mine, "SKILL.md")),
                StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    /// <summary>
    /// The other half. A clean tracked folder is fully recoverable, so refusing there would be noise — and a
    /// test asserting only the refusal would happily pass against a guard that refused everything.
    /// </summary>
    [Fact]
    public void A_clean_tracked_skill_folder_is_still_overwritten()
    {
        if (!GitAvailable())
        {
            // Without git the guard is inert BY DESIGN — the probe answers "no" and the install proceeds
            // exactly as it did before this existed. There is nothing to assert, and asserting the refusal
            // here would be asserting that git is installed.
            return;
        }

        string repo = NewGitRepo();
        try
        {
            string skills = Path.Combine(repo, "skills");
            Directory.CreateDirectory(Path.Combine(skills, "charter"));
            File.WriteAllText(
                Path.Combine(skills, "charter", "SKILL.md"), "---\nname: charter\n---\n\n# committed\n");
            Git(repo, "add", "-A");
            Git(repo, "commit", "-m", "authored skills");

            var result = CharterCliRunner.Run("skills", "install", "--target", skills, "--force");

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("refusing to overwrite", result.StdErr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public void OverwriteTracked_proceeds_anyway_because_the_operator_asked()
    {
        if (!GitAvailable())
        {
            // Without git the guard is inert BY DESIGN — the probe answers "no" and the install proceeds
            // exactly as it did before this existed. There is nothing to assert, and asserting the refusal
            // here would be asserting that git is installed.
            return;
        }

        string repo = NewGitRepo();
        try
        {
            string skills = Path.Combine(repo, "skills");
            string mine = Path.Combine(skills, "charter");
            Directory.CreateDirectory(mine);
            File.WriteAllText(Path.Combine(mine, "SKILL.md"), "---\nname: charter\n---\n\n# committed\n");
            Git(repo, "add", "-A");
            Git(repo, "commit", "-m", "authored skills");
            File.AppendAllText(Path.Combine(mine, "SKILL.md"), "\nuncommitted sentence\n");

            var result = CharterCliRunner.Run(
                "skills", "install", "--target", skills, "--force", "--overwrite-tracked");

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain(
                "uncommitted sentence",
                File.ReadAllText(Path.Combine(mine, "SKILL.md")),
                StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    /// <summary>
    /// The common case, and the one that must never regress: a plain install directory in no repository at
    /// all. The probe cannot answer there, and "cannot answer" must mean "proceed" — a git-less host, or a
    /// destination outside any repo, must behave exactly as it did before this guard existed.
    /// </summary>
    [Fact]
    public void A_target_outside_any_repository_is_unaffected()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string skills = Path.Combine(workDir, "skills");
            Assert.Equal(0, CharterCliRunner.Run("skills", "install", "--target", skills).ExitCode);

            var forced = CharterCliRunner.Run("skills", "install", "--target", skills, "--force");

            Assert.Equal(0, forced.ExitCode);
            Assert.DoesNotContain("refusing to overwrite", forced.StdErr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    private static string NewGitRepo()
    {
        string dir = CharterCliRunner.NewTempDirectory();
        Git(dir, "init");
        Git(dir, "config", "user.email", "test@example.com");
        Git(dir, "config", "user.name", "Test");
        return dir;
    }

    private static bool GitAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Git(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        process.WaitForExit(30_000);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                // git writes read-only objects under .git on Windows; clear them or the delete fails.
                foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort temp cleanup.
        }
    }
}
