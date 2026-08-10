using Charter.Core;

namespace Charter.Server;

/// <summary>
/// Who a review log's records are attributed to, and whether that came from git. When
/// <see cref="FromGit"/> is false the identity is a clearly-marked local stand-in and
/// <see cref="Reason"/> says why, so the CLI can warn the reviewer that their comments will not be
/// attributable to them on a teammate's machine — <b>without</b> refusing to run.
/// </summary>
/// <param name="Author">The author every record is written as.</param>
/// <param name="FromGit">Whether <c>git config user.name</c>/<c>user.email</c> supplied the identity.</param>
/// <param name="Reason">Why the fallback was used, or <see langword="null"/> when git supplied the identity.</param>
public sealed record ReviewIdentity(ReviewAuthor Author, bool FromGit, string? Reason);

/// <summary>
/// Resolves the reviewing author from git, <b>read-only</b>. §5.1 of the design of record separates two
/// claims that "Charter never runs git" used to conflate: Charter must never MUTATE git state (it does not
/// commit, push, stage, or rewrite history), but reading it is both permitted and load-bearing — without a
/// real identity the whole per-author log scheme has nothing to key on.
/// </summary>
/// <remarks>
/// Every failure mode — git not installed, not a repository, <c>user.email</c> unset, a hung process — falls
/// back to a local identity rather than failing the review. Local-only single-reviewer use is a supported
/// mode (§7's opt-out is exactly that), so it must keep working on a machine that has never seen git.
/// </remarks>
public static class GitIdentity
{
    /// <summary>The domain that marks a fallback identity as machine-local rather than a real address.</summary>
    public const string LocalDomain = "localhost";

    /// <summary>
    /// Read <c>user.name</c> / <c>user.email</c> from git as configured for
    /// <paramref name="workingDirectory"/> (so a repo-local identity wins over the global one, exactly as it
    /// would for a commit), or return a marked local fallback. Never throws.
    /// </summary>
    public static ReviewIdentity Resolve(string workingDirectory)
    {
        var directory = ResolveWorkingDirectory(workingDirectory);

        var email = ReadConfig(directory, "user.email");
        if (string.IsNullOrEmpty(email))
        {
            return Local(
                "git did not report a user.email for this directory (git may be absent, this may not be a "
                + "repository, or the identity may be unset)");
        }

        var name = ReadConfig(directory, "user.name");
        return new ReviewIdentity(
            new ReviewAuthor(string.IsNullOrEmpty(name) ? email : name, email),
            FromGit: true,
            Reason: null);
    }

    /// <summary>
    /// The marked machine-local identity: the OS user at <see cref="LocalDomain"/>. It is deliberately NOT a
    /// plausible address — a teammate reading the log must be able to see at a glance that Charter did not
    /// know who wrote it.
    /// </summary>
    public static ReviewIdentity Local(string reason)
    {
        var user = SanitizeUser(Environment.UserName);
        return new ReviewIdentity(
            new ReviewAuthor($"{user} (local identity)", $"{user}@{LocalDomain}"),
            FromGit: false,
            Reason: reason);
    }

    // The plan's own directory, falling back to the process cwd — git config is directory-scoped, so asking
    // from the wrong place can silently return a different (or no) identity.
    private static string ResolveWorkingDirectory(string workingDirectory)
    {
        try
        {
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
            {
                return Path.GetFullPath(workingDirectory);
            }
        }
        catch (Exception)
        {
            // An unusable path is simply not usable as a working directory; fall through to the cwd.
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// One <c>git config --get &lt;key&gt;</c>, or null on any failure. <c>--get</c> is a pure read; nothing
    /// here can alter a repository. Shares <see cref="GitCommand"/> with the tracked-directory read, so there
    /// is exactly one place in the review SERVER that spawns git. (The CLI's <c>charter recap</c> has its own
    /// reader — same read-only rule, different failure and timeout policy.)
    /// </summary>
    private static string? ReadConfig(string workingDirectory, string key)
        => GitCommand.Read(workingDirectory, "config", "--get", key);

    // Keep the fallback address parseable and file-name-safe: the log's file name is derived from it.
    private static string SanitizeUser(string? user)
    {
        var slug = string.IsNullOrWhiteSpace(user) ? null : ReviewLogPaths.Slug(user);
        return string.IsNullOrEmpty(slug) ? "charter-local" : slug;
    }
}
