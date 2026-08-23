using System;
using System.Diagnostics;
using System.IO;

namespace Charter.Cli.Tests;

/// <summary>
/// Drives a REAL <c>git</c> against a throwaway repository in the temp directory. The guards under test
/// (Charter #154's overwrite refusal, #194's working-tree advisory) shell out to git themselves, so the only
/// thing worth testing them against is a genuine work tree — a mocked git would prove the mock.
/// </summary>
internal static class TestGit
{
    /// <summary>
    /// Whether <c>git</c> can be run at all. Both guards are inert BY DESIGN without it (their probe answers
    /// "no" and the install proceeds), so a git-less host has nothing to assert rather than something to fail.
    /// </summary>
    public static bool IsAvailable { get; } = ProbeAvailability();

    /// <summary>A fresh temp directory initialised as a git repository with an identity configured.</summary>
    public static string NewRepo()
    {
        string dir = CharterCliRunner.NewTempDirectory();
        Run(dir, "init");
        Run(dir, "config", "user.email", "test@example.com");
        Run(dir, "config", "user.name", "Test");
        return dir;
    }

    /// <summary>
    /// Run git in <paramref name="workingDirectory"/> and return its stdout. Exit status is deliberately not
    /// asserted: callers here either use the output as an oracle or are setting a fixture up.
    /// </summary>
    public static string Run(string workingDirectory, params string[] args)
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

        // Read before waiting: a sequential ReadToEnd()-then-WaitForExit() deadlocks on a full pipe buffer.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(30_000);

        _ = stderr.GetAwaiter().GetResult();
        return stdout.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Best-effort recursive delete of a throwaway repository. git writes read-only objects under
    /// <c>.git</c> on Windows, which makes a plain recursive delete fail.
    /// </summary>
    public static void TryDeleteRepo(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(dir, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp directory must never fail a test.
        }
    }

    private static bool ProbeAvailability()
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
}
