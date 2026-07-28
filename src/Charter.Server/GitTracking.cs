namespace Charter.Server;

/// <summary>
/// Whether a plan's review log has been <b>opted into sharing</b> — i.e. whether git already tracks anything in
/// the plan's <c>.review/</c> directory. This is the gate §5.0 of
/// <c>docs/plans/03-git-mediated-team-review.md</c> makes binding: <i>"A solo reviewer who never intends to
/// share must not be nagged. The §5.1 warnings fire only when the <c>.review/</c> directory is actually TRACKED
/// — untracked, gitignored, or not-a-repo ⇒ silent, and Charter behaves exactly as it does today."</i>
/// </summary>
/// <remarks>
/// <para>
/// The question is deliberately "does git track anything in there", not "is this a repo". A brand-new
/// <c>.review/</c> that this reviewer just created is untracked, so a solo session stays silent; a directory
/// carrying a teammate's committed log is tracked, so the reviewer who joins it is told once that what they
/// write is permanent. That is the same fact, asked the only way that separates the two cases.
/// </para>
/// <para>
/// <c>git ls-files</c> lists only files in the index. A gitignored directory has nothing in the index, and
/// neither has a directory in a repo nobody has committed it to — so both answer <see langword="false"/>
/// without a second command. Anything git cannot answer (absent, not a repo, timeout) is also
/// <see langword="false"/>: unknown must mean silent, never a spurious warning.
/// </para>
/// </remarks>
public static class GitTracking
{
    /// <summary>
    /// Whether git tracks at least one file inside <paramref name="reviewDirectory"/>. Never throws; returns
    /// <see langword="false"/> whenever the answer cannot be established.
    /// </summary>
    /// <remarks>
    /// The review directory is always a direct child of the plan's own directory, so the pathspec is passed as
    /// the bare directory NAME from the plan's directory as the working directory — no absolute path crosses
    /// into git's pathspec parser, which keeps this correct on Windows (backslashes) and inside a checkout whose
    /// path contains spaces.
    /// </remarks>
    public static bool IsTracked(string reviewDirectory)
    {
        if (string.IsNullOrEmpty(reviewDirectory))
        {
            return false;
        }

        string parent;
        string name;
        try
        {
            var full = Path.GetFullPath(reviewDirectory);
            parent = Path.GetDirectoryName(full) ?? string.Empty;
            name = Path.GetFileName(full);
        }
        catch (Exception)
        {
            return false;
        }

        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name) || !Directory.Exists(parent))
        {
            return false;
        }

        return GitCommand.Read(parent, "ls-files", "--", name) is not null;
    }
}
