using System;
using System.Diagnostics;
using System.IO;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// The gate §5.0 makes binding: the review-log warnings fire only when the plan's <c>.review/</c> directory is
/// actually <b>tracked</b> by git — i.e. the reviewer has opted into sharing. Untracked, gitignored, or not a
/// repository must all be silent, because that is the solo case and §5.0 forbids nagging it.
///
/// These run against a real <c>git init</c> in a temp directory (a pure read, and never against the developer's
/// own repo) and skip cleanly where git is unavailable — Charter must work on a machine that has never seen git,
/// so its absence is a supported state, not a test failure.
/// </summary>
[Trait("Category", "ReviewLog")]
public class GitTrackingTests
{
    [SkippableFact]
    public void UntrackedDirectory_InARealRepo_IsNotTracked()
    {
        var repo = NewRepo();
        Skip.If(repo is null, "git is unavailable on this host.");
        try
        {
            var review = Path.Combine(repo!, "plan.charter.review");
            Directory.CreateDirectory(review);
            File.WriteAllText(Path.Combine(review, "alice.deadbeef.jsonl"), "{}\n");

            Assert.False(
                GitTracking.IsTracked(review),
                "a .review/ this reviewer just created is untracked — the solo case, which must stay silent.");
        }
        finally
        {
            TryDeleteDir(repo!);
        }
    }

    [SkippableFact]
    public void TrackedDirectory_IsTracked()
    {
        var repo = NewRepo();
        Skip.If(repo is null, "git is unavailable on this host.");
        try
        {
            var review = Path.Combine(repo!, "plan.charter.review");
            Directory.CreateDirectory(review);
            File.WriteAllText(Path.Combine(review, "alice.deadbeef.jsonl"), "{}\n");

            // A teammate's committed log is what makes this a SHARED review — the only case in which the
            // permanence notice is relevant.
            Assert.True(RunGit(repo!, "add", "--", "plan.charter.review"), "git add should succeed in a fresh repo");
            Assert.True(GitTracking.IsTracked(review));
        }
        finally
        {
            TryDeleteDir(repo!);
        }
    }

    [SkippableFact]
    public void GitignoredDirectory_IsNotTracked_TheDocumentedOptOut()
    {
        var repo = NewRepo();
        Skip.If(repo is null, "git is unavailable on this host.");
        try
        {
            File.WriteAllText(Path.Combine(repo!, ".gitignore"), "*.review/\n");
            var review = Path.Combine(repo!, "plan.charter.review");
            Directory.CreateDirectory(review);
            File.WriteAllText(Path.Combine(review, "alice.deadbeef.jsonl"), "{}\n");

            // §7's opt-out: a team that wants local-only review gitignores the directory and Charter keeps
            // working exactly as it does today — including saying nothing about permanence.
            RunGit(repo!, "add", "--all");
            Assert.False(GitTracking.IsTracked(review));
        }
        finally
        {
            TryDeleteDir(repo!);
        }
    }

    [Fact]
    public void DirectoryOutsideAnyRepository_IsNotTracked()
    {
        var directory = Path.Combine(Path.GetTempPath(), "charter-notrepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var review = Path.Combine(directory, "plan.charter.review");
            Directory.CreateDirectory(review);
            Assert.False(GitTracking.IsTracked(review));
        }
        finally
        {
            TryDeleteDir(directory);
        }
    }

    [Fact]
    public void MissingDirectory_IsNotTracked_NeverThrows()
        => Assert.False(GitTracking.IsTracked(
            Path.Combine(Path.GetTempPath(), "charter-absent-" + Guid.NewGuid().ToString("N"), "x.review")));

    // ---- helpers -----------------------------------------------------------------------------------------

    /// <summary>A fresh temp git repository, or null when git could not be run at all.</summary>
    private static string? NewRepo()
    {
        var directory = Path.Combine(Path.GetTempPath(), "charter-gitrepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (RunGit(directory, "init"))
        {
            return directory;
        }

        TryDeleteDir(directory);
        return null;
    }

    private static bool RunGit(string workingDirectory, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(15_000) && process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDeleteDir(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                // A .git directory carries read-only objects on Windows; clear the bit before recursing.
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup of a temp directory.
        }
    }
}
