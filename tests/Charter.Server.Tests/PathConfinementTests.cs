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
}
