using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Charter.Core;
using Xunit;
using Xunit.Sdk;

namespace Charter.Cli.Tests;

/// <summary>
/// The SOLO path, exercised through the real <c>charter</c> binary — the seam the review-log integration
/// regressed. §5.0 of <c>docs/plans/03-git-mediated-team-review.md</c> is binding: <i>"A solo reviewer who never
/// intends to share must not be nagged… No new required setup."</i> The library's opt-in default
/// (<c>ReviewServerOptions.ReviewLog = null</c>) never applied in the product, because <c>charter review</c>
/// always supplies a writer — so only a PROCESS-level test covers where the regression actually lived.
///
/// Also covers Charter #67 end to end: a plan deleted and re-authored at the same path must not hand the new
/// document the old document's annotation queue.
/// </summary>
[Trait("Category", "Cli")]
public class SoloReviewPathTests
{
    private const string Plan =
        "# Solo plan\n\nA paragraph a reviewer might annotate, or might simply read.\n";

    private const string OriginalPlan =
        "# Rate limiting\n\n" +
        "The read path stays Postgres-only until the write path is proven.\n";

    private const string ReplacementPlan =
        "# Tenant onboarding\n\n" +
        "Every tenant gets an isolated schema provisioned at signup time.\n";

    private const string OldNote = "the note from the old document";

