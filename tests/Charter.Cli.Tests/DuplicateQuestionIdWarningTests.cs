using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #48 / C6 — a duplicate <c>:::question</c> id must be reported at the FRONT of the loop.
///
/// <c>charter-format</c> calls a document-duplicate question id a review-time error, and review STARTS with
/// render — but the only enforcement lived in the answer-drain (<c>DuplicateQuestionIdException</c>, thrown by
/// <c>QuestionResolution</c>). So <c>render</c>, <c>review</c> and <c>handoff</c> passed duplicates through
/// silently at exit 0 and the human answered the whole plan FIRST and only then hit the failure.
///
/// The fix reuses <c>QuestionResolution.FindDuplicateQuestionIds</c> and emits a NON-FATAL stderr warning
/// naming the duplicated id(s) — matching the existing version-marker warning idiom, so exit codes are
/// unchanged for a plan that otherwise renders. These pin exactly that: the warning appears from each verb,
/// names the id, does not change the exit code, and a clean plan stays silent.
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","DuplicateQuestionIdWarning")].
/// </summary>
[Trait("Category", "DuplicateQuestionIdWarning")]
public class DuplicateQuestionIdWarningTests
{
    private const int TimeoutMs = 60_000;

    // Two :::question blocks sharing the id "q-theme" — an answer would resolve into BOTH.
    private const string DuplicateIdPlan =
        "---\ncharter-format-version: 1\n---\n\n" +
        "# Duplicate Id Plan\n\nAn overview paragraph.\n\n" +
        ":::question\n" +
        "{\"id\":\"q-theme\",\"title\":\"First\",\"mode\":\"single\",\"options\":[\"A\",\"B\"],\"target\":\"human\"}\n" +
        ":::\n\n" +
        ":::question\n" +
        "{\"id\":\"q-theme\",\"title\":\"Second\",\"mode\":\"single\",\"options\":[\"A\",\"B\"],\"target\":\"human\"}\n" +
        ":::\n";

    // The same plan with document-unique ids — the negative control.
    private const string UniqueIdPlan =
        "---\ncharter-format-version: 1\n---\n\n" +
        "# Unique Id Plan\n\nAn overview paragraph.\n\n" +
        ":::question\n" +
        "{\"id\":\"q-theme\",\"title\":\"First\",\"mode\":\"single\",\"options\":[\"A\",\"B\"],\"target\":\"human\"}\n" +
        ":::\n\n" +
        ":::question\n" +
        "{\"id\":\"q-layout\",\"title\":\"Second\",\"mode\":\"single\",\"options\":[\"A\",\"B\"],\"target\":\"human\"}\n" +
        ":::\n";

