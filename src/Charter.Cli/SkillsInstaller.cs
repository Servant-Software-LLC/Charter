using System.Reflection;

namespace Charter.Cli;

/// <summary>
/// Extracts the Claude skills bundled inside this tool — embedded as resources under the
/// <c>charter-skills/</c> prefix (see <c>Charter.Cli.csproj</c>) so they travel INSIDE the single
/// self-contained binary — into Claude Code's skills directory. Each embedded resource is named
/// <c>charter-skills/&lt;skill&gt;/&lt;relative path&gt;</c>; the first path segment is the skill folder,
/// the rest reconstructs its tree under the target.
///
/// Kept free of console and CLI concerns so it can be unit-/process-tested against a temp target dir.
/// The command layer (<see cref="SkillsCommand"/>) owns resolving paths, printing, and exit codes.
///
/// Each INSTALLED skill's top-level <c>SKILL.md</c> is then version-stamped
/// (<c>metadata.charter-version</c>) via <see cref="SkillFrontmatterStamper"/> using the running tool's
/// version, so staleness is later detectable even though the embedded/published source is unstamped.
/// Mirrors Guardrails' <c>SkillsInstaller</c> (adapted to read embedded resources rather than a loose
/// bundled folder, because Charter ships as one self-contained binary).
/// </summary>
internal static class SkillsInstaller
{
    /// <summary>Manifest-name prefix of every bundled skill resource (matches the csproj LogicalName).</summary>
    private const string ResourcePrefix = "charter-skills/";

    /// <summary>The top-level skill file that carries the version stamp.</summary>
    private const string SkillFileName = "SKILL.md";

    /// <summary>What happened to a single skill folder during an install.</summary>
    public enum SkillOutcome
    {
        /// <summary>Extracted into the target (fresh, or overwritten under <c>force</c>).</summary>
        Installed,

        /// <summary>Already present in the target and left untouched (no <c>force</c>).</summary>
        Skipped,
    }

    /// <summary>The per-skill result of an install pass.</summary>
    public sealed record SkillResult(string Name, SkillOutcome Outcome);

    /// <summary>
    /// Extract every bundled skill folder into <paramref name="targetDir"/>. With
    /// <paramref name="force"/>, an existing target skill folder is replaced; without it, an existing
    /// folder is left untouched and reported <see cref="SkillOutcome.Skipped"/>. Results are ordered by
    /// skill name (ordinal). Each INSTALLED skill's top-level <c>SKILL.md</c> is stamped with
    /// <paramref name="toolVersion"/> under <c>metadata.charter-version</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">This build carries no bundled skills.</exception>
    public static IReadOnlyList<SkillResult> InstallAll(string targetDir, bool force, string toolVersion)
    {
        ArgumentNullException.ThrowIfNull(targetDir);
        ArgumentNullException.ThrowIfNull(toolVersion);

        Assembly assembly = typeof(SkillsInstaller).Assembly;
        IReadOnlyDictionary<string, List<SkillFile>> skills = GroupResourcesBySkill(assembly);

        if (skills.Count == 0)
        {
            throw new InvalidOperationException(
                "This build of charter does not carry its bundled skills. Re-pack/re-install the tool.");
        }

        Directory.CreateDirectory(targetDir);

        var results = new List<SkillResult>();
        foreach ((string skillName, List<SkillFile> files) in skills)
        {
            string destination = Path.Combine(targetDir, skillName);

            if (Directory.Exists(destination) && !force)
            {
                results.Add(new SkillResult(skillName, SkillOutcome.Skipped));
                continue;
            }

            if (Directory.Exists(destination))
            {
                DeleteDirectory(destination);
            }

            foreach (SkillFile file in files)
            {
                ExtractResource(assembly, file, destination);
            }

            StampInstalledSkillVersion(destination, toolVersion);
            results.Add(new SkillResult(skillName, SkillOutcome.Installed));
        }

        return results;
    }

