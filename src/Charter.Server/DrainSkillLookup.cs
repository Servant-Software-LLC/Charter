namespace Charter.Server;

/// <summary>
/// Can this machine resolve <c>/charter-drain</c>? (Charter #116.)
/// </summary>
/// <remarks>
/// <para>
/// The review page hands a reviewer a skill invocation to give their agent (#144), which is only better than
/// the raw command line it replaced if the skill actually exists. Handing over a call that resolves to nothing
/// trades one silent failure for another — and silent-failure-that-looks-like-success is the exact defect
/// #144 set out to remove.
/// </para>
/// <para>
/// <b>This only ever ADDS an instruction; it never withholds one.</b> A "not found" verdict can be wrong —
/// <c>charter skills install --target &lt;dir&gt;</c> puts skills anywhere, and this cannot enumerate every
/// place an agent might look. So the page keeps offering <c>/charter-drain</c> either way and merely adds
/// "install the skills first" when it cannot see the skill. A false negative then costs a reviewer one extra
/// true sentence; a false positive costs them a paste that does nothing and no idea why.
/// </para>
/// </remarks>
public static class DrainSkillLookup
{
    /// <summary>The skill the review page hands over.</summary>
    public const string SkillName = "charter-drain";

    /// <summary>
    /// True when a <c>charter-drain</c> skill directory is visible in one of the places
    /// <c>charter skills install</c> writes to: the user-home install (<c>~/.claude/skills</c>, the default)
    /// or a project install (<c>.claude/skills</c>) at or above <paramref name="planPath"/>.
    /// </summary>
    /// <remarks>
    /// The plan is the only anchor the server has for "the project", and it is a good one: an agent reviewing
    /// this plan is working in this repo, so a repo-scoped <c>--project</c> install lives at or above it. The
    /// server's own working directory is deliberately NOT used — <c>charter review</c> is frequently launched
    /// by an agent from somewhere unrelated to the plan.
    /// </remarks>
    public static bool IsInstalled(string? planPath)
        => IsInstalled(planPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// <paramref name="homeDirectory"/>-injectable overload. Exists so a test can be HERMETIC: the real home
    /// is the developer's own, and a machine that happens to have the skill installed would otherwise make
    /// the missing-skill case unassertable (it did, the first time this was written).
    /// </summary>
    internal static bool IsInstalled(string? planPath, string? homeDirectory)
    {
        foreach (var directory in CandidateSkillDirectories(planPath, homeDirectory))
        {
            try
            {
                if (Directory.Exists(Path.Combine(directory, SkillName)))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // An unreadable candidate is not evidence of absence; try the next.
            }
        }

        return false;
    }

    private static IEnumerable<string> CandidateSkillDirectories(string? planPath, string? homeDirectory)
    {
        if (!string.IsNullOrEmpty(homeDirectory))
        {
            yield return Path.Combine(homeDirectory, ".claude", "skills");
        }

        if (string.IsNullOrEmpty(planPath))
        {
            yield break;
        }

        DirectoryInfo? directory;
        try
        {
            directory = new FileInfo(Path.GetFullPath(planPath)).Directory;
        }
        catch (Exception)
        {
            yield break;
        }

        // Walk up from the plan looking for a project-scoped install, STOPPING AT THE REPO ROOT — the first
        // ancestor carrying a `.git`. "The project" is the repo the plan lives in, not every ancestor up to
        // the drive root: an unbounded walk from a plan anywhere under a user's home eventually reaches the
        // home directory itself and reports its `~/.claude/skills` as a project install. That is the same
        // answer by luck rather than by reasoning, and it makes the two cases indistinguishable.
        //
        // Depth-capped as well, so a plan outside any repo cannot turn a page load into a filesystem crawl.
        for (var depth = 0; directory is not null && depth < 24; depth++, directory = directory.Parent)
        {
            yield return Path.Combine(directory.FullName, ".claude", "skills");

            var atRepoRoot = false;
            try
            {
                atRepoRoot = Directory.Exists(Path.Combine(directory.FullName, ".git"))
                    || File.Exists(Path.Combine(directory.FullName, ".git"));   // a worktree's .git is a FILE
            }
            catch (Exception)
            {
                // Unreadable ancestor — keep walking rather than stopping early.
            }

            if (atRepoRoot)
            {
                yield break;
            }
        }
    }
}
