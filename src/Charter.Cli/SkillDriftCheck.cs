namespace Charter.Cli;

/// <summary>
/// Detects installed Charter skills whose stamped <c>metadata.charter-version</c> no longer matches the
/// running tool, so <c>charter --version</c> can warn the user to reinstall them. The stamp is written at
/// INSTALL time by <see cref="SkillsInstaller"/> (via <see cref="SkillFrontmatterStamper"/>), and this check
/// reads it back through <see cref="SkillFrontmatterStamper.ReadVersion"/> — the same shape install wrote, so
/// the two never drift in interpretation. Mirrors Guardrails' skill-version-drift warning.
///
/// The scan is confined to the two standard skills roots that <see cref="SkillsInstaller.ResolveTargetDir"/>
/// resolves to (<c>~/.claude/skills</c> and <c>./.claude/skills</c>) — the SAME resolution install uses to
/// WRITE them, so the reader and writer can never diverge on location. A skill that is absent, unstamped, or
/// unreadable is simply not reported (no warning, no throw); only a present-and-mismatched stamp is stale.
/// </summary>
internal static class SkillDriftCheck
{
    /// <summary>The bundled skill folders whose installed copies carry a version stamp.</summary>
    private static readonly string[] SkillNames = { "charter", "charter-format" };

    /// <summary>The top-level skill file that carries the version stamp.</summary>
    private const string SkillFileName = "SKILL.md";

    /// <summary>One installed skill whose stamped version differs from the running tool.</summary>
    public sealed record StaleSkill(string Name, string InstalledVersion, string Directory);

    /// <summary>
    /// Scan the two standard skills roots (<c>~/.claude/skills</c> and <c>./.claude/skills</c>) for installed
    /// <c>charter</c> / <c>charter-format</c> skills whose stamped version differs from
    /// <paramref name="currentVersion"/>. Resolves the roots exactly as <see cref="SkillsInstaller"/> does.
    /// </summary>
    public static IReadOnlyList<StaleSkill> FindStaleSkills(string currentVersion)
    {
        var roots = new[]
        {
            SkillsInstaller.ResolveTargetDir(target: null, project: false), // ~/.claude/skills
            SkillsInstaller.ResolveTargetDir(target: null, project: true),  // ./.claude/skills
        };

        return FindStaleSkillsIn(roots, currentVersion);
    }

    /// <summary>
    /// The testable core: scan the given <paramref name="roots"/> for installed skills whose stamped version
    /// differs from <paramref name="currentVersion"/>. Roots that resolve to the same absolute path are
    /// scanned once (home and cwd can coincide). Never throws on a missing/unreadable skill.
    /// </summary>
    public static IReadOnlyList<StaleSkill> FindStaleSkillsIn(IEnumerable<string> roots, string currentVersion)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(currentVersion);

        var stale = new List<StaleSkill>();
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots)
        {
            string fullRoot = SafeFullPath(root);
            if (fullRoot.Length == 0 || !seenRoots.Add(fullRoot))
            {
                continue; // unresolvable, or a duplicate of a root already scanned
            }

            foreach (string skill in SkillNames)
            {
                string directory = Path.Combine(fullRoot, skill);
                string? installed = TryReadStampedVersion(Path.Combine(directory, SkillFileName));

                // Absent or unstamped -> nothing to compare -> not reported. Only a present, differing stamp
                // is stale.
                if (!string.IsNullOrEmpty(installed)
                    && !string.Equals(installed, currentVersion, StringComparison.Ordinal))
                {
                    stale.Add(new StaleSkill(skill, installed!, directory));
                }
            }
        }

        return stale;
    }

    /// <summary>Read the <c>metadata.charter-version</c> stamp from a <c>SKILL.md</c>, or <c>null</c> when the
    /// file is missing, unreadable, or carries no stamp.</summary>
    private static string? TryReadStampedVersion(string skillMdPath)
    {
        try
        {
            return File.Exists(skillMdPath)
                ? SkillFrontmatterStamper.ReadVersion(File.ReadAllText(skillMdPath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null; // an unreadable skill must never break `charter --version`
        }
    }

    /// <summary><see cref="Path.GetFullPath(string)"/>, or an empty string for a malformed path (rather than
    /// throwing) so one bad root never aborts the scan.</summary>
    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }
}
