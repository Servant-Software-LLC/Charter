using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Charter.Server;
using Xunit;
using Xunit.Sdk;

namespace Charter.Cli.Tests;

/// <summary>
/// Process-level tests for <c>charter poll</c> and the <c>charter review</c> session-descriptor lifecycle. They
/// invoke the REAL built binary as a child process (like <see cref="CliProcessTests"/>), isolating each run in
/// its own temp state directory via the <c>CHARTER_STATE_DIR</c> override so nothing touches the developer's
/// real registry and tests never pollute one another. The load-bearing case is the end-to-end loop: a running
/// <c>review</c> server, an answer submitted to it, and <c>poll --url --apply</c> draining the envelope AND
/// writing that answer INLINE into the plan's <c>:::question</c> (the living-document write).
/// </summary>
[Trait("Category", "Cli")]
public class PollCommandTests
{
    private const string SimplePlan = "# Poll Plan\n\nAn overview paragraph for the poll tests.\n";

    private const string QuestionPlan =
        "# Poll Loop Plan\n\nAn overview paragraph.\n\n" +
        ":::question\n" +
        "{\"id\":\"q-theme\",\"title\":\"Which theme should ship?\",\"mode\":\"single\",\"options\":[\"A\",\"B\"],\"target\":\"human\"}\n" +
        ":::\n";

    private static readonly Regex ReadyUrl = new(@"https?://127\.0\.0\.1:\d+/\?key=[0-9a-f]+", RegexOptions.Compiled);

    // ---- No-session + regression --------------------------------------------------------------------------

