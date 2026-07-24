using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Charter.Server;

/// <summary>
/// The per-user registry of <see cref="SessionDescriptor"/>s — one file per live review session, named by a
/// SHA-256 of the plan's canonical path so <c>poll &lt;plan&gt;</c> resolves straight to its descriptor. All
/// I/O degrades gracefully: a corrupt/missing descriptor reads back as <c>null</c> (never throws), and writes
/// publish atomically (temp file + <see cref="File.Move(string, string, bool)"/>) so a reader never sees a
/// half-written file. On POSIX each descriptor is <c>0600</c> (owner-only) because it carries the session key.
/// </summary>
public static class SessionRegistry
{
    // 0600 — the owning user may read/write the descriptor; nobody else. It carries the capability key.
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly JsonSerializerOptions DescriptorJson =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>A descriptor paired with the registry file it was read from.</summary>
    public sealed record SessionEntry(SessionDescriptor Descriptor, string Path);

    /// <summary>
    /// The registry file path for <paramref name="planPath"/> inside <paramref name="sessionsDirectory"/>:
    /// <c>&lt;sha256(Path.GetFullPath(planPath))&gt;.json</c>. Canonicalizing first makes the filename stable
    /// across equivalent relative/absolute references to the same plan.
    /// </summary>
    public static string PathForPlan(string sessionsDirectory, string planPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionsDirectory);
        ArgumentException.ThrowIfNullOrEmpty(planPath);

        var canonical = Path.GetFullPath(planPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return Path.Combine(sessionsDirectory, hash + ".json");
    }

    /// <summary>
    /// Atomically publish <paramref name="descriptor"/> into <paramref name="sessionsDirectory"/> and return
    /// the file path. Ensures the directory (0700 on POSIX), writes a unique temp file in the SAME directory,
    /// tightens it to 0600 on POSIX, then move-overwrites the canonical name — so a concurrent reader sees
    /// either the old descriptor or the new one, never a partial write, and the 0600 mode carries through the
    /// rename.
    /// </summary>
    public static string Write(string sessionsDirectory, SessionDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionsDirectory);
        ArgumentNullException.ThrowIfNull(descriptor);

        StateDirectory.EnsureSessionsAt(sessionsDirectory);
        var path = PathForPlan(sessionsDirectory, descriptor.SourcePath);
        var json = JsonSerializer.Serialize(descriptor, DescriptorJson);

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, json);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, OwnerOnlyFile);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            Delete(temp);
            throw;
        }

        return path;
    }

    /// <summary>
    /// Read the descriptor at <paramref name="path"/>, or <c>null</c> when the file is missing, unreadable,
    /// not valid JSON, or missing a required field. The descriptor is a hint, so a bad one must degrade to
    /// "no session" — this never throws.
    /// </summary>
    public static SessionDescriptor? Read(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var descriptor = JsonSerializer.Deserialize<SessionDescriptor>(File.ReadAllText(path), DescriptorJson);
            if (descriptor is null
                || string.IsNullOrEmpty(descriptor.Address)
                || string.IsNullOrEmpty(descriptor.Key)
                || string.IsNullOrEmpty(descriptor.SourcePath))
            {
                return null;
            }

            return descriptor;
        }
        catch (Exception)
        {
            // Corrupt / partially written / unreadable descriptor: treat as absent, never fatal.
            return null;
        }
    }

    /// <summary>The descriptor for <paramref name="planPath"/> in <paramref name="sessionsDirectory"/>, or null.</summary>
    public static SessionDescriptor? ReadForPlan(string sessionsDirectory, string planPath)
        => Read(PathForPlan(sessionsDirectory, planPath));

    /// <summary>
    /// Every readable descriptor in <paramref name="sessionsDirectory"/> paired with its file path. Corrupt
    /// files are skipped; a missing directory yields an empty list.
    /// </summary>
    public static IReadOnlyList<SessionEntry> Enumerate(string sessionsDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionsDirectory);

        var entries = new List<SessionEntry>();
        if (!Directory.Exists(sessionsDirectory))
        {
            return entries;
        }

        foreach (var file in Directory.EnumerateFiles(sessionsDirectory, "*.json"))
        {
            var descriptor = Read(file);
            if (descriptor is not null)
            {
                entries.Add(new SessionEntry(descriptor, file));
            }
        }

        return entries;
    }

    /// <summary>Best-effort delete of the descriptor at <paramref name="path"/>; a failure is swallowed.</summary>
    public static void Delete(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // A leftover descriptor is harmless — the next liveness probe prunes it as stale.
        }
    }
}
