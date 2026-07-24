namespace Charter.Server;

/// <summary>
/// Resolves the per-user directory where <see cref="SessionRegistry"/> keeps its session descriptors — the
/// registry <c>charter poll</c> discovers a running <c>charter review</c> server through. The location follows
/// each OS's state-dir convention so a descriptor (which carries the session's capability key) lives under the
/// invoking user's account, never in a world-readable temp dir.
/// </summary>
/// <remarks>
/// Layout (the <c>sessions</c> leaf under a per-user base):
/// <list type="bullet">
///   <item>Windows: <c>%LOCALAPPDATA%\Charter\sessions\</c> (per-user ACL is the guard).</item>
///   <item>macOS: <c>~/Library/Application Support/Charter/sessions/</c>.</item>
///   <item>Linux/other POSIX: <c>$XDG_STATE_HOME/charter/sessions/</c>, else <c>~/.local/state/charter/sessions/</c>.</item>
/// </list>
/// The <c>CHARTER_STATE_DIR</c> environment variable overrides the whole resolved sessions directory — an
/// additive test/isolation seam (used to give a spawned <c>review</c>+<c>poll</c> pair their own registry).
/// When unset, the OS defaults above apply, so it changes no shipped behaviour. On POSIX the directory is
/// created <c>0700</c> (owner-only) via <see cref="File.SetUnixFileMode(string, UnixFileMode)"/>; on Windows
/// the per-user <c>%LOCALAPPDATA%</c> ACL is the equivalent confinement.
/// </remarks>
public static class StateDirectory
{
    /// <summary>Environment variable that, when set, overrides the resolved sessions directory verbatim.</summary>
    public const string OverrideEnvironmentVariable = "CHARTER_STATE_DIR";

    // 0700 — owner may read/write/traverse; nobody else. The descriptors under it carry capability keys.
    private const UnixFileMode OwnerOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// The resolved sessions directory: the <c>CHARTER_STATE_DIR</c> override when set, otherwise the
    /// OS-specific per-user state location. Pure path resolution — performs no I/O.
    /// </summary>
    public static string Sessions()
    {
        var overridden = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        return !string.IsNullOrEmpty(overridden)
            ? overridden
            : Path.Combine(BaseStateDirectory(), "sessions");
    }

    /// <summary>
    /// The resolved review-sidecar directory: the server-owned durable home for a session's queued
    /// annotations/answers (§1.6). It lives alongside the sessions registry so the same
    /// <c>CHARTER_STATE_DIR</c> override isolates both in tests — <c>&lt;override&gt;/sidecars</c> when the
    /// override is set (a sibling of the descriptor files it drops directly under the override), else
    /// <c>&lt;base&gt;/sidecars</c> next to <c>&lt;base&gt;/sessions</c>. Pure path resolution — no I/O. The
    /// leaf is a subdirectory (never the top level a <c>*.json</c> descriptor scan enumerates), so a sidecar
    /// never collides with a session descriptor.
    /// </summary>
    public static string Sidecars()
    {
        var overridden = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        return !string.IsNullOrEmpty(overridden)
            ? Path.Combine(overridden, "sidecars")
            : Path.Combine(BaseStateDirectory(), "sidecars");
    }

    /// <summary>
    /// Ensure <paramref name="directory"/> exists, tightening it to <c>0700</c> on POSIX. Returns the same
    /// path. Kept as an explicit-directory helper (not just <see cref="Sessions"/>) so the perms behaviour is
    /// unit-testable against a temp directory with no reliance on the process-global override variable.
    /// </summary>
    internal static string EnsureSessionsAt(string directory) => EnsureOwnerOnlyDirectory(directory);

    /// <summary>
    /// Ensure <paramref name="directory"/> exists, tightening it to <c>0700</c> (owner-only) on POSIX, and
    /// return it. Shared by the sessions registry and the review sidecar — both hold per-user secrets (a
    /// capability key; queued review state), so both live under an owner-only directory.
    /// </summary>
    internal static string EnsureOwnerOnlyDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, OwnerOnlyDirectory);
        }

        return directory;
    }

    private static string BaseStateDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Charter");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "Charter");
        }

        // Linux / other POSIX: honour XDG_STATE_HOME, else ~/.local/state (the XDG default).
        var xdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var stateHome = !string.IsNullOrEmpty(xdgStateHome)
            ? xdgStateHome
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.Combine(stateHome, "charter");
    }
}
