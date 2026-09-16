using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;

namespace Charter.Cli.Tests;

/// <summary>
/// Shared harness for invoking the REAL built <c>charter</c> binary as a child process (via
/// <c>dotnet exec &lt;Charter.Cli.dll&gt;</c>) and capturing its exit code and streams — the point being
/// to exercise the actual CLI contract, not an in-proc call. Adds an optional working directory so a
/// <c>--project</c> install can be pointed at a temp dir. The DLL path comes from the
/// <c>CharterCliPath</c> assembly-metadata attribute the test csproj stamps at build (config-matched,
/// cross-platform, no hard-coded OS path).
/// </summary>
internal static class CharterCliRunner
{
    // Generous upper bound so a slow/cold CI agent never flakes; a MAX guard, not a fixed sleep.
    private const int TimeoutMs = 60_000;

    // The floor under RunWith's per-child isolation: seven test files build their own `dotnet exec` launcher, and a
    // child inherits this process's environment, so setting it here reaches every one of them (and any future one).
    [ModuleInitializer]
    internal static void IsolateSkillsRootForEveryChildProcess() =>
        Environment.SetEnvironmentVariable(
            SkillsInstaller.OverrideEnvironmentVariable,
            Path.Combine(Path.GetTempPath(), "charter-no-skills-" + Guid.NewGuid().ToString("N")));

    public static (int ExitCode, string StdOut, string StdErr) Run(params string[] args) =>
        RunIn(null, args);

    // Named distinctly (not a Run overload) so a call like Run("skills") can never bind its first string
    // argument to a working-directory parameter — a params-vs-overload trap.
    public static (int ExitCode, string StdOut, string StdErr) RunIn(string? workingDirectory, params string[] args)
        => RunWith(workingDirectory, environment: null, args);

    /// <summary>
    /// <see cref="RunIn"/> with extra environment variables for the child — the seam a test needs to point the
    /// CLI at its own <c>CHARTER_STATE_DIR</c> instead of the developer's real per-user state directory.
    /// </summary>
    public static (int ExitCode, string StdOut, string StdErr) RunWith(
        string? workingDirectory, IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        string cliDll = CharterCliDllPath();

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        // Isolate the skills root BY DEFAULT (Charter #237). Every verb now runs the skill-drift check against
        // ~/.claude/skills, so on a developer machine carrying an older install every child process would print
        // a warning a clean CI runner never sees — and a test asserting an empty stderr would pass in CI and fail
        // at a desk. A path that does not exist reads as "no skills installed", so nothing is created. A test
        // that needs a particular skills root passes CHARTER_SKILLS_DIR itself, which the loop below applies.
        startInfo.Environment["CHARTER_SKILLS_DIR"] =
            Path.Combine(Path.GetTempPath(), "charter-no-skills-" + Guid.NewGuid().ToString("N"));

        if (environment is not null)
        {
            foreach (var entry in environment)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(cliDll);
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new XunitException("Failed to start the charter CLI process.");

        // Read both streams concurrently BEFORE waiting, so a child filling one pipe buffer cannot deadlock.
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

        // Now the process has exited, this parameterless wait guarantees the async readers observed EOF.
        process.WaitForExit();

        return (process.ExitCode, stdOutTask.GetAwaiter().GetResult(), stdErrTask.GetAwaiter().GetResult());
    }

    public static string NewTempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "charter-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDeleteDirectory(string dir)
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

    private static string CharterCliDllPath()
    {
        AssemblyMetadataAttribute? metadata = System.Linq.Enumerable.FirstOrDefault(
            typeof(CharterCliRunner).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
            attribute => attribute.Key == "CharterCliPath");

        string? path = metadata?.Value;
        Assert.False(string.IsNullOrEmpty(path), "The build did not set the CharterCliPath assembly metadata.");
        Assert.True(File.Exists(path), $"Built Charter.Cli.dll not found at '{path}'.");
        return path!;
    }
}
