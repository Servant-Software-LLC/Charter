using System.Diagnostics;
using System.Text;

namespace Charter.Server;

/// <summary>
/// The one place the review SERVER shells out to git, and it is <b>read-only</b>. (The CLI's
/// <c>charter recap</c> reads git too, through its own <c>RecapGit</c> — same read-only rule, but it must
/// report a failure rather than degrade to null, and it allows a far longer timeout.) §5.1 of
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
    /// git emits UTF-8, but .NET decodes a REDIRECTED stream with the console's encoding unless told
    /// otherwise — an OEM code page on Windows. Unpinned, a reviewer called <c>Müller</c> is attributed as
    /// <c>MÃ¼ller</c> on every comment they leave, and a teammate reading the committed log sees the same
    /// corruption. For the rarer case of a non-ASCII <c>user.email</c> it is worse than cosmetic:
    /// <see cref="ReviewLogPaths.FileNameForAuthor"/> derives both halves of the log's FILE NAME from the
    /// address, so a misdecoded one writes to a different file than the same person's correctly-decoded
    /// identity would (Charter #122).
    /// No BOM — git never emits one, and a BOM-emitting encoder would prepend U+FEFF to every value read.
    /// </summary>
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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