    [Fact]
    public async Task Poll_NoRunningSession_Exits3_WithCleanStderr_AndSessionNull()
    {
        var stateDir = NewTempDir();
        try
        {
            // An empty registry -> no live session to auto-select.
            var result = await RunCharterAsync(stateDir, "poll");

            Assert.Equal(3, result.ExitCode);
            Assert.Contains("no running review session", result.StdErr);
            AssertSessionNull(result.StdOut);

            var combined = result.StdOut + "\n" + result.StdErr;
            Assert.DoesNotContain("Unhandled exception", combined);
            Assert.DoesNotContain("   at ", combined);
        }
        finally
        {
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task Poll_UnknownPlan_Exits3_SessionNull()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(SimplePlan);
        try
        {
            // A plan with no descriptor registered: selects by canonical path, finds nothing -> no session.
            var result = await RunCharterAsync(stateDir, "poll", planPath);

            Assert.Equal(3, result.ExitCode);
            AssertSessionNull(result.StdOut);
        }
        finally
        {
            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task NoArgs_HelpBanner_ListsPollVerb()
    {
        var result = await RunCharterAsync(stateDir: null);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("poll", result.StdOut);
    }

    [Fact]
    public async Task UnknownVerb_Message_ListsPoll_AndExitsNonZero()
    {
        var result = await RunCharterAsync(stateDir: null, "definitely-not-a-verb");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("poll", result.StdErr);
    }

    // ---- The end-to-end loop (T5 integration) -------------------------------------------------------------

    [Fact]
    public async Task Poll_UrlDrain_EmitsEnvelope_KeyOmitted()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(QuestionPlan);
        Process? review = null;
        try
        {
            (review, var url) = await StartReviewAsync(stateDir, planPath);

            // The human submits an answer to q-theme through the running review server.
            await PostAnswerAsync(url, "q-theme", new[] { "A" });

            // poll --url drains it (bypassing discovery) and emits the single stdout envelope.
            var poll = await RunCharterAsync(stateDir, "poll", "--url", url);
            Assert.Equal(0, poll.ExitCode);

            using var envelope = JsonDocument.Parse(poll.StdOut.Trim());
            var root = envelope.RootElement;
            var session = root.GetProperty("session");
            Assert.Equal(JsonValueKind.Object, session.ValueKind);
            Assert.Equal(Path.GetFileName(planPath), session.GetProperty("sourceFile").GetString());

            var answers = root.GetProperty("answers");
            Assert.Equal(1, answers.GetArrayLength());
            Assert.Equal("q-theme", answers[0].GetProperty("questionId").GetString());

            // The capability key must never appear in the envelope.
            Assert.DoesNotContain(KeyOf(url), poll.StdOut);
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Poll_UrlDrain_Apply_WritesAnswerInlineIntoTheCharterMd()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(QuestionPlan);
        Process? review = null;
        try
        {
            (review, var url) = await StartReviewAsync(stateDir, planPath);

            // Before: the plan's :::question is OPEN (no inline answer yet).
            Assert.Null(ExtractQuestionAnswer(await File.ReadAllTextAsync(planPath), "q-theme"));

            // The human submits an answer to q-theme through the running review server.
            await PostAnswerAsync(url, "q-theme", new[] { "A" });

            // poll --url --apply drains it AND writes the answer INLINE into the plan file (living-document write).
            var poll = await RunCharterAsync(stateDir, "poll", "--url", url, "--apply");
            Assert.Equal(0, poll.ExitCode);

            // The envelope is still emitted (key omitted) — --apply is an additive effect, not a replacement.
            using (var envelope = JsonDocument.Parse(poll.StdOut.Trim()))
            {
                var answers = envelope.RootElement.GetProperty("answers");
                Assert.Equal(1, answers.GetArrayLength());
                Assert.Equal("q-theme", answers[0].GetProperty("questionId").GetString());
                Assert.DoesNotContain(KeyOf(url), poll.StdOut);
            }

            // After: the .charter.md ON DISK now carries the resolved answer inline — parse the :::question body
            // back to JSON and assert the answer array holds the submitted value. Proves the question is RESOLVED.
            var resolved = ExtractQuestionAnswer(await File.ReadAllTextAsync(planPath), "q-theme");
            Assert.NotNull(resolved);
            Assert.Equal(new[] { "A" }, resolved);
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    // ---- Descriptor lifecycle (T6) ------------------------------------------------------------------------

    [Fact]
    public async Task Review_WritesDescriptor_AtExpectedPath_WithCorrectFields()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(SimplePlan);
        Process? review = null;
        try
        {
            (review, var url) = await StartReviewAsync(stateDir, planPath);

            var descriptorPath = SessionRegistry.PathForPlan(stateDir, planPath);
            Assert.True(File.Exists(descriptorPath), "review should register a descriptor at the plan's registry path.");

            var descriptor = SessionRegistry.Read(descriptorPath);
            Assert.NotNull(descriptor);
            Assert.Equal(Path.GetFullPath(planPath), Path.GetFullPath(descriptor!.SourcePath));
            Assert.Equal(KeyOf(url), descriptor.Key);
            Assert.Equal(AuthorityOf(url) + "/", descriptor.Address);
            Assert.Equal(review.Id, descriptor.Pid);
            Assert.Equal(SessionDescriptor.CurrentSchema, descriptor.Schema);
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task KilledReview_LeavesDescriptor_Poll_TreatsStale_ExitsNoSession_AndPrunes()
    {
        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(SimplePlan);
        Process? review = null;
        try
        {
            (review, _) = await StartReviewAsync(stateDir, planPath);
            var descriptorPath = SessionRegistry.PathForPlan(stateDir, planPath);
            Assert.True(File.Exists(descriptorPath));

            // Kill (not clean): the finally-delete never runs, so the descriptor is orphaned on disk.
            TryKill(review);
            review = null;
            Assert.True(File.Exists(descriptorPath), "a killed server should leave its descriptor behind.");

            // poll <plan> proves liveness against the dead port, reports no-session, AND prunes the stale hint.
            var result = await RunCharterAsync(stateDir, "poll", planPath);
            Assert.Equal(3, result.ExitCode);
            Assert.Contains("no running review session", result.StdErr);
            AssertSessionNull(result.StdOut);
            Assert.False(File.Exists(descriptorPath), "poll should prune the stale descriptor.");
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Review_CleanExit_RemovesDescriptor()
    {
        if (OperatingSystem.IsWindows())
        {
            // Delivering a clean interrupt (SIGINT -> Console.CancelKeyPress) to a child is POSIX territory;
            // the removal call itself is exercised cross-platform by the SessionRegistry.Delete unit tests.
            return;
        }

        var stateDir = NewTempDir();
        var planPath = WriteTempPlan(SimplePlan);
        Process? review = null;
        try
        {
            (review, _) = await StartReviewAsync(stateDir, planPath);
            var descriptorPath = SessionRegistry.PathForPlan(stateDir, planPath);
            Assert.True(File.Exists(descriptorPath));

            // SIGINT -> the review handler sets Cancel=true and stops -> the finally removes the descriptor.
            using (var kill = Process.Start("kill", $"-INT {review.Id}"))
            {
                kill?.WaitForExit(5000);
            }

            Assert.True(review.WaitForExit(15000), "review should exit cleanly after SIGINT.");
            Assert.False(File.Exists(descriptorPath), "a clean exit should remove the descriptor.");
        }
        finally
        {
            if (review is not null)
            {
                TryKill(review);
            }

            TryDeleteDir(stateDir);
            TryDelete(planPath);
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private static void AssertSessionNull(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout.Trim());
        Assert.True(doc.RootElement.TryGetProperty("session", out var session), "envelope should carry a session field.");
        Assert.Equal(JsonValueKind.Null, session.ValueKind);
    }

    /// <summary>
    /// The inline <c>answer</c> array of the <c>:::question</c> whose JSON body carries <paramref name="questionId"/>,
    /// or <c>null</c> when that question is still OPEN (no <c>answer</c> key). The fixture's question body is a single
    /// JSON line and <see cref="Charter.Core.QuestionResolution"/> re-serializes it as one line, so scanning the
    /// plan's lines for the id-bearing object and reading its <c>answer</c> is sufficient — and it proves the
    /// living-document write landed on disk by re-parsing the ACTUAL file, not the poll stdout.
    /// </summary>
    private static string[]? ExtractQuestionAnswer(string markdown, string questionId)
    {
        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("{") || !trimmed.Contains("\"" + questionId + "\""))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(trimmed);
            if (!doc.RootElement.TryGetProperty("answer", out var answer) || answer.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var values = new List<string>();
            foreach (var element in answer.EnumerateArray())
            {
                values.Add(element.GetString() ?? string.Empty);
            }

            return values.ToArray();
        }

        return null;
    }

    private static string KeyOf(string capabilityUrl)
        => Regex.Match(capabilityUrl, "key=([0-9a-f]+)").Groups[1].Value;

    private static string AuthorityOf(string capabilityUrl)
        => new Uri(capabilityUrl).GetLeftPart(UriPartial.Authority);

    /// <summary>
    /// Submit an answer to the running review server via its capability URL — a same-origin POST to
    /// <c>/api/{key}/answers</c>, exactly as the browser SDK would.
    /// </summary>
    private static async Task PostAnswerAsync(string capabilityUrl, string questionId, string[] values)
    {
        var baseUri = new Uri(AuthorityOf(capabilityUrl) + "/");
        var answersUri = new Uri(baseUri, $"api/{Uri.EscapeDataString(KeyOf(capabilityUrl))}/answers");

        using var client = new HttpClient();
        var payload = JsonSerializer.Serialize(new { questionId, mode = "single", values, target = "human" });
        using var request = new HttpRequestMessage(HttpMethod.Post, answersUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", AuthorityOf(capabilityUrl));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"seed answer POST should succeed, got {(int)response.StatusCode}.");
    }

    /// <summary>
    /// Start <c>charter review &lt;plan&gt; --no-open</c> as a child process and read stdout until it prints the
    /// ready URL. stderr is drained in the background so a warning line can never fill the pipe and stall the
    /// child. The caller owns killing the returned process.
    /// </summary>
    private static async Task<(Process Process, string Url)> StartReviewAsync(string stateDir, string planPath)
    {
        var process = Process.Start(MakeStartInfo(stateDir, "review", planPath, "--no-open"))
            ?? throw new XunitException("Failed to start charter review.");

        // Drain stderr in the background (fire-and-forget) so the child never blocks writing to a full pipe.
        _ = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break; // EOF: the process exited without a ready line.
                }

                var match = ReadyUrl.Match(line);
                if (match.Success)
                {
                    return (process, match.Value);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fell through to the failure below.
        }

        TryKill(process);
        throw new XunitException("charter review did not print a ready URL in time.");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCharterAsync(
        string? stateDir, params string[] args)
    {
        using var process = Process.Start(MakeStartInfo(stateDir, args))
            ?? throw new XunitException("Failed to start the charter CLI process.");

        // Read both streams concurrently before waiting so a full pipe buffer cannot deadlock the child.
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new XunitException($"charter {string.Join(' ', args)} did not exit within the deadline.");
        }

        return (process.ExitCode, await stdOutTask, await stdErrTask);
    }

    private static ProcessStartInfo MakeStartInfo(string? stateDir, params string[] args)
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
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (stateDir is not null)
        {
            startInfo.Environment["CHARTER_STATE_DIR"] = stateDir;
        }

        return startInfo;
    }

    private static string CharterCliDllPath()
    {
        AssemblyMetadataAttribute? metadata = System.Linq.Enumerable.FirstOrDefault(
            typeof(PollCommandTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
            attribute => attribute.Key == "CharterCliPath");

        string? path = metadata?.Value;
        Assert.False(string.IsNullOrEmpty(path), "The build did not set the CharterCliPath assembly metadata.");
        Assert.True(File.Exists(path), $"Built Charter.Cli.dll not found at '{path}'.");
        return path!;
    }

    private static void TryKill(Process process)
    {
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
        }
        catch (Exception)
        {
            // Ignore.
        }
    }

    private static string WriteTempPlan(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "charter-poll-plan-" + Guid.NewGuid().ToString("N") + ".charter.md");
        File.WriteAllText(path, content);
        return path;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-poll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup.
        }
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
            // Best-effort cleanup.
        }
    }
}