    /// <summary>
    /// Resolve where skills should be installed, in precedence order: an explicit
    /// <paramref name="target"/> wins; else <paramref name="project"/> means <c>./.claude/skills</c> under
    /// the current directory (a repo-scoped install); else the default <c>~/.claude/skills</c> in the user
    /// home (available in every repo). The chosen directory is created by <see cref="InstallAll"/> if it
    /// does not yet exist. Uses <see cref="Environment.SpecialFolder.UserProfile"/> so the home path
    /// resolves cross-platform with no hard-coded separators.
    /// </summary>
    public static string ResolveTargetDir(string? target, bool project)
    {
        if (!string.IsNullOrWhiteSpace(target))
        {
            return target;
        }

        if (project)
        {
            return Path.Combine(Directory.GetCurrentDirectory(), ".claude", "skills");
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".claude", "skills");
    }

    /// <summary>One bundled file: the skill it belongs to, its path within that skill, and its resource name.</summary>
    private sealed record SkillFile(string RelativePath, string ResourceName);

    /// <summary>
    /// Group the <c>charter-skills/</c> manifest resources by their first path segment (the skill folder),
    /// into a name-ordered map of files. A resource with no path segment after the skill folder is ignored.
    /// </summary>
    private static IReadOnlyDictionary<string, List<SkillFile>> GroupResourcesBySkill(Assembly assembly)
    {
        var skills = new SortedDictionary<string, List<SkillFile>>(StringComparer.Ordinal);

        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string relative = resourceName[ResourcePrefix.Length..];
            int firstSlash = relative.IndexOf('/');
            if (firstSlash <= 0 || firstSlash == relative.Length - 1)
            {
                continue; // no skill folder, or nothing after it
            }

            string skillName = relative[..firstSlash];
            string withinSkill = relative[(firstSlash + 1)..];

            if (!skills.TryGetValue(skillName, out List<SkillFile>? files))
            {
                files = new List<SkillFile>();
                skills[skillName] = files;
            }

            files.Add(new SkillFile(withinSkill, resourceName));
        }

        return skills;
    }

    /// <summary>Write one embedded resource to its path under the destination skill folder.</summary>
    private static void ExtractResource(Assembly assembly, SkillFile file, string destinationSkillDir)
    {
        string[] segments = file.RelativePath.Split('/');
        foreach (string segment in segments)
        {
            if (segment is "" or "." or "..")
            {
                throw new InvalidOperationException($"Unsafe bundled skill path: {file.RelativePath}");
            }
        }

        string targetPath = destinationSkillDir;
        foreach (string segment in segments)
        {
            targetPath = Path.Combine(targetPath, segment);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        using Stream stream = assembly.GetManifestResourceStream(file.ResourceName)
            ?? throw new InvalidOperationException($"Embedded skill resource '{file.ResourceName}' was not found.");
        using FileStream output = File.Create(targetPath);
        stream.CopyTo(output);
    }

    /// <summary>
    /// Stamp <paramref name="toolVersion"/> into the destination skill folder's top-level <c>SKILL.md</c>
    /// frontmatter. A folder with no top-level <c>SKILL.md</c> is left as extracted.
    /// </summary>
    private static void StampInstalledSkillVersion(string destinationSkillDir, string toolVersion)
    {
        string skillMd = Path.Combine(destinationSkillDir, SkillFileName);
        if (!File.Exists(skillMd))
        {
            return;
        }

        string original = File.ReadAllText(skillMd);
        string stamped = SkillFrontmatterStamper.Stamp(original, toolVersion);
        if (!string.Equals(stamped, original, StringComparison.Ordinal))
        {
            File.WriteAllText(skillMd, stamped);
        }
    }

    /// <summary>
    /// Recursively delete <paramref name="path"/>, first clearing any read-only attribute so a forced
    /// reinstall never fails with Access Denied on Windows.
    /// </summary>
    private static void DeleteDirectory(string path)
    {
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
