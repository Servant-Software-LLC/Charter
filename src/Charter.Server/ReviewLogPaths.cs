using System.Security.Cryptography;
using System.Text;

namespace Charter.Server;

/// <summary>
/// Where a plan's per-author review logs live, and what they are called (§2 of
/// <c>docs/plans/03-git-mediated-team-review.md</c>, which is normative). Pure path arithmetic — no I/O — so
/// the writer, the review server's panel read, and <c>charter poll</c>'s server-less read all compute exactly
/// the same names for one plan.
/// </summary>
/// <remarks>
/// <para>
/// The layout is <c>&lt;planDir&gt;/&lt;plan file name minus .md&gt;.review/&lt;slug&gt;.&lt;hash&gt;.jsonl</c>
/// — so <c>tenant-rate-limit.charter.md</c> gets <c>tenant-rate-limit.charter.review/</c>. The directory sits
/// BESIDE the plan, inside the repo, because the log is the transport: it travels by git.
/// </para>
/// <para>
/// <b>Both halves of the file name are computed from the LOWERCASED email, and that is load-bearing.</b> The
/// fold compares author identity case-insensitively (so a capitalisation change in <c>git config</c> cannot
/// cost someone the right to withdraw their own comment); if the file name disagreed, one person would end up
/// with two files and their own records would look like a stranger's.
/// </para>
/// <para>
/// <b>The hash is identity; the slug is only legibility.</b> <c>alice@ng.example.com</c> and
/// <c>alice.ng@example.com</c> slug alike, and with one file per author for the life of the plan they would
/// otherwise SHARE a file — interleaving two people's records and conflicting on every parallel review.
/// </para>
/// </remarks>
public static class ReviewLogPaths
{
    /// <summary>The suffix appended to the plan's name (minus <c>.md</c>) to form the log directory.</summary>
    public const string DirectorySuffix = ".review";

    /// <summary>The extension every per-author log carries. One JSON object per line (§3).</summary>
    public const string LogExtension = ".jsonl";

    /// <summary>The glob that enumerates a review directory's per-author logs.</summary>
    public const string LogSearchPattern = "*" + LogExtension;

    // A slug that is merely long adds nothing (the hash already carries identity) and risks a
    // PathTooLongException on a deep checkout, so it is capped. The cap never affects distinctness.
    private const int MaxSlugLength = 64;

    // The slug for an email with no ASCII alphanumerics at all. The hash still distinguishes such authors.
    private const string EmptySlug = "author";

    /// <summary>
    /// The review-log directory for <paramref name="planPath"/>: the plan's own directory plus its file name
    /// with a trailing <c>.md</c> replaced by <c>.review</c>. A plan whose name does not end in <c>.md</c>
    /// keeps its whole name (never a silently truncated one).
    /// </summary>
    public static string DirectoryForPlan(string planPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(planPath);

        var full = Path.GetFullPath(planPath);
        var directory = Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileName(full);
        if (fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^3];
        }

        return Path.Combine(directory, fileName + DirectorySuffix);
    }

    /// <summary>
    /// The log file name for <paramref name="email"/>: <c>&lt;slug&gt;.&lt;hash&gt;.jsonl</c>, both halves
    /// computed from the lowercased address.
    /// </summary>
    public static string FileNameForAuthor(string email)
    {
        ArgumentException.ThrowIfNullOrEmpty(email);

        var normalized = Normalize(email);
        return Slug(normalized) + "." + Hash(normalized) + LogExtension;
    }

    /// <summary>The full path of <paramref name="email"/>'s log for the plan at <paramref name="planPath"/>.</summary>
    public static string LogForAuthor(string planPath, string email)
        => Path.Combine(DirectoryForPlan(planPath), FileNameForAuthor(email));

    /// <summary>
    /// The human-readable half of the file name: runs of ASCII alphanumerics in the LOWERCASED address, joined
    /// by <c>-</c>. Deliberately not a general slugifier — anything outside <c>[a-z0-9]</c> (including
    /// non-ASCII letters, which have no portable lowercase form across file systems) is a separator, so the
    /// name is safe on every platform Charter ships to.
    /// </summary>
    public static string Slug(string email)
    {
        ArgumentException.ThrowIfNullOrEmpty(email);

        var slug = new StringBuilder(email.Length);
        var pendingSeparator = false;
        foreach (var c in Normalize(email))
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingSeparator && slug.Length > 0)
                {
                    slug.Append('-');
                }

                pendingSeparator = false;
                slug.Append(c);
                if (slug.Length == MaxSlugLength)
                {
                    break;
                }

                continue;
            }

            pendingSeparator = true;
        }

        return slug.Length == 0 ? EmptySlug : slug.ToString();
    }

    /// <summary>
    /// The identity half of the file name: the first 8 hex digits of <c>sha256</c> of the LOWERCASED address.
    /// Short because it only has to separate the members of one team, not resist collision attacks.
    /// </summary>
    public static string Hash(string email)
    {
        ArgumentException.ThrowIfNullOrEmpty(email);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(email)));
        return Convert.ToHexString(digest)[..8].ToLowerInvariant();
    }

    /// <summary>
    /// Every per-author log in <paramref name="reviewDirectory"/>, ordered by file name so a fold's
    /// line-scoped diagnostics are reported in a stable order on every machine. Empty when the directory does
    /// not exist — a plan nobody has commented on is not an error.
    /// </summary>
    /// <remarks>
    /// The empty list answers two different questions the same way: <i>the directory holds no logs</i> and
    /// <i>there is no directory</i>. That is deliberate here — this is path arithmetic, and every caller that
    /// only wants the names is entitled to the shorter answer — but a caller reporting what a reviewer SAID
    /// must separate them, or it asserts "nobody commented" on the strength of a directory it never opened
    /// (Charter #221). <see cref="ReviewLogStore.Read(string)"/> is where that separation is made.
    /// </remarks>
    public static IReadOnlyList<string> EnumerateLogs(string reviewDirectory)
    {
        if (string.IsNullOrEmpty(reviewDirectory) || !Directory.Exists(reviewDirectory))
        {
            return Array.Empty<string>();
        }

        var logs = Directory.GetFiles(reviewDirectory, LogSearchPattern, SearchOption.TopDirectoryOnly);
        Array.Sort(logs, StringComparer.Ordinal);
        return logs;
    }

    // The one normalization both halves of the file name and the fold's identity comparison agree on.
    private static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
