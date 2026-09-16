using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #245 — a wrong-typed optional <c>:::question</c> field was dropped with exit 0 and an empty stderr.
///
/// <para>
/// Found honestly: a generator emitted <c>"rationale": ["…"]</c> because of a stray trailing comma, and nothing
/// said so. The question rendered as a normal answerable form with no reasoning, and the author noticed only by
/// re-reading the file. Every neighbouring case already behaved: required fields refuse to render, a missing
/// <c>recommended</c> warns. This was the one field that failed quietly.
/// </para>
/// <para>
/// These pin the fix at the process boundary, where the reporter met it: <c>render</c> and <c>handoff</c> warn
/// and keep exit 0, the positive control and the documented <c>null</c> opt-out stay silent, and
/// <c>headless</c> records it as a note that — the load-bearing case — NEVER escalates a run on its own.
/// </para>
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","WrongTypedFieldWarning")].
/// </summary>
[Trait("Category", "WrongTypedFieldWarning")]
public class WrongTypedFieldWarningTests
{
    private const int TimeoutMs = 60_000;

    private static string Plan(string rationale, string answer = "")
        => "---\ncharter-format-version: 1\n---\n\n"
            + "# Plan\n\n"
            + ":::question\n"
            + "{\"id\":\"q-store\",\"title\":\"Which datastore?\",\"mode\":\"single\","
            + "\"options\":[\"Postgres\",\"SQLite\"],\"recommended\":\"Postgres\","
            + "\"rationale\":" + rationale + answer + ",\"target\":\"human\"}\n"
            + ":::\n";

    [Fact]
    public void Render_WrongTypedRationale_WarnsOnStderr_ExitCodeUnchanged()
    {
        string workDir = NewTempDirectory();
        try
        {
            string plan = WritePlan(workDir, Plan("[\"the reasoning\"]"));
            string outputPath = Path.Combine(workDir, "out.html");

            var result = RunCharter("render", plan, "-o", outputPath);

            // Warned, never refused: an optional field of the wrong type must not block a review.
            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(outputPath));

            Assert.Contains(
                "charter render: warning: question 'q-store' (line 7): `rationale` must be a string, got array",
                result.StdErr);
            Assert.Contains("its reasoning will not appear in the review or the handoff", result.StdErr);

            // ...and the page really does lack it, which is why the warning has to exist at all.
            Assert.DoesNotContain("the reasoning", File.ReadAllText(outputPath));
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Handoff_WrongTypedRationale_WarnsOnStderr()
    {
        string workDir = NewTempDirectory();
        try
        {
            string plan = WritePlan(workDir, Plan("42"));

            var result = RunCharter("handoff", plan, "-o", Path.Combine(workDir, "out.md"));

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("charter handoff: warning: question 'q-store'", result.StdErr);
            Assert.Contains("`rationale` must be a string, got number", result.StdErr);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Render_StringRationale_IsSilent()
    {
        string workDir = NewTempDirectory();
        try
        {
            string plan = WritePlan(workDir, Plan("\"a plain string\""));

            var result = RunCharter("render", plan, "-o", Path.Combine(workDir, "out.html"));

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("must be a string", result.StdErr);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Render_NullRationale_IsSilent()
    {
        // JSON null is absent, not wrong-typed — and for `recommended` it is the documented deliberate opt-out.
        string workDir = NewTempDirectory();
        try
        {
            string plan = WritePlan(workDir, Plan("null"));

            var result = RunCharter("render", plan, "-o", Path.Combine(workDir, "out.html"));

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("must be a string", result.StdErr);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Headless_RecordsTheDroppedField_AsANoteThatNeverEscalates()
    {
        string workDir = NewTempDirectory();
        try
        {
            // ANSWERED, so nothing else in the plan asks for a human. Any escalation here could only come from the
            // new note — which is exactly what must not happen: the question can still be answered, it has only
            // lost its reasoning.
            string plan = WritePlan(workDir, Plan("[\"the reasoning\"]", answer: ",\"answer\":[\"Postgres\"]"));

            var result = RunCharter("headless", plan, "--out-dir", workDir);

            Assert.Equal(0, result.ExitCode);

            // The RECORD is the channel, not stderr. `headless` prints to stderr only what needs a human — every
            // warning kind, `missing-recommendation` included, lives in the record alone — and the issue's
            // complaint was precisely that the forensic record said nothing.
            using var record = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(workDir, "*.headless.json")[0]));
            Assert.False(record.RootElement.GetProperty("needsHuman").GetBoolean());

            JsonElement? note = null;
            foreach (var candidate in record.RootElement.GetProperty("notes").EnumerateArray())
            {
                if (candidate.GetProperty("kind").GetString() == "wrong-typed-field")
                {
                    note = candidate;
                }
            }

            Assert.True(note.HasValue, "the headless record carries no wrong-typed-field note");
            Assert.Equal(7, note!.Value.GetProperty("sourceLine").GetInt32());
            Assert.Contains("q-store", note.Value.GetProperty("message").GetString(), StringComparison.Ordinal);
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
            typeof(WrongTypedFieldWarningTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
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
        string dir = Path.Combine(Path.GetTempPath(), "charter-wrongtype-" + Guid.NewGuid().ToString("N"));
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
