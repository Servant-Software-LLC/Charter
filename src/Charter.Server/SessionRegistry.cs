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

        // A new session publishing its descriptor is the natural, already-happening moment to sweep the ones
        // whose processes have gone (Charter #147). Doing it here as well as on read means the directory
        // self-corrects on the ordinary path — starting a review — rather than only when something enumerates.
        try { Prune(sessionsDirectory); } catch (Exception) { /* never block a session over housekeeping */ }

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
    /// files are skipped; a missing directory yields an empty list. <b>Prunes as it reads</b> — see
    /// <see cref="Prune"/> — so a descriptor whose review process is provably gone never reaches a caller.
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
            if (descriptor is null)
            {
                continue;
            }

            if (IsProvablyDead(descriptor))
            {
                Delete(file);
                continue;
            }

            entries.Add(new SessionEntry(descriptor, file));
        }

        return entries;
    }

    /// <summary>
    /// Delete every descriptor in <paramref name="sessionsDirectory"/> whose session is PROVABLY gone,
    /// returning how many were removed (Charter #147).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the "next liveness probe" <see cref="Delete"/> has always promised in its catch block, and which
    /// never existed: no consumer of the registry checked a descriptor's <see cref="SessionDescriptor.Pid"/>,
    /// and nothing ever swept the directory. On a working machine that left 11 dead entries beside 1 live one,
    /// the oldest 19 days old, each still carrying a loopback address and a capability key.
    /// </para>
    /// <para>
    /// <b>The pid is used as a NEGATIVE signal only</b>, which is what makes this safe despite
    /// <see cref="SessionDescriptor.Pid"/> being documented as unusable for liveness. Pid reuse can make a dead
    /// session look alive; it can never make a live session look dead. So "no process with this id" proves the
    /// session is gone, while "a process with this id exists" proves nothing and is treated as KEEP. Every
    /// ambiguity errs toward keeping — a retained stale descriptor costs a wasted probe, a wrongly-deleted live
    /// one costs a working session.
    /// </para>
    /// </remarks>
    public static int Prune(string sessionsDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionsDirectory);
        if (!Directory.Exists(sessionsDirectory))
        {
            return 0;
        }

        var pruned = 0;
        foreach (var file in Directory.EnumerateFiles(sessionsDirectory, "*.json"))
        {
            var descriptor = Read(file);
            if (descriptor is null || IsProvablyDead(descriptor))
            {
                Delete(file);
                pruned++;
            }
        }

        return pruned;
    }

    /// <summary>
    /// True when this descriptor's session is provably gone: its review process no longer exists, or the plan
    /// it was serving is no longer on disk (a review server for a deleted file cannot be useful — four of the
    /// eleven leaked entries were exactly that, pointing into temp directories another tool had since swept).
    /// Any doubt — an unreadable process table, a permission error — answers <see langword="false"/>.
    /// </summary>
    public static bool IsProvablyDead(SessionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!string.IsNullOrEmpty(descriptor.SourcePath) && !File.Exists(descriptor.SourcePath))
        {
            return true;
        }

        if (descriptor.Pid <= 0)
        {
            return false;   // nothing to check against; keep
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(descriptor.Pid);
            if (process.HasExited)
            {
                return true;
            }

            // Pid reuse, defeated without risking a false delete. A process cannot have written a descriptor
            // before it started, so a start time LATER than the descriptor's proves this is a different
            // process wearing the same id. Observed immediately in the wild: a review session's pid had been
            // recycled to msedge four days after the descriptor was written, and the pid-existence check alone
            // kept the dead entry forever.
            //
            // The inequality only ever runs one way, which is what makes it safe: a genuinely live session
            // started BEFORE it published, so it can never be caught by this. The tolerance absorbs clock
            // jitter between the two local readings; any failure to read a start time falls through to KEEP.
            var started = process.StartTime.ToUniversalTime();
            if (started > descriptor.CreatedAt.UtcDateTime.AddSeconds(5))
            {
                return true;
            }

            return false;
        }
        catch (ArgumentException)
        {
            return true;    // no process carries this id — the one unambiguous "gone"
        }
        catch (Exception)
        {
            return false;   // could not tell; keep
        }
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
            // A leftover descriptor is harmless: `Prune` sweeps it on the next `Enumerate` or the next session
            // `Write`. That was once a comment describing a mechanism nobody had built (Charter #147) — the
            // registry accumulated dead entries for weeks because no consumer ever checked a pid. It is true now.
        }
    }
}
