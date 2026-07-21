using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;

namespace Charter.Cli.Tests;

/// <summary>
/// Behavioral tests that invoke the REAL built <c>charter</c> binary as a child process and assert on its
/// exit code and stderr — the point being to exercise the actual CLI contract, not an in-proc call. They
/// pin the two hardening behaviors from batch C8: (a) an unknown verb exits NON-ZERO with a clean
/// <c>unknown command</c> message instead of silently falling through to the help banner + exit 0, and
/// (b) an I/O failure (e.g. <c>render -o &lt;an existing directory&gt;</c>) becomes one clean
/// <c>charter render: …</c> line + exit 1 instead of a raw unhandled stack trace. The no-arg help path and
/// <c>--version</c> happy paths are guarded so the hardening did not regress them.
/// </summary>
[Trait("Category", "Cli")]
public class CliProcessTests
{
    // Generous upper bound so a slow/cold CI agent never flakes; it is a MAX guard, not a fixed sleep — every
    // verb under test exits promptly, so the wait returns as soon as the process is done.
    private const int TimeoutMs = 60_000;

    [Fact]
    public void UnknownVerb_ExitsNonZero_WithUnknownCommandOnStderr()
    {
        var result = RunCharter("bogus-verb");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("unknown command", result.StdErr);
    }

    [Fact]
    public void NoArgs_ExitsZero_ShowsHelpBanner()
    {
        var result = RunCharter();

        // The genuine no-argument help path is unchanged: exit 0 and the banner's command list on stdout.
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("render", result.StdOut);
    }

    [Fact]
    public void Render_ToExistingDirectoryOutput_Exits1_WithCleanErrorAndNoStackTrace()
    {
        string workDir = NewTempDirectory();
        try
        {
            string inputPlan = Path.Combine(workDir, "plan.mdx");
            File.WriteAllText(inputPlan, "# A Valid Charter Plan\n\nSome prose the renderer will happily render.\n");

            // -o points at an EXISTING directory, so the write inside `render` fails with an I/O / access
            // exception. Pre-hardening this dumped a raw unhandled stack trace; now it must be one clean line.
            var result = RunCharter("render", inputPlan, "-o", workDir);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("charter render:", result.StdErr);

            string combined = result.StdOut + "\n" + result.StdErr;
            Assert.DoesNotContain("Unhandled exception", combined);
            Assert.DoesNotContain("   at ", combined); // no stack-trace frame leaked to the user
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Render_MissingInput_Exits1_WithCleanError()
    {
        string missingInput = Path.Combine(Path.GetTempPath(), "charter-missing-" + Guid.NewGuid().ToString("N") + ".mdx");
        string outputPath = Path.Combine(Path.GetTempPath(), "charter-out-" + Guid.NewGuid().ToString("N") + ".html");

        var result = RunCharter("render", missingInput, "-o", outputPath);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("charter render:", result.StdErr);

        string combined = result.StdOut + "\n" + result.StdErr;
        Assert.DoesNotContain("Unhandled exception", combined);
        Assert.DoesNotContain("   at ", combined);
    }

    [Fact]
    public void Version_PrintsCharterVersion_ExitsZero()
    {
        var result = RunCharter("--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("charter ", result.StdOut);
    }

    /// <summary>
    /// Runs the built CLI as a child process via <c>dotnet exec &lt;Charter.Cli.dll&gt;</c> and returns its exit
    /// code and captured streams. The DLL path is the Charter.Cli build output resolved by MSBuild at compile
    /// time (see the csproj <c>CaptureCharterCliPath</c> target) — its own bin, config-matched, next to its
    /// runtimeconfig.json — so this locates the artifact robustly and cross-platform with no hard-coded OS path.
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) RunCharter(params string[] args)
    {
        string cliDll = CharterCliDllPath();

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(cliDll);
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new XunitException("Failed to start the charter CLI process.");

        // Read both streams concurrently BEFORE waiting, so a child that fills one pipe buffer cannot deadlock.
        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Best-effort teardown; the assertion below reports the real failure.
            }

            throw new XunitException($"charter {string.Join(' ', args)} did not exit within {TimeoutMs} ms.");
        }

        // Now that the process has exited, this second (parameterless) wait guarantees the async stream readers
        // have observed EOF before we read their results.
        process.WaitForExit();

        return (process.ExitCode, stdOutTask.GetAwaiter().GetResult(), stdErrTask.GetAwaiter().GetResult());
    }

    private static string CharterCliDllPath()
    {
        AssemblyMetadataAttribute? metadata = System.Linq.Enumerable.FirstOrDefault(
            typeof(CliProcessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
            attribute => attribute.Key == "CharterCliPath");

        string? path = metadata?.Value;
        Assert.False(string.IsNullOrEmpty(path), "The build did not set the CharterCliPath assembly metadata.");
        Assert.True(File.Exists(path), $"Built Charter.Cli.dll not found at '{path}'.");
        return path!;
    }

    private static string NewTempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "charter-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDirectory(string dir)
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
            // Best-effort cleanup of a temp directory; a leftover temp dir must not fail the test.
        }
    }
}