    [Fact]
    public void Render_DuplicateQuestionIds_WarnsOnStderr_ExitCodeUnchanged()
    {
        string workDir = NewTempDirectory();
        try
        {
            string plan = WritePlan(workDir, DuplicateIdPlan);
            string outputPath = Path.Combine(workDir, "out.html");

            var result = RunCharter("render", plan, "-o", outputPath);

            // Non-fatal: the plan still renders, exit 0, and the artifact is actually written.
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Rendered", result.StdOut);
            Assert.True(File.Exists(outputPath));

            // ...but the duplicate is named on stderr, at the FRONT of the loop.
            Assert.Contains("charter render: warning:", result.StdErr);
            Assert.Contains("duplicate :::question id", result.StdErr);
            Assert.Contains("q-theme", result.StdErr);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Handoff_DuplicateQuestionIds_WarnsOnStderr_ExitCodeUnchanged()
    {
        string workDir = NewTempDirectory();
        try
        {
            string plan = WritePlan(workDir, DuplicateIdPlan);
            string outputPath = Path.Combine(workDir, "plan.md");

            var result = RunCharter("handoff", plan, "-o", outputPath);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Handed off", result.StdOut);
            Assert.True(File.Exists(outputPath));

            Assert.Contains("charter handoff: warning:", result.StdErr);
            Assert.Contains("duplicate :::question id", result.StdErr);
            Assert.Contains("q-theme", result.StdErr);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public async Task Review_DuplicateQuestionIds_WarnsOnStderr_BeforeServing()
    {
        string workDir = NewTempDirectory();
        Process? process = null;
        try
        {
            string plan = WritePlan(workDir, DuplicateIdPlan);

            var startInfo = MakeStartInfo("review", plan, "--no-open");
            startInfo.Environment["CHARTER_STATE_DIR"] = Path.Combine(workDir, "state");

            process = Process.Start(startInfo) ?? throw new XunitException("Failed to start charter review.");

            // Drain stdout in the background so the child can never stall on a full pipe while we read stderr.
            _ = process.StandardOutput.ReadToEndAsync();

            // The warning is emitted BEFORE the server starts serving, so it arrives on stderr promptly.
            var warning = await ReadStdErrUntilAsync(process, "duplicate :::question id", TimeSpan.FromSeconds(45));

            Assert.Contains("charter review: warning:", warning);
            Assert.Contains("q-theme", warning);
        }
        finally
        {
            TryKill(process);
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void CleanPlan_NoDuplicateWarning_FromRenderOrHandoff()
    {
        string workDir = NewTempDirectory();
        try
        {
            string plan = WritePlan(workDir, UniqueIdPlan);

            var render = RunCharter("render", plan, "-o", Path.Combine(workDir, "out.html"));
            var handoff = RunCharter("handoff", plan, "-o", Path.Combine(workDir, "plan.md"));

            Assert.Equal(0, render.ExitCode);
            Assert.Equal(0, handoff.ExitCode);

            // A document-unique plan is SILENT — the lint must not cry wolf on every plan with questions.
            Assert.DoesNotContain("duplicate", render.StdErr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("duplicate", handoff.StdErr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// Read the child's stderr line by line until one contains <paramref name="marker"/>, returning everything
    /// read so far. Deterministic (no sleep): it waits on the stream, bounded by <paramref name="timeout"/>.
    /// </summary>
    private static async Task<string> ReadStdErrUntilAsync(Process process, string marker, TimeSpan timeout)
    {
        var seen = new System.Text.StringBuilder();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                string? line = await process.StandardError.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break; // EOF
                }

                seen.AppendLine(line);
                if (line.Contains(marker, StringComparison.Ordinal))
                {
                    return seen.ToString();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fall through to the failure below.
        }

        throw new XunitException(
            $"charter review never wrote a stderr line containing '{marker}'. Saw:\n{seen}");
    }

    private static string WritePlan(string workDir, string content)
    {
        string path = Path.Combine(workDir, "plan.charter.md");
        File.WriteAllText(path, content);
        return path;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCharter(params string[] args)
    {
        using var process = Process.Start(MakeStartInfo(args))
            ?? throw new XunitException("Failed to start the charter CLI process.");

        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeoutMs))
        {
            TryKill(process);
            throw new XunitException($"charter {string.Join(' ', args)} did not exit within {TimeoutMs} ms.");
        }

        process.WaitForExit();
        return (process.ExitCode, stdOutTask.GetAwaiter().GetResult(), stdErrTask.GetAwaiter().GetResult());
    }

    private static ProcessStartInfo MakeStartInfo(params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(CharterCliDllPath());
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    private static string CharterCliDllPath()
    {
        AssemblyMetadataAttribute? metadata = System.Linq.Enumerable.FirstOrDefault(
            typeof(DuplicateQuestionIdWarningTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
            attribute => attribute.Key == "CharterCliPath");

        string? path = metadata?.Value;
        Assert.False(string.IsNullOrEmpty(path), "The build did not set the CharterCliPath assembly metadata.");
        Assert.True(File.Exists(path), $"Built Charter.Cli.dll not found at '{path}'.");
        return path!;
    }

    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best-effort teardown.
        }

        try
        {
            process.WaitForExit(5000);
            process.Dispose();
        }
        catch (Exception)
        {
            // Ignore.
        }
    }

    private static string NewTempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "charter-dupid-" + Guid.NewGuid().ToString("N"));
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
