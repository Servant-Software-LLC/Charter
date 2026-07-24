using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Unit tests for the OS-state-dir session registry (<see cref="SessionRegistry"/> + <see cref="StateDirectory"/>)
/// that <c>charter poll</c> discovers a running <c>charter review</c> server through. Every test operates in its
/// OWN temp directory passed explicitly to the registry, so they are fully isolated and parallel-safe with no
/// reliance on the process-global <c>CHARTER_STATE_DIR</c> override. POSIX-permission tests skip on Windows,
/// where per-user <c>%LOCALAPPDATA%</c> ACLs are the equivalent guard.
/// </summary>
[Trait("Category", "SessionRegistry")]
public class SessionRegistryTests
{
    [Fact]
    public void WriteThenRead_RoundTripsTheDescriptor()
    {
        var dir = NewTempDir();
        try
        {
            var planPath = Path.Combine(dir, "plan.mdx");
            var descriptor = MakeDescriptor(planPath);

            var written = SessionRegistry.Write(dir, descriptor);
            Assert.Equal(SessionRegistry.PathForPlan(dir, planPath), written);

            var read = SessionRegistry.Read(written);
            Assert.NotNull(read);
            Assert.Equal(descriptor.Schema, read!.Schema);
            Assert.Equal(descriptor.Address, read.Address);
            Assert.Equal(descriptor.Key, read.Key);
            Assert.Equal(descriptor.SourcePath, read.SourcePath);
            Assert.Equal(descriptor.SourceFile, read.SourceFile);
            Assert.Equal(descriptor.Pid, read.Pid);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void PathForPlan_IsStable_PerCanonicalPath()
    {
        var dir = NewTempDir();
        try
        {
            var planPath = Path.Combine(dir, "plan.mdx");

            var first = SessionRegistry.PathForPlan(dir, planPath);
            var second = SessionRegistry.PathForPlan(dir, planPath);
            Assert.Equal(first, second);

            // A non-canonical spelling of the SAME file (a redundant './') hashes to the same filename because
            // PathForPlan canonicalizes with Path.GetFullPath first.
            var viaRelative = Path.Combine(dir, ".", "plan.mdx");
            Assert.Equal(first, SessionRegistry.PathForPlan(dir, viaRelative));

            // The filename is a 64-hex SHA-256 digest with a .json extension.
            Assert.Matches("^[0-9a-f]{64}\\.json$", Path.GetFileName(first));

            // A different plan hashes to a different file.
            var other = SessionRegistry.PathForPlan(dir, Path.Combine(dir, "other.mdx"));
            Assert.NotEqual(first, other);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void Enumerate_ReturnsEveryReadableDescriptor_SkippingCorruptFiles()
    {
        var dir = NewTempDir();
        try
        {
            var planA = Path.Combine(dir, "a.mdx");
            var planB = Path.Combine(dir, "b.mdx");
            var pathA = SessionRegistry.Write(dir, MakeDescriptor(planA, key: "aaaa"));
            var pathB = SessionRegistry.Write(dir, MakeDescriptor(planB, key: "bbbb"));

            // A garbage .json file in the same directory must be skipped, not counted or thrown on.
            File.WriteAllText(Path.Combine(dir, "garbage.json"), "{ this is not a descriptor");

            var entries = SessionRegistry.Enumerate(dir);
            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, e => e.Path == pathA && e.Descriptor.Key == "aaaa");
            Assert.Contains(entries, e => e.Path == pathB && e.Descriptor.Key == "bbbb");
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void Enumerate_MissingDirectory_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-registry-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(SessionRegistry.Enumerate(dir));
    }

    [Fact]
    public void Delete_RemovesTheDescriptor()
    {
        var dir = NewTempDir();
        try
        {
            var planPath = Path.Combine(dir, "plan.mdx");
            var written = SessionRegistry.Write(dir, MakeDescriptor(planPath));
            Assert.True(File.Exists(written));

            SessionRegistry.Delete(written);
            Assert.False(File.Exists(written));
            Assert.Null(SessionRegistry.Read(written));

            // Delete is idempotent / best-effort: a second delete of an absent file does not throw.
            SessionRegistry.Delete(written);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Theory]
    [InlineData("{ not valid json")]
    [InlineData("null")]
    [InlineData("{}")] // valid JSON, but missing every required field
    [InlineData("{\"schema\":1,\"address\":\"http://127.0.0.1:1/\",\"key\":\"\",\"sourcePath\":\"/p\"}")] // empty key
    public void Read_CorruptOrIncompleteDescriptor_ReturnsNull_NeverThrows(string content)
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "descriptor.json");
            File.WriteAllText(path, content);
            Assert.Null(SessionRegistry.Read(path));
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void Read_MissingFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), "charter-descriptor-missing-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Null(SessionRegistry.Read(path));
    }

    [Fact]
    public void Write_SetsDescriptorFileTo0600_OnPosix()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // POSIX-only: on Windows the per-user %LOCALAPPDATA% ACL is the guard.
        }

        var dir = NewTempDir();
        try
        {
            var written = SessionRegistry.Write(dir, MakeDescriptor(Path.Combine(dir, "plan.mdx")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(written));
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void EnsureSessionsAt_CreatesDirectory0700_OnPosix()
    {
        var dir = Path.Combine(NewTempDir(), "sessions");
        try
        {
            var created = StateDirectory.EnsureSessionsAt(dir);
            Assert.True(Directory.Exists(created));

            if (OperatingSystem.IsWindows())
            {
                return; // POSIX-only permission assertion.
            }

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(created));
        }
        finally
        {
            TryDeleteDir(Directory.GetParent(dir)!.FullName);
        }
    }

    [Fact]
    public void WrittenDescriptor_UsesCamelCaseWireShape()
    {
        var dir = NewTempDir();
        try
        {
            var planPath = Path.Combine(dir, "plan.mdx");
            var written = SessionRegistry.Write(dir, MakeDescriptor(planPath));

            using var doc = JsonDocument.Parse(File.ReadAllText(written));
            var root = doc.RootElement;
            // camelCase keys, matching the approved descriptor contract.
            foreach (var key in new[] { "schema", "address", "key", "sourcePath", "sourceFile", "pid", "createdAt" })
            {
                Assert.True(root.TryGetProperty(key, out _), $"descriptor JSON should carry '{key}'");
            }
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    private static SessionDescriptor MakeDescriptor(
        string planPath, string key = "0123abcd0123abcd", string address = "http://127.0.0.1:54321/")
        => new(
            Schema: SessionDescriptor.CurrentSchema,
            Address: address,
            Key: key,
            SourcePath: planPath,
            SourceFile: Path.GetFileName(planPath),
            Pid: 4242,
            CreatedAt: DateTimeOffset.UtcNow);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-registry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup of a temp directory.
        }
    }
}
