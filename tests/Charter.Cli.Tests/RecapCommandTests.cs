using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// CLI-contract tests for <c>charter recap</c> (Charter #1), driven against a REAL throwaway git repository —
/// the git reading is half the verb, and a fixture-diff test cannot show that the range, the rename detection
/// or the commit lookup are wired correctly.
/// <para>
/// The invariant behind the failure cases: a recap that writes nothing must SAY why. A verb that exits 0 having
/// silently produced an empty document is the same defect class as the drain that reported success without
/// delivering (#117) — every signal says it worked.
/// </para>
/// </summary>
[Trait("Category", "Recap")]
public class RecapCommandTests : IDisposable
{
    private readonly string _repo;

    public RecapCommandTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "charter-recap-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_repo);

        Git("init", "-q", ".");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        Git("config", "commit.gpgsign", "false");

        File.WriteAllText(Path.Combine(_repo, "reader.cs"), "// one\n// two\n");
        Git("add", "-A");
        Git("commit", "-qm", "base");

        File.WriteAllText(Path.Combine(_repo, "reader.cs"), "// one\n// changed\n// three\n");
        Git("add", "-A");
        Git("commit", "-qm", "fix: change the reader");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            // git marks everything under .git/objects read-only, and on Windows Directory.Delete refuses a
            // read-only file — so the attribute has to come off first or every test in this class fails in
            // teardown having actually passed.
            foreach (string file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_repo, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green test run over.
        }
    }

    [Fact]
    public void ARange_WritesASeedNamingTheFilesAndTheCommits()
    {
        string output = Path.Combine(_repo, "recap.charter.md");

        var result = CharterCliRunner.RunIn(_repo, "recap", "HEAD~1..HEAD", "-o", output);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Recapped", result.StdOut);

        string seed = File.ReadAllText(output);
        Assert.Contains("charter-format-version", seed);
        Assert.Contains("reader.cs", seed);
        Assert.Contains("fix: change the reader", seed);       // the commit table came from git log
        Assert.Contains(":::diff", seed);
    }

    /// <summary>
    /// The seed is the MECHANICAL half, and stdout's single success line would let that go unnoticed. The
    /// enrichment prompt therefore rides stderr on every run — a raw seed handed straight to a reviewer is the
    /// failure this prevents.
    /// </summary>
    [Fact]
    public void EveryRun_TellsTheAgentWhatIsStillMissing()
    {
        string output = Path.Combine(_repo, "recap.charter.md");

        var result = CharterCliRunner.RunIn(_repo, "recap", "HEAD~1..HEAD", "-o", output);

        Assert.Contains("MECHANICAL half", result.StdErr);
        Assert.Contains(":::diagram", result.StdErr);
        Assert.Contains(":::question", result.StdErr);
        Assert.DoesNotContain("MECHANICAL half", result.StdOut);   // stdout stays the one machine-readable line
    }

    /// <summary>
    /// git emits UTF-8, but a redirected stream is decoded with the CONSOLE's code page unless told otherwise —
    /// an OEM one on Windows. Unfixed, an em-dash in a commit subject or any non-ASCII source line reaches the
    /// seed as mojibake and is then WRITTEN to the .charter.md that way. Found by recapping Charter itself,
    /// whose own commit subjects use em-dashes, which is why the fixture uses the same characters.
    /// </summary>
    [Fact]
    public void NonAsciiContent_SurvivesTheGitRead_RatherThanArrivingAsMojibake()
    {
        File.WriteAllText(Path.Combine(_repo, "unicode.txt"), "café — naïve\n");
        Git("add", "-A");
        Git("commit", "-qm", "feat: händle — café naïve");

        string output = Path.Combine(_repo, "unicode.charter.md");
        Assert.Equal(0, CharterCliRunner.RunIn(_repo, "recap", "HEAD~1..HEAD", "-o", output).ExitCode);

        string seed = File.ReadAllText(output);
        Assert.Contains("händle — café naïve", seed);   // the commit subject
        Assert.Contains("café — naïve", seed);               // the diff content
        Assert.DoesNotContain("Γ", seed);                              // the mojibake this produced
    }

    [Fact]
    public void AnUnknownRevision_FailsWithGitsOwnMessage_AndWritesNothing()
    {
        string output = Path.Combine(_repo, "never.charter.md");

        var result = CharterCliRunner.RunIn(_repo, "recap", "no-such-ref..HEAD", "-o", output);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("charter recap:", result.StdErr);
        Assert.Contains("no-such-ref", result.StdErr);
        Assert.False(File.Exists(output));
    }

    /// <summary>An empty range is not an error in git, but writing a recap of nothing would hand the reviewer a
    /// document asserting a change that does not exist.</summary>
    [Fact]
    public void ARangeWithNoChanges_FailsWithTheReason_RatherThanWritingAnEmptyRecap()
    {
        string output = Path.Combine(_repo, "empty.charter.md");

        var result = CharterCliRunner.RunIn(_repo, "recap", "HEAD..HEAD", "-o", output);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("no changes to recap", result.StdErr);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ADirectoryThatIsNotARepository_FailsCleanly()
    {
        string outside = Path.Combine(Path.GetTempPath(), "charter-norepo-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(outside);
        try
        {
            var result = CharterCliRunner.RunIn(
                outside, "recap", "HEAD~1..HEAD", "-o", Path.Combine(outside, "x.charter.md"));

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("charter recap:", result.StdErr);
            Assert.DoesNotContain("Unhandled exception", result.StdErr);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>The seed must be reviewable by the verbs that follow it — that is the entire point of emitting a
    /// <c>.charter.md</c> rather than an HTML report.</summary>
    [Fact]
    public void TheSeed_RendersThroughTheOrdinaryRenderVerb()
    {
        string seedPath = Path.Combine(_repo, "recap.charter.md");
        string htmlPath = Path.Combine(_repo, "recap.html");

        Assert.Equal(0, CharterCliRunner.RunIn(_repo, "recap", "HEAD~1..HEAD", "-o", seedPath).ExitCode);

        var render = CharterCliRunner.RunIn(_repo, "render", seedPath, "-o", htmlPath);

        Assert.Equal(0, render.ExitCode);
        string html = File.ReadAllText(htmlPath);
        Assert.Contains("class=\"diff\"", html);
        Assert.Contains("diff-line", html);
    }

    private void Git(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git could not be started for the test fixture.");
        process.WaitForExit(30_000);
    }
}
