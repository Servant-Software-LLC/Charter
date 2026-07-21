using System.IO;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Path-confinement tests — the AUTHORITATIVE confinement proof. These call
/// <see cref="PathConfinement.Resolve"/> DIRECTLY (not over HTTP), so they are transport-independent: an
/// in-root path resolves under the root, while a <c>..</c> traversal and an absolute path outside the root
/// each resolve to <c>null</c>. An HTTP-level traversal test (see the loopback integration test) cannot
/// stand in for this — the transport strips <c>..</c> before it ever reaches the server, so this unit test
/// is the load-bearing evidence that Charter's own code refuses to escape the served root.
/// </summary>
[Trait("Category", "ReviewServer")]
public class PathConfinementTests
{
    private static string NewRoot()
        => Path.Combine(Path.GetTempPath(), "charter-confine-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Resolve_InRootRelativePath_ReturnsPathUnderRoot()
    {
        var root = NewRoot();

        var resolved = PathConfinement.Resolve(root, "assets/app.js");

        Assert.NotNull(resolved);
        var fullRoot = Path.GetFullPath(root);
        var fullResolved = Path.GetFullPath(resolved!);
        Assert.StartsWith(fullRoot, fullResolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_DotDotTraversal_ReturnsNull()
    {
        var root = NewRoot();

        var resolved = PathConfinement.Resolve(root, "../../etc/passwd");

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_AbsolutePathOutsideRoot_ReturnsNull()
    {
        var root = NewRoot();
        var absoluteOutside = OperatingSystem.IsWindows()
            ? @"C:\Windows\System32\drivers\etc\hosts"
            : "/etc/passwd";

        var resolved = PathConfinement.Resolve(root, absoluteOutside);

        Assert.Null(resolved);
    }

    /// <summary>
    /// A directory symlink/junction created INSIDE the root but pointing OUTSIDE it must not resolve to an
    /// in-root path: <see cref="Path.GetFullPath(string)"/> is lexical and would leave the link textually
    /// contained, so confinement must resolve the reparse point, see its target escapes the root, and return
    /// null — otherwise a link inside root reads outside root. Guarded to platforms that permit link creation.
    /// </summary>
    [Fact]
    public void Resolve_SymlinkInsideRootPointingOutside_ReturnsNull()
    {
        var root = NewRoot();
        var outside = NewRoot() + "-outside";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "out-of-root secret");

            var linkPath = Path.Combine(root, "escape");
            if (!TryCreateDirectoryLink(linkPath, outside))
            {
                return; // no reparse point could be created on this platform/run — skip cleanly
            }

            // The link is textually inside root; a lexical-only check accepts escape/secret.txt. The
            // reparse-point resolution must refuse it (the target escapes the root).
            var resolved = PathConfinement.Resolve(root, "escape/secret.txt");

            Assert.Null(resolved);
        }
        finally
        {
            // Remove the reparse point itself (non-recursive, so the link is unlinked without following it into
            // the outside target) before deleting the trees — a recursive delete can choke on a junction.
            TryUnlinkReparsePoint(Path.Combine(root, "escape"));
            TryDeleteTree(root);
            TryDeleteTree(outside);
        }
    }

    /// <summary>
    /// The sibling-string-prefix boundary the primitive's own suite omits: a request that escapes to a SIBLING
    /// directory sharing the root's name as a raw string prefix (<c>plan</c> vs <c>plan-evil</c>) must return
    /// null — containment is separator-safe, not a bare <c>StartsWith</c>.
    /// </summary>
    [Fact]
    public void Resolve_SiblingSharingRootNameAsStringPrefix_ReturnsNull()
    {
        var root = Path.Combine(Path.GetTempPath(), "charter-confine-" + Guid.NewGuid().ToString("N"), "plan");

        var resolved = PathConfinement.Resolve(root, "../plan-evil/secret.txt");

        Assert.Null(resolved);
    }

    /// <summary>
    /// Create a directory reparse point at <paramref name="link"/> pointing to <paramref name="target"/>,
    /// returning false when the platform/run permits none. Prefers a symlink (works on Linux/macOS and on
    /// Windows with Developer Mode); on Windows without that privilege it falls back to a JUNCTION (also a
    /// reparse point, but creatable without elevation) via <c>cmd /c mklink /J</c> — so the confinement fix is
    /// exercised on every OS in the matrix rather than silently skipped.
    /// </summary>
    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Symlink not permitted here; on Windows a junction is an unprivileged reparse-point alternative.
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Unlink a directory reparse point (symlink/junction) itself, without following it.</summary>
    private static void TryUnlinkReparsePoint(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leaked reparse point in temp is harmless; not worth failing the test over.
        }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leaked temp tree is harmless; a held handle during a slow delete is not worth failing the test.
        }
    }
}
