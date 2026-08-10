using System.Diagnostics;
using System.Text;
using Charter.Core;

namespace Charter.Cli;

/// <summary>
/// The git reads behind <c>charter recap</c>, and they are <b>read-only</b> — the same rule
/// <c>Charter.Server.GitCommand</c> follows (<c>docs/plans/03-git-mediated-team-review.md</c> §5.1): Charter
/// must never MUTATE git state, but reading it is permitted and load-bearing.
/// </summary>
/// <remarks>
/// This is deliberately NOT <c>GitCommand</c>. That helper serves the review server, where an unreadable git is
/// simply "unknown" and every failure degrades to <see langword="null"/> so a review never fails — and where a
/// five-second timeout is right because it only asks questions git answers instantly. A recap is the opposite
/// case on both counts: the user typed a range, so a failure must be REPORTED with git's own message (a typo'd
/// ref is the common case and "nothing happened" would be a terrible answer), and <c>git diff</c> over a large
/// branch legitimately takes longer than five seconds.
/// </remarks>
internal static class RecapGit
{
    /// <summary>UTF-8 WITHOUT a BOM: git never emits one, and a BOM-emitting encoder would prepend U+FEFF to
    /// the decoded text, which would then be written into the seed's first heading.</summary>
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Generous compared to the review server's 5s: a diff over a long branch in a big repository is
    /// real work, and a recap is a foreground command the user is waiting on deliberately.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>The unified diff for <paramref name="range"/>, exactly as git renders it.</summary>
    /// <remarks>
    /// <c>--no-color</c> and <c>--no-ext-diff</c> pin the OUTPUT FORMAT: git suppresses colour when stdout is
    /// not a terminal, but a user's <c>color.diff = always</c> would smuggle ANSI escapes into the seed, and a
    /// configured external diff driver can emit something that is not a unified diff at all.
    /// <c>--find-renames</c> makes rename detection explicit rather than dependent on the caller's git version
    /// and config.
    /// </remarks>
    public static GitRead Diff(string workingDirectory, string range)
        => Run(workingDirectory, "diff", "--no-color", "--no-ext-diff", "--find-renames", range);

    /// <summary>
    /// The commits that produced the diff, newest first, or an EMPTY list when git cannot answer.
    /// </summary>
    /// <remarks>
    /// The two-branch mapping is the whole subtlety. For an explicit range (<c>main..HEAD</c>) the range IS the
    /// commit set. For a single ref (<c>HEAD~3</c>), <c>git diff</c> compares it against the working tree — but
    /// <c>git log HEAD~3</c> would list all history REACHABLE FROM it, which is the wrong set and looks
    /// authoritative, so the ref is turned into <c>&lt;ref&gt;..HEAD</c> to name the commits that actually
    /// produced the change. A failure yields no table rather than a wrong one: the diff is the deliverable, the
    /// commit list is context.
    /// </remarks>
    public static IReadOnlyList<RecapCommit> Commits(string workingDirectory, string range)
    {
        var spec = range.Contains("..", StringComparison.Ordinal) ? range : $"{range}..HEAD";
        var read = Run(workingDirectory, "log", "--no-color", "--format=%h%x09%s", spec);
        if (!read.Ok)
        {
            return Array.Empty<RecapCommit>();
        }

        var commits = new List<RecapCommit>();
        foreach (var line in read.Output.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.Length == 0)
            {
                continue;
            }

            var tab = text.IndexOf('\t', StringComparison.Ordinal);
            commits.Add(tab < 0
                ? new RecapCommit(text, string.Empty)
                : new RecapCommit(text[..tab], text[(tab + 1)..]));
        }

        return commits;
    }

    private static GitRead Run(string workingDirectory, params string[] arguments)
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

                // git emits UTF-8. Without this, .NET decodes a redirected stream using the CONSOLE's code
                // page, which on Windows is an OEM one — so an em-dash in a commit subject, or any non-ASCII
                // source line in the diff, reaches the seed as mojibake and is then written to a .charter.md
                // as those wrong characters. Caught by recapping this repository, whose own commit subjects
                // contain em-dashes.
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return GitRead.Failed("git could not be started.");
            }

            // Read both pipes concurrently BEFORE waiting. A diff larger than the OS pipe buffer deadlocks a
            // sequential ReadToEnd()-then-WaitForExit(), and a recap's whole input is a potentially huge diff.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                TryKill(process);
                return GitRead.Failed($"git did not finish within {Timeout.TotalMinutes:0} minutes.");
            }

            var output = stdout.GetAwaiter().GetResult();
            var error = stderr.GetAwaiter().GetResult();

            return process.ExitCode == 0
                ? GitRead.Succeeded(output)
                : GitRead.Failed(Summarize(error, process.ExitCode));
        }
        catch (Exception ex)
        {
            return GitRead.Failed(ex.Message);
        }
    }

    /// <summary>Reduce git's stderr to the one line worth showing, keeping its own wording — "unknown revision"
    /// and "not a git repository" are the two common failures and git already says them well.</summary>
    private static string Summarize(string stderr, int exitCode)
    {
        foreach (var line in stderr.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length > 0)
            {
                return text.StartsWith("fatal: ", StringComparison.Ordinal) ? text["fatal: ".Length..] : text;
            }
        }

        return $"git exited with code {exitCode}.";
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already exited, or cannot be killed; either way there is nothing further to do.
        }
    }
}

/// <summary>The outcome of one git read: its <paramref name="Output"/> on success, or the
/// <paramref name="Error"/> to show the user.</summary>
internal sealed record GitRead(bool Ok, string Output, string Error)
{
    public static GitRead Succeeded(string output) => new(true, output, string.Empty);

    public static GitRead Failed(string error) => new(false, string.Empty, error);
}
