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
    /// Ensure <paramref name="directory"/> exists, tightening it to <c>0700</c> on POSIX. Returns the same
    /// path. Kept as an explicit-directory helper (not just <see cref="Sessions"/>) so the perms behaviour is
    /// unit-testable against a temp directory with no reliance on the process-global override variable.
    /// </summary>
    internal static string EnsureSessionsAt(string directory)
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
