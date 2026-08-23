using System.Diagnostics;
using System.Text;

namespace Charter.Cli;

/// <summary>
/// Three cheap, best-effort questions about a directory's relationship to git: does the repository TRACK
/// anything under it, does anything under it have UNCOMMITTED changes (both Charter #154), and is it inside a
/// working tree AT ALL (Charter #194).
/// </summary>
/// <remarks>
/// <para>
/// <c>charter skills install --force</c> deletes its destination and re-extracts the bundled copy. That is
/// correct and harmless for <c>~/.claude/skills</c>, which holds installed copies and nothing else. It is not
/// harmless for <c>--project</c> or a <c>--target</c> pointed at a repository that tracks its skills as
/// SOURCE — which is what this repository does, and what anyone authoring skills alongside their code will
/// do. There, <c>--force</c> destroys authored work, and uncommitted work is the part no <c>git checkout</c>
/// can bring back.
/// </para>
/// <para>
/// The third question is the complement of the first two, not a refinement of them. #154 keys on skills a
/// repository ALREADY TRACKS; #194 is the case where brand-new, untracked skills land in somebody's work tree
/// because the destination turned out to be inside one — <c>~/.claude/skills</c> being a symlink into a
/// dotfiles repo, say. Both conjuncts of the #154 guard are false there, so it stays quiet, and until #194
/// nothing else spoke either.
/// </para>
/// <para>
/// The path to that command is not obscure: <c>charter --version</c> PRINTS it as the remedy for stale
/// skills, so the natural response is to run it wherever you happen to be standing.
/// </para>
/// <para>
/// <b>Failure answers "no", always.</b> git missing or off PATH, the path outside any repository, a
/// permission fault, a timeout — every one of them answers <see langword="false"/>. The overwhelmingly common
/// destination (an install directory in no repository at all) is indistinguishable from an unanswerable
/// probe, and <see langword="false"/> is what preserves the behaviour Charter had before this existed. A
/// probe failure therefore never breaks a working install; it only declines to add the new protection.
/// </para>
/// <para>
/// Mirrors Guardrails' <c>GitWorkingTree</c> (their #461), which shipped first and whose shape this follows
/// deliberately — the same footgun deserves the same remedy in both tools.
/// </para>
/// </remarks>
internal static class GitWorkingTree
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// True when the repository containing <paramref name="path"/> tracks at least one file at or under it —
    /// i.e. the directory is authored SOURCE. False when the path does not exist, lies outside a repository,
    /// holds only untracked/ignored files, or git cannot be run.
    /// </summary>
    public static bool TracksAnyFileUnder(string path)
        => !string.IsNullOrWhiteSpace(Run(path, "ls-files", "--", "."));

    /// <summary>
    /// True when git reports ANY pending change at or under <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// <c>git status --porcelain</c> reports modified, staged, deleted AND untracked-but-not-ignored.
    /// Untracked counts deliberately: <c>--force</c> deletes the whole destination folder, so a
    /// never-committed file inside it is precisely the thing git could not restore. Scoped by pathspec, so
    /// unrelated dirt elsewhere in the repository is not this directory's problem.
    /// </remarks>
    public static bool HasLocalChangesUnder(string path)
        => !string.IsNullOrWhiteSpace(Run(path, "status", "--porcelain", "--", "."));

    /// <summary>
    /// Where <paramref name="path"/> sits inside a git working tree, or <see langword="null"/> when it sits
    /// in none — which, per this class's contract, is also the answer whenever git cannot say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves come from ONE <c>git rev-parse --show-toplevel --show-prefix</c>, run with
    /// <paramref name="path"/> as the working directory. That matters more than it looks: git resolves its
    /// own working directory to a PHYSICAL path, so a target reached through a symlink or a junction reports
    /// the repository it really lands in rather than the path the caller typed. Reasoning about the path
    /// string instead would be a no-op in precisely the case #194 was filed for.
    /// </para>
    /// <para>
    /// <c>--show-prefix</c> is what makes the advisory's ignore rules correct without assuming a layout: git
    /// states the target's own position under the root (forward slashes, trailing slash, empty at the root),
    /// so the rules can be anchored at the root the reader would actually paste them into.
    /// </para>
    /// </remarks>
    public static WorkTreeLocation? LocateWorkTree(string path)
    {
        string? output = Run(path, "rev-parse", "--show-toplevel", "--show-prefix");
        if (output is null)
        {
            return null;
        }

        string[] lines = output.Split('\n');
        if (lines.Length < 2)
        {
            // git answered in a shape this does not understand. Under the fail-to-"no" contract that is
            // silence, never a guess — a guessed prefix would print ignore rules pointing at nothing.
            return null;
        }

        string root = lines[0].TrimEnd('\r');
        string prefix = lines[1].TrimEnd('\r');

        return string.IsNullOrEmpty(root) ? null : new WorkTreeLocation(root, prefix);
    }

    /// <summary>
    /// A directory's position inside a working tree: the repository <paramref name="Root"/> as git spells it,
    /// and <paramref name="PrefixWithinRoot"/>, the slash-terminated path from that root down to the
    /// directory (empty when it IS the root).
    /// </summary>
    public sealed record WorkTreeLocation(string Root, string PrefixWithinRoot);

    /// <summary>
    /// Run <c>git</c> with <paramref name="workingDirectory"/> as its cwd and return stdout, or
    /// <see langword="null"/> on ANY failure (missing directory, git absent, non-zero exit — exit 128 is
    /// "not a git repository"). Never throws.
    /// </summary>
    private static string? Run(string workingDirectory, params string[] arguments)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,

                // git emits UTF-8; without this .NET decodes the redirected stream using the CONSOLE's code
                // page, which on Windows is an OEM one. `ls-files` output is repository PATHS, which are
                // routinely non-ASCII — the same trap Charter #122 fixed for the recap reader.
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

            // Both pipes read concurrently BEFORE waiting: a large `ls-files` can exceed the OS pipe buffer,
            // and a sequential ReadToEnd()-then-WaitForExit() deadlocks when it does.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                TryKill(process);
                return null;
            }

            var output = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;   // git absent, permission fault, anything: the answer is "no".
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best-effort teardown.
        }
    }
}
