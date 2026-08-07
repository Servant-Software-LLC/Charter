using System;
using System.IO;

namespace Charter.Server;

/// <summary>
/// A stable id for <b>this checkout</b> of a repository — not for the repository, and not for the path.
/// <para>
/// The consumption ledger (<see cref="ReviewLogLedger"/>) lives outside the working tree and is keyed by the
/// plan's absolute path. That is correct until a checkout is <b>replaced at the same path</b> — a fresh clone
/// over a stale one, a rebuilt container mounting the repo at the same point, a wiped-and-recreated worktree.
/// The new checkout then inherits the old one's ledger, and every committed review record the previous
/// checkout consumed is treated as already delivered. The new agent never sees it, silently (#81).
/// </para>
/// <para>
/// Repository identity cannot detect this: a re-clone has the same remote, the same commits, and the same
/// record ids, so the ledger looks perfectly valid. The only thing that changed is the filesystem object. So
/// the marker has to be <b>clone-scoped by construction</b>, which is why it lives inside <c>.git/</c> —
/// git never carries it to a clone, and no gitignore rule can accidentally start tracking it. A marker under
/// the working tree (say beside <c>&lt;plan&gt;.review/</c>) would be committed the moment that directory is
/// tracked, and would then arrive in the fresh clone it exists to distinguish.
/// </para>
/// <para>
/// <b>Not in a repo is not an error.</b> A solo reviewer with a plan in a plain directory gets
/// <see langword="null"/> and the ledger behaves exactly as it did before — no new required setup
/// (plan-03 §5.0). The check simply does not apply where there is no checkout to replace.
/// </para>
/// </summary>
public static class CheckoutIdentity
{
    private const string MarkerName = "charter-checkout";

    /// <summary>
    /// The id of the checkout containing <paramref name="planPath"/>, creating it on first sight, or
    /// <see langword="null"/> when the plan is not inside a git repository or the marker cannot be
    /// read/written. Never throws: an identity that cannot be established must degrade to the pre-#81
    /// behaviour, never to a failed drain.
    /// </summary>
    public static string? ForPlan(string planPath)
    {
        if (string.IsNullOrEmpty(planPath))
        {
            return null;
        }

        try
        {
            var gitDirectory = FindGitDirectory(Path.GetDirectoryName(Path.GetFullPath(planPath)));
            if (gitDirectory is null)
            {
                return null;
            }

            var marker = Path.Combine(gitDirectory, MarkerName);
            if (File.Exists(marker))
            {
                var existing = File.ReadAllText(marker).Trim();
                if (existing.Length > 0)
                {
                    return existing;
                }
            }

            // First sight of this checkout. A fresh id here is what makes the NEXT comparison meaningful;
            // writing it is not a side effect to avoid.
            var minted = Guid.NewGuid().ToString("N");
            File.WriteAllText(marker, minted + Environment.NewLine);
            return minted;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Walk up from <paramref name="start"/> for a <c>.git</c> entry, resolving the <c>gitdir:</c> indirection
    /// a linked worktree uses — so each worktree is its own checkout, which is exactly right: a recreated
    /// worktree is a replaced checkout.
    /// </summary>
    private static string? FindGitDirectory(string? start)
    {
        var directory = start;
        while (!string.IsNullOrEmpty(directory))
        {
            var candidate = Path.Combine(directory, ".git");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            if (File.Exists(candidate))
            {
                var text = File.ReadAllText(candidate).Trim();
                const string prefix = "gitdir:";
                if (text.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var target = text[prefix.Length..].Trim();
                    var resolved = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(directory, target));
                    return Directory.Exists(resolved) ? resolved : null;
                }

                return null;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }
}