    private static readonly Regex ReadyUrl = new(
        @"http://127\.0\.0\.1:\d+/\?key=\S+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ---- Blocker 1: a session that writes nothing leaves nothing --------------------------------------

    [Fact]
    public async Task Review_SessionThatWritesNothing_LeavesNoNewFileOrDirectoryBesideThePlan()
    {
        using var work = new Workspace();
        var planPath = Path.Combine(work.PlanDirectory, "plan.charter.md");
        await File.WriteAllTextAsync(planPath, Plan);
        var before = Snapshot(work.PlanDirectory);

        var review = await work.StartReviewAsync(planPath);
        review.Stop();

        // The server was fully up (it printed its capability URL) and then went away without the reviewer
        // writing a single comment. Nothing may have appeared beside their plan.
        Assert.Equal(before, Snapshot(work.PlanDirectory));
        Assert.False(
            Directory.Exists(Path.Combine(work.PlanDirectory, "plan.charter.review")),
            "opening a plan for review must not create a .review/ directory (§5.0).");

        // ...and nothing may have been said about permanence, git, or committing: this reviewer never opted
        // into sharing, so §7's notice is not relevant to them and a per-session banner is exactly the nag
        // §5.0 forbids.
        var stderr = review.StdErr;
        Assert.DoesNotContain("permanent", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COMMITTED", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(".gitignore", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("git config", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the §5.0 gate, and the one that keeps the silence honest: when the reviewer HAS opted
    /// into sharing — git already tracks the plan's <c>.review/</c> — §7's permanence notice still fires, once,
    /// at the moment the first record actually lands. Without this, "silent for solo" could be satisfied by
    /// deleting the notice altogether.
    /// </summary>
    [Fact]
    public async Task Review_InARepoTrackingTheReviewDirectory_StatesPermanenceOnTheFirstWrittenComment()
    {
        using var work = new Workspace();
        if (!work.InitGitRepo())
        {
            return; // git is a supported absence; GitTrackingTests carries the deterministic coverage.
        }

        var planPath = Path.Combine(work.PlanDirectory, "plan.charter.md");
        await File.WriteAllTextAsync(planPath, Plan);

        // A teammate's log, already committed to the index: this is what makes the review SHARED.
        var reviewDir = Path.Combine(work.PlanDirectory, "plan.charter.review");
        Directory.CreateDirectory(reviewDir);
        await File.WriteAllTextAsync(Path.Combine(reviewDir, "teammate.deadbeef.jsonl"), "{}\n");
        Assert.True(work.Git("add", "--", "plan.charter.review"), "git add should succeed in a fresh repo");

        var review = await work.StartReviewAsync(planPath);
        try
        {
            // Serving alone still says nothing — the notice is a fact about a record, not about a session.
            Assert.DoesNotContain("permanent", review.StdErr, StringComparison.OrdinalIgnoreCase);

            await SubmitAnnotationAsync(review.Url, FirstAnchorOf(Plan), "a comment that will be committed");
            await review.WaitForStdErrAsync("permanent in history");
            Assert.Contains(".gitignore", review.StdErr, StringComparison.Ordinal);

            // ...and it is said ONCE, not per comment.
            await SubmitAnnotationAsync(review.Url, FirstAnchorOf(Plan), "a second comment");
            await Task.Delay(250);
            Assert.Equal(
                1,
                CountOccurrences(review.StdErr, "permanent in history"));
        }
        finally
        {
            review.Stop();
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal);
             at >= 0;
             at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // ---- Charter #67: a replaced plan does not inherit the old queue ----------------------------------

    [Fact]
    public async Task Review_PlanReplacedAtTheSamePath_DoesNotHandTheOldQueueToPoll()
    {
        using var work = new Workspace();
        var planPath = Path.Combine(work.PlanDirectory, "plan.charter.md");

        // Round 1: a real review session, with a real annotation submitted through the loopback API, so the
        // queue is seeded exactly the way a human seeds it.
        await File.WriteAllTextAsync(planPath, OriginalPlan);
        var first = await work.StartReviewAsync(planPath);
        try
        {
            await SubmitAnnotationAsync(first.Url, FirstAnchorOf(OriginalPlan), OldNote);
        }
        finally
        {
            first.Stop();
        }

        // The plan is DELETED and a substantially different one is authored at the same path.
        File.Delete(planPath);
        await File.WriteAllTextAsync(planPath, ReplacementPlan);

        // Round 2: the new document's review must start clean, and say why.
        var second = await work.StartReviewAsync(planPath);
        try
        {
            await second.WaitForStdErrAsync("written against a different document");
            Assert.Contains("--keep-annotations", second.StdErr, StringComparison.Ordinal);

            var poll = await work.RunAsync("poll", planPath);

            // The headline of #67: `charter poll` handed back every annotation of the deleted file.
            Assert.DoesNotContain(OldNote, poll.StdOut, StringComparison.Ordinal);
            Assert.Equal(ReviewExitCodes.CleanEmpty, poll.ExitCode);
        }
        finally
        {
            second.Stop();
        }

        // Nothing was destroyed: the set-aside queue is on disk and still carries the reviewer's words.
        var kept = Assert.Single(
            Directory.EnumerateFiles(Path.Combine(work.StateDirectory, "sidecars"), "*.stale-*.json"));
        Assert.Contains(OldNote, await File.ReadAllTextAsync(kept), StringComparison.Ordinal);
    }

    // ---- plumbing ------------------------------------------------------------------------------------

    /// <summary>The stable block id of the plan's first anchorable block, as the SDK would post it.</summary>
    private static string FirstAnchorOf(string markdown)
        => SourceMap.Build(markdown).Anchors.OrderBy(anchor => anchor, StringComparer.Ordinal).First();

    /// <summary>
    /// A plan directory and a state directory that are SIBLINGS, never nested. The footprint assertion counts
    /// every entry beside the plan, so a <c>CHARTER_STATE_DIR</c> inside it would make the test pass or fail on
    /// Charter's own bookkeeping instead of on what it leaves next to the reviewer's file.
    /// </summary>
    private sealed class Workspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "charter-solo-cli-" + Guid.NewGuid().ToString("N"));

        public Workspace()
        {
            PlanDirectory = Path.Combine(_root, "plans");
            StateDirectory = Path.Combine(_root, "state");
            Directory.CreateDirectory(PlanDirectory);
            Directory.CreateDirectory(StateDirectory);
        }

        public string PlanDirectory { get; }

        public string StateDirectory { get; }

        public void Dispose()
        {
            try
            {
                // A .git directory carries read-only objects on Windows; clear the bit before recursing.
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
            }
            catch (Exception)
            {
                // Best-effort; the delete below is itself best-effort.
            }

            CharterCliRunner.TryDeleteDirectory(_root);
        }

        /// <summary>Make the PLAN directory a git repository. False when git could not be run at all.</summary>
        public bool InitGitRepo() => Git("init");

        /// <summary>One git command in the plan directory. Read or write — this is TEST setup, not Charter.</summary>
        public bool Git(params string[] arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = PlanDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
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

        /// <summary>
        /// Start <c>charter review &lt;plan&gt; --no-open</c> and read stdout until it prints its capability
        /// URL. stderr is drained in the background so a warning line can never fill the pipe and stall the
        /// child. The caller stops it.
        /// </summary>
        public async Task<ReviewRun> StartReviewAsync(string planPath)
        {
            var process = Process.Start(StartInfo("review", planPath, "--no-open"))
                ?? throw new XunitException("Failed to start charter review.");
            var run = new ReviewRun(process);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync(cts.Token)) is not null)
                {
                    var match = ReadyUrl.Match(line);
                    if (match.Success)
                    {
                        run.Url = match.Value;
                        return run;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Fall through to the failure below.
            }

            run.Stop();
            throw new XunitException("charter review did not print a ready URL in time.");
        }

        public async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(params string[] args)
        {
            using var process = Process.Start(StartInfo(args))
                ?? throw new XunitException("Failed to start the charter CLI process.");

            var stdOut = process.StandardOutput.ReadToEndAsync();
            var stdErr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Best-effort teardown; the assertion below reports the real failure.
                }

                throw new XunitException($"charter {string.Join(' ', args)} did not exit within the deadline.");
            }

            return (process.ExitCode, await stdOut, await stdErr);
        }

        private ProcessStartInfo StartInfo(params string[] args)
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = PlanDirectory,
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

            startInfo.Environment["CHARTER_STATE_DIR"] = StateDirectory;
            return startInfo;
        }
    }

    /// <summary>A running <c>charter review</c> child, its capability URL, and everything it has said on stderr.</summary>
    private sealed class ReviewRun
    {
        private readonly Process _process;
        private readonly StringBuilder _stderr = new();

        public ReviewRun(Process process)
        {
            _process = process;
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) is not null)
                {
                    lock (_stderr)
                    {
                        _stderr.AppendLine(line);
                    }
                }
            });
        }

        public string Url { get; set; } = string.Empty;

        public string StdErr
        {
            get
            {
                lock (_stderr)
                {
                    return _stderr.ToString();
                }
            }
        }

        /// <summary>
        /// Wait until stderr carries <paramref name="text"/>. Bounded, never a fixed sleep: the notice is
        /// written before the ready line, but the two streams are read independently, so its arrival in this
        /// buffer is not ordered against the URL the caller already saw.
        /// </summary>
        public async Task WaitForStdErrAsync(string text, int timeoutMs = 20_000)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (StdErr.Contains(text, StringComparison.Ordinal))
                {
                    return;
                }

                await Task.Delay(50);
            }

            Assert.Fail($"charter review never said '{text}' on stderr. It said:\n{StdErr}");
        }

        public void Stop()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Best-effort teardown.
            }

            try
            {
                _process.WaitForExit(10_000);
            }
            catch (Exception)
            {
                // Ignore.
            }
        }
    }

    /// <summary>POST one element annotation through the loopback API, exactly as the browser SDK does.</summary>
    private static async Task SubmitAnnotationAsync(string capabilityUrl, string anchorId, string note)
    {
        var url = new Uri(capabilityUrl);
        var key = KeyOf(url);

        using var client = new HttpClient();
        var target = new Uri(url, $"api/{Uri.EscapeDataString(key)}/prompts");
        var payload = JsonSerializer.Serialize(new { kind = "element", anchorId, note });

        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", url.GetLeftPart(UriPartial.Authority));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.SendAsync(request, cts.Token);
        Assert.True(
            response.IsSuccessStatusCode,
            $"seeding an annotation should succeed, got {(int)response.StatusCode}.");
    }

    // The ready URL is exactly http://127.0.0.1:<port>/?key=<key>, so the key is the query minus its "?key=".
    private static string KeyOf(Uri url)
    {
        const string prefix = "?key=";
        Assert.StartsWith(prefix, url.Query, StringComparison.Ordinal);
        return Uri.UnescapeDataString(url.Query[prefix.Length..]);
    }

    private static string CharterCliDllPath()
    {
        var metadata = typeof(SoloReviewPathTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "CharterCliPath");

        var path = metadata?.Value;
        Assert.False(string.IsNullOrEmpty(path), "The build did not set the CharterCliPath assembly metadata.");
        Assert.True(File.Exists(path), $"Built Charter.Cli.dll not found at '{path}'.");
        return path!;
    }

    /// <summary>Every file and directory under <paramref name="directory"/>, as a comparable sorted list.</summary>
    private static string Snapshot(string directory)
        => string.Join(
            "\n",
            Directory
                .EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
                .OrderBy(entry => entry, StringComparer.Ordinal));
}
