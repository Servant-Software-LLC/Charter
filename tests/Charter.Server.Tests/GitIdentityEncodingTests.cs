using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// git emits UTF-8, but .NET decodes a REDIRECTED stream with the console's encoding unless told otherwise —
/// an OEM code page on Windows (Charter #122). Unpinned, a reviewer called <c>Müller</c> is attributed as
/// <c>MÃ¼ller</c> on every comment they leave, and a teammate reading the committed log sees the same
/// corruption. Nothing errors; the name is simply wrong, and only the person whose name it is would notice.
/// <para>
/// The sibling defect was found by dogfooding <c>charter recap</c> on this repository, whose commit subjects
/// use em-dashes. This class covers the identity path, where the value is a person rather than a subject line.
/// </para>
/// </summary>
[Trait("Category", "GitIdentityEncoding")]
public class GitIdentityEncodingTests
{
    /// <summary>Names chosen to span the failure modes: a Latin-1 diaeresis (two UTF-8 bytes), an accented
    /// vowel, and a CJK name (three UTF-8 bytes) that no single-byte code page can represent at all.</summary>
    [Theory]
    [InlineData("Sören Müller", "soren@example.com")]
    [InlineData("José Álvarez", "jose@example.com")]
    [InlineData("田中太郎", "tanaka@example.com")]
    public void ANonAsciiReviewerName_SurvivesTheGitRead(string name, string email)
    {
        var repo = NewRepo();
        if (repo is null)
        {
            return;   // no git on this machine; the suite must still be runnable
        }

        try
        {
            Assert.True(RunGit(repo, "config", "user.name", name));
            Assert.True(RunGit(repo, "config", "user.email", email));

            var identity = GitIdentity.Resolve(repo);

            Assert.True(identity.FromGit);
            Assert.Equal(name, identity.Author.Name);
            Assert.Equal(email, identity.Author.Email);

            // The specific corruption this guards: a UTF-8 pair decoded as two single-byte characters.
            Assert.DoesNotContain("Ã", identity.Author.Name, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDir(repo);
        }
    }

    /// <summary>
    /// A non-ASCII <c>user.email</c> is rare but legal, and there the corruption is worse than cosmetic:
    /// <see cref="ReviewLogPaths.FileNameForAuthor"/> derives BOTH halves of the log's file name from the
    /// address, so a misdecoded one writes a person's comments to a different file than their correctly
    /// decoded identity would — and a teammate would never find them.
    /// </summary>
    [Fact]
    public void ANonAsciiEmail_ResolvesToTheSameLogFileGitWouldName()
    {
        var repo = NewRepo();
        if (repo is null)
        {
            return;
        }

        try
        {
            const string email = "sören@example.com";
            Assert.True(RunGit(repo, "config", "user.name", "Sören"));
            Assert.True(RunGit(repo, "config", "user.email", email));

            var identity = GitIdentity.Resolve(repo);

            Assert.Equal(
                ReviewLogPaths.FileNameForAuthor(email),
                ReviewLogPaths.FileNameForAuthor(identity.Author.Email));
        }
        finally
        {
            TryDeleteDir(repo);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static string? NewRepo()
    {
        var directory = Path.Combine(Path.GetTempPath(), "charter-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (RunGit(directory, "init"))
        {
            return directory;
        }

        TryDeleteDir(directory);
        return null;
    }

    /// <summary>
    /// Runs git for the FIXTURE. Its own streams are pinned to UTF-8 for the same reason the code under test
    /// is — otherwise a fixture that fails to write the name correctly would look like a product bug.
    /// </summary>
    private static bool RunGit(string workingDirectory, params string[] arguments)
    {
        try
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = utf8,
                StandardErrorEncoding = utf8,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(15_000) && process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDeleteDir(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green run over.
        }
    }
}
