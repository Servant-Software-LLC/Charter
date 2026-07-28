using System.Diagnostics;

namespace Charter.Server;

/// <summary>
/// The one place Charter shells out to git, and it is <b>read-only</b>. §5.1 of
/// <c>docs/plans/03-git-mediated-team-review.md</c> separates two claims that "Charter never runs git" used to
/// conflate: Charter must never MUTATE git state (it does not commit, push, stage, or rewrite history), but
/// reading it is both permitted and load-bearing.
/// </summary>
/// <remarks>
/// Every failure mode — git absent, not a repository, a hung process, a sandbox that refuses to spawn — returns
/// <see langword="null"/> rather than throwing. A caller that cannot read git must degrade to the local,
/// solo-safe answer, never fail the review.
/// </remarks>
internal static class GitCommand
{
    // git answers these reads instantly; a longer wait would only delay `charter review`'s ready line.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Run <c>git</c> with <paramref name="arguments"/> in <paramref name="workingDirectory"/> and return its
    /// trimmed stdout, or <see langword="null"/> when git could not be run, timed out, or exited non-zero.
    /// </summary>
    public static string? Read(string workingDirectory, params string[] arguments)
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
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                TryKill(process);
                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            var trimmed = output.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
        catch (Exception)
        {
            // git absent, not executable, sandboxed, or refused: an answer Charter cannot read is simply
            // "unknown", which every caller here treats as the solo-safe default.
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // It already exited, or cannot be killed; either way nothing further to do.
        }
    }
}
