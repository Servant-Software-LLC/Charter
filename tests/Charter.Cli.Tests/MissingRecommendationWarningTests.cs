using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #142 — <c>recommended</c> is optional in schema and load-bearing unattended, and nothing noticed
/// when an authoring agent omitted it.
///
/// <para>
/// Eleven <c>:::question</c> blocks were authored across two real plans without a lean. Every one validated
/// and rendered; the omission surfaced only when a human noticed the absent <i>(Recommended)</i> tags in the
/// review panel and asked whether the feature had been lost. It had not — it had simply never been used, and
/// the pipeline was entirely content with that. An omitted optional field produces valid output, which is the
/// worst available combination of silent and wrong.
/// </para>
/// <para>
/// The fix reuses <c>QuestionResolution.FindQuestionsMissingRecommendation</c> and emits a NON-FATAL stderr
/// warning from the three verbs that already run the version-marker and duplicate-id lints. These pin exactly
/// that: the warning appears, names the offending id, never changes an exit code, stays silent for a plan that
/// carries leans, and — the load-bearing case — stays silent for a DELIBERATE <c>"recommended": null</c>.
/// </para>
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","MissingRecommendationWarning")].
/// </summary>
[Trait("Category", "MissingRecommendationWarning")]
public class MissingRecommendationWarningTests
{
    private const int TimeoutMs = 60_000;

    // An open, human-targeted select question with no `recommended` key at all.
    private const string NoLeanPlan =
        "---\ncharter-format-version: 1\n---\n\n" +
        "# No Lean Plan\n\nAn overview paragraph.\n\n" +
        ":::question\n" +
        "{\"id\":\"q-store\",\"title\":\"Which datastore?\",\"mode\":\"single\"," +
        "\"options\":[\"Postgres\",\"DynamoDB\"],\"target\":\"human\"}\n" +
        ":::\n";

    // The same question carrying the agent's lean — the positive control.
    private const string LeanPlan =
        "---\ncharter-format-version: 1\n---\n\n" +
        "# Lean Plan\n\nAn overview paragraph.\n\n" +
        ":::question\n" +
        "{\"id\":\"q-store\",\"title\":\"Which datastore?\",\"mode\":\"single\"," +
        "\"options\":[\"Postgres\",\"DynamoDB\"],\"recommended\":\"Postgres\",\"target\":\"human\"}\n" +
        ":::\n";

    // A considered abstention. Parses identically to NoLeanPlan — the distinction lives ONLY in the source
    // bytes, and it is the whole reason the opt-out exists.
    private const string DeclinedLeanPlan =
        "---\ncharter-format-version: 1\n---\n\n" +
        "# Declined Lean Plan\n\nAn overview paragraph.\n\n" +
        ":::question\n" +
        "{\"id\":\"q-store\",\"title\":\"Which datastore?\",\"mode\":\"single\"," +
        "\"options\":[\"Postgres\",\"DynamoDB\"],\"recommended\":null,\"target\":\"human\"}\n" +
        ":::\n";

    [Fact]
    public void Render_QuestionWithNoRecommendation_WarnsOnStderr_ExitCodeUnchanged()
    {
        string workDir = NewTempDirectory();
        try
        {
            string plan = WritePlan(workDir, NoLeanPlan);
            string outputPath = Path.Combine(workDir, "out.html");

            var result = RunCharter("render", plan, "-o", outputPath);

            // Non-fatal: some forks genuinely are 50/50, so this can never be an error.
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Rendered", result.StdOut);
            Assert.True(File.Exists(outputPath));

            Assert.Contains("charter render: warning:", result.StdErr);
            Assert.Contains("no `recommended`", result.StdErr);
            Assert.Contains("q-store", result.StdErr);
            // The message must teach the opt-out, or the only way to silence it is to invent a lean.
            Assert.Contains("\"recommended\": null", result.StdErr);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Handoff_QuestionWithNoRecommendation_WarnsOnStderr_ExitCodeUnchanged()
    {
        string workDir = NewTempDirectory();
        try
        {
            string plan = WritePlan(workDir, NoLeanPlan);
            string outputPath = Path.Combine(workDir, "plan.md");

            var result = RunCharter("handoff", plan, "-o", outputPath);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(outputPath));

            Assert.Contains("charter handoff: warning:", result.StdErr);
            Assert.Contains("no `recommended`", result.StdErr);
            Assert.Contains("q-store", result.StdErr);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Render_QuestionCarryingALean_IsSilent()
    {
        string workDir = NewTempDirectory();
        try
        {
            string plan = WritePlan(workDir, LeanPlan);
            var result = RunCharter("render", plan, "-o", Path.Combine(workDir, "out.html"));

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("no `recommended`", result.StdErr);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// The distinction the whole feature turns on. An explicit <c>null</c> says "I considered a lean and
    /// declined"; an absent key is indistinguishable from "I never knew the field existed". Both parse to a
    /// null <c>Recommended</c>, so only a lint reading the raw JSON can tell them apart — and if it could
    /// not, an author who genuinely had no lean would be nagged forever with no way to say so.
    /// </summary>
    [Fact]
    public void Render_ExplicitNullRecommendation_IsADeliberateAbstention_AndIsSilent()
    {
        string workDir = NewTempDirectory();
        try
        {
            string plan = WritePlan(workDir, DeclinedLeanPlan);
            var result = RunCharter("render", plan, "-o", Path.Combine(workDir, "out.html"));

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("no `recommended`", result.StdErr);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// Charter #142's third item: the unattended escalation must be triage-able WITHOUT opening the plan.
    /// A `headless` run over an open human question reports whether that question carries a lean, so the
    /// difference between "clear this in ten seconds" and "reconstruct the decision from scratch" is visible
    /// on the escalation line itself.
    /// </summary>
    [Fact]
    public void Headless_ReportsWhetherEachOpenQuestionCarriesALean()
    {
        string workDir = NewTempDirectory();
        try
        {
            string bare = WritePlan(workDir, NoLeanPlan);
            var withoutLean = RunCharter("headless", bare, "--out-dir", workDir);

            Assert.Equal(2, withoutLean.ExitCode);      // a human must decide
            Assert.Contains("unanswered question 'q-store'", withoutLean.StdErr);
            Assert.Contains("[no recommendation]", withoutLean.StdErr);

            string leanDir = NewTempDirectory();
            try
            {
                string leaning = WritePlan(leanDir, LeanPlan);
                var withLean = RunCharter("headless", leaning, "--out-dir", leanDir);

                Assert.Equal(2, withLean.ExitCode);      // still open — but now actionable
                Assert.Contains("[agent recommends: Postgres]", withLean.StdErr);

                // ...and the record carries it too, so a machine triage never has to parse stderr.
                string record = Directory.GetFiles(leanDir, "*.headless.json")[0];
                Assert.Contains("\"recommended\": \"Postgres\"", File.ReadAllText(record));
            }
            finally
            {
                TryDeleteDirectory(leanDir);
            }
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
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
            typeof(MissingRecommendationWarningTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
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
        string dir = Path.Combine(Path.GetTempPath(), "charter-lean-" + Guid.NewGuid().ToString("N"));
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
