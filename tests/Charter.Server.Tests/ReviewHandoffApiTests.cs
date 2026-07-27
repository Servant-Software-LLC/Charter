using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// The reviewer's ROUND HAND-OFF, server side — the two halves that let a reviewer stay in the browser:
/// <list type="number">
///   <item><b>Charter #62.</b> The <c>/api/poll</c> long-poll waits on EITHER queue. An annotation always woke
///   it; a <c>:::question</c> ANSWER used to wake nothing, so the reviewer's highest-value signal — their
///   decision — sat unseen until the ~30s timeout elapsed. The mirror-image guard matters just as much: once
///   the queues are drained AND acked, the next wait must BLOCK, or <c>poll --wait</c> degenerates into a hot
///   loop returning instantly forever (the stale-completed-signal bug the annotation store learned in #42).</item>
///   <item><b>The in-page "Send to agent" control.</b> <c>POST /api/{key}/review/submit</c> records "the
///   reviewer marked this round complete" and wakes the long-poll; <c>GET /api/review?key=…</c> reports it;
///   <c>POST /api/{key}/review/ack?sequence=N</c> clears it once an agent has been told. The server records
///   and signals ONLY — it never writes the plan (Architecture B's single-writer invariant: the drafting agent
///   owns the <c>.charter.md</c>).</item>
/// </list>
/// Every test starts a REAL loopback server and drives it over <see cref="HttpClient"/>.
/// </summary>
[Trait("Category", "ReviewHandoffApi")]
public class ReviewHandoffApiTests
{
    private const string PlanMarkdown =
        "# Review Hand-off Plan\n\nAn overview paragraph the reviewer can annotate.\n";

    // How long a woken long-poll may take before we call it "not woken". The server's own PollTimeout is ~30s,
    // so anything under this proves the wake signal fired rather than the timeout elapsing.
    private static readonly TimeSpan PromptWake = TimeSpan.FromSeconds(10);

    // How long a poll must stay outstanding before we accept "it is genuinely blocking" (the hot-loop guard).
    private static readonly TimeSpan BlockingProof = TimeSpan.FromSeconds(1.5);

    // ---- Charter #62: the long-poll wakes on EITHER queue ------------------------------------------------

    [Fact]
    public async Task LongPoll_WakesPromptly_WhenAnAnswerIsSubmittedWhileItIsOutstanding()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            // A DEFAULT poll (no wait=0) on empty queues: it long-polls.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var stopwatch = Stopwatch.StartNew();
            var pollTask = client.GetAsync(PollUri(server, session), cts.Token);
            await AssertStillPendingAsync(pollTask);

            // The reviewer answers a :::question. Before #62 this woke nothing — the poll ran to its ~30s
            // timeout while the reviewer stared at a page that looked like it had done nothing.
            await PostAnswerAsync(client, server, session, "q-theme");

            using var response = await pollTask.WaitAsync(PromptWake, cts.Token);
            stopwatch.Stop();

            Assert.True(response.IsSuccessStatusCode);
            Assert.True(
                stopwatch.Elapsed < PromptWake,
                $"an answer must wake the outstanding long-poll promptly, took {stopwatch.Elapsed}.");

            // The poll's own body is the ANNOTATION drain — unchanged, still a bare array (additive fix).
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.Equal(0, doc.RootElement.GetArrayLength());

            // ...and the answer that woke it is still peekable (peek → apply → commit is untouched).
            Assert.Equal(1, (await GetJsonAsync(client, AnswersUri(server, session))).GetArrayLength());
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task LongPoll_StillWakesPromptly_WhenAnAnnotationIsSubmitted()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var pollTask = client.GetAsync(PollUri(server, session), cts.Token);
            await AssertStillPendingAsync(pollTask);

            // The pre-existing wake path must survive waiting on more than one store.
            await PostAnnotationAsync(client, server, session, "Wake the agent.");

            using var response = await pollTask.WaitAsync(PromptWake, cts.Token);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            Assert.Equal(1, doc.RootElement.GetArrayLength());
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task LongPoll_AfterAnswersAreAcked_BlocksAgain_RatherThanSpinning()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            // Queue an answer, then ACK it — the commit path, which is exactly where a missed re-arm of the
            // wake signal leaves a stale completed signal behind.
            await PostAnswerAsync(client, server, session, "q-theme");
            using (var ack = await PostAsync(client, AckAnswersUri(server, session, 1), SameOrigin(server)))
            {
                Assert.True(ack.IsSuccessStatusCode);
            }

            Assert.Equal(0, (await GetJsonAsync(client, AnswersUri(server, session))).GetArrayLength());

            // With both queues empty the next long-poll must BLOCK. A stale completed signal would return an
            // empty array instantly — forever — which is a 100%-CPU `poll --wait` loop, not a wait.
            using var cts = new CancellationTokenSource();
            var pollTask = client.GetAsync(PollUri(server, session), cts.Token);
            await AssertStillPendingAsync(pollTask);

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pollTask);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task LongPoll_WithBothQueuesNonEmpty_DrainsAnnotationsAndLeavesAnswersQueued()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            await PostAnnotationAsync(client, server, session, "Both queues carry work.");
            await PostAnswerAsync(client, server, session, "q-theme");

            // Both signals are complete: the poll fast-paths (no wait at all) and the two disciplines stay
            // distinct — annotations drain destructively, answers are left for peek → apply → commit.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stopwatch = Stopwatch.StartNew();
            using var response = await client.GetAsync(PollUri(server, session), cts.Token);
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < PromptWake, $"a non-empty queue must not wait, took {stopwatch.Elapsed}.");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            Assert.Equal(1, doc.RootElement.GetArrayLength());
            Assert.Equal(1, (await GetJsonAsync(client, AnswersUri(server, session))).GetArrayLength());
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- The "Send to agent" hand-off: submit / report / ack ---------------------------------------------

    [Fact]
    public async Task ReviewSubmit_RecordsTheMarker_WithWhenAndWhatWasPending()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            // Nothing handed off yet: the status route reports the round as open, with live pending counts.
            var before = await GetJsonAsync(client, ReviewUri(server, session));
            Assert.False(before.GetProperty("submitted").GetBoolean());
            Assert.Equal(JsonValueKind.Null, before.GetProperty("submission").ValueKind);
            Assert.Equal(0, before.GetProperty("pending").GetProperty("annotations").GetInt32());
            Assert.Equal(0, before.GetProperty("pending").GetProperty("answers").GetInt32());

            await PostAnnotationAsync(client, server, session, "One note to hand off.");
            await PostAnswerAsync(client, server, session, "q-theme");

            using (var submit = await PostAsync(client, SubmitUri(server, session), SameOrigin(server)))
            {
                Assert.True(submit.IsSuccessStatusCode);
            }

            var after = await GetJsonAsync(client, ReviewUri(server, session));
            Assert.True(after.GetProperty("submitted").GetBoolean());

            var submission = after.GetProperty("submission");
            Assert.Equal(JsonValueKind.Object, submission.ValueKind);
            Assert.Equal(1, submission.GetProperty("annotations").GetInt32());
            Assert.Equal(1, submission.GetProperty("answers").GetInt32());
            Assert.True(submission.GetProperty("sequence").GetInt64() > 0);
            Assert.True(
                submission.GetProperty("submittedAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5),
                "the marker must record WHEN the reviewer handed the round off.");

            // The signal never writes the plan — that stays the drafting agent's job (Architecture B).
            Assert.Equal(PlanMarkdown, await File.ReadAllTextAsync(planPath));
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task ReviewSubmit_WakesAnOutstandingLongPoll_EvenWithNothingNewQueued()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var stopwatch = Stopwatch.StartNew();
            var pollTask = client.GetAsync(PollUri(server, session), cts.Token);
            await AssertStillPendingAsync(pollTask);

            // The reviewer clicks "Send to agent" with both queues empty (the agent already drained them). The
            // hand-off is itself the signal, so the waiting agent must still wake.
            using (var submit = await PostAsync(client, SubmitUri(server, session), SameOrigin(server)))
            {
                Assert.True(submit.IsSuccessStatusCode);
            }

            using var response = await pollTask.WaitAsync(PromptWake, cts.Token);
            stopwatch.Stop();

            Assert.True(response.IsSuccessStatusCode);
            Assert.True(
                stopwatch.Elapsed < PromptWake,
                $"'Send to agent' must wake the outstanding long-poll, took {stopwatch.Elapsed}.");
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task ReviewAck_ClearsTheMarker_SoItDoesNotReFire_AndTheNextPollBlocks()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            using (var submit = await PostAsync(client, SubmitUri(server, session), SameOrigin(server)))
            {
                Assert.True(submit.IsSuccessStatusCode);
            }

            var sequence = (await GetJsonAsync(client, ReviewUri(server, session)))
                .GetProperty("submission").GetProperty("sequence").GetInt64();

            using (var ack = await PostAsync(client, AckReviewUri(server, session, sequence), SameOrigin(server)))
            {
                Assert.True(ack.IsSuccessStatusCode);
                using var body = JsonDocument.Parse(await ack.Content.ReadAsStringAsync());
                Assert.True(body.RootElement.GetProperty("acked").GetBoolean());
            }

            var cleared = await GetJsonAsync(client, ReviewUri(server, session));
            Assert.False(cleared.GetProperty("submitted").GetBoolean());
            Assert.Equal(JsonValueKind.Null, cleared.GetProperty("submission").ValueKind);

            // Cleared means cleared: the wake signal is re-armed, so the next long-poll BLOCKS instead of
            // returning instantly forever on a stale marker.
            using var cts = new CancellationTokenSource();
            var pollTask = client.GetAsync(PollUri(server, session), cts.Token);
            await AssertStillPendingAsync(pollTask);

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pollTask);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task ReviewAck_WithAStaleSequence_LeavesTheNewerHandOffPending()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            using (var first = await PostAsync(client, SubmitUri(server, session), SameOrigin(server)))
            {
                Assert.True(first.IsSuccessStatusCode);
            }

            var stale = (await GetJsonAsync(client, ReviewUri(server, session)))
                .GetProperty("submission").GetProperty("sequence").GetInt64();

            // The reviewer hands off a SECOND round before the agent's ack for the first lands. Acking by
            // sequence is a compare-and-clear, so the stale ack must not swallow the newer hand-off.
            using (var second = await PostAsync(client, SubmitUri(server, session), SameOrigin(server)))
            {
                Assert.True(second.IsSuccessStatusCode);
            }

            using (var ack = await PostAsync(client, AckReviewUri(server, session, stale), SameOrigin(server)))
            {
                using var body = JsonDocument.Parse(await ack.Content.ReadAsStringAsync());
                Assert.False(body.RootElement.GetProperty("acked").GetBoolean());
            }

            var status = await GetJsonAsync(client, ReviewUri(server, session));
            Assert.True(status.GetProperty("submitted").GetBoolean());
            Assert.True(status.GetProperty("submission").GetProperty("sequence").GetInt64() > stale);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- Gates: the capability key and CSRF/same-origin, exactly like the other writes -------------------

    [Fact]
    public async Task ReviewSubmit_WithTheWrongKey_IsRejected()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            var forged = new Uri(server.Address, "api/not-the-real-key/review/submit");
            using (var response = await PostAsync(client, forged, SameOrigin(server)))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            // ...and nothing was recorded: a refused hand-off must not half-land.
            Assert.False((await GetJsonAsync(client, ReviewUri(server, session)))
                .GetProperty("submitted").GetBoolean());
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task ReviewSubmit_WithForeignOrigin_IsRejected_CsrfSameOrigin()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            // A state-changing POST must not be forgeable from a foreign origin even WITH a valid key.
            using (var response = await PostAsync(
                       client, SubmitUri(server, session), "https://charter-review.attacker.example"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }

            Assert.False((await GetJsonAsync(client, ReviewUri(server, session)))
                .GetProperty("submitted").GetBoolean());
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task ReviewAck_WithoutTheKeyOrFromAForeignOrigin_IsRejected()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            using (var submit = await PostAsync(client, SubmitUri(server, session), SameOrigin(server)))
            {
                Assert.True(submit.IsSuccessStatusCode);
            }

            var sequence = (await GetJsonAsync(client, ReviewUri(server, session)))
                .GetProperty("submission").GetProperty("sequence").GetInt64();

            var wrongKey = new Uri(server.Address, $"api/not-the-real-key/review/ack?sequence={sequence}");
            using (var response = await PostAsync(client, wrongKey, SameOrigin(server)))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            using (var response = await PostAsync(
                       client, AckReviewUri(server, session, sequence), "https://charter-review.attacker.example"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }

            // Neither refusal cleared the reviewer's hand-off.
            Assert.True((await GetJsonAsync(client, ReviewUri(server, session)))
                .GetProperty("submitted").GetBoolean());
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task ReviewStatus_WithoutOrWithWrongKey_IsRejected()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            using (var noKey = await client.GetAsync(new Uri(server.Address, "api/review"), cts.Token))
            {
                Assert.NotEqual(HttpStatusCode.OK, noKey.StatusCode);
            }

            using (var wrongKey = await client.GetAsync(
                       new Uri(server.Address, "api/review?key=not-the-real-key"), cts.Token))
            {
                Assert.NotEqual(HttpStatusCode.OK, wrongKey.StatusCode);
            }
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    /// <summary>
    /// Assert <paramref name="pending"/> has NOT completed after <see cref="BlockingProof"/> — the poll is
    /// genuinely long-polling. Used both to arm a wake test (post only once the wait is outstanding) and as
    /// the hot-loop guard itself.
    /// </summary>
    private static async Task AssertStillPendingAsync(Task pending)
    {
        var settled = await Task.WhenAny(pending, Task.Delay(BlockingProof));
        Assert.NotSame(pending, settled);
    }

    private static Uri PollUri(ReviewServer server, ReviewSession session)
        => new(server.Address, "api/poll?key=" + Uri.EscapeDataString(session.Key.Value));

    private static Uri AnswersUri(ReviewServer server, ReviewSession session)
        => new(server.Address, "api/answers?key=" + Uri.EscapeDataString(session.Key.Value));

    private static Uri ReviewUri(ReviewServer server, ReviewSession session)
        => new(server.Address, "api/review?key=" + Uri.EscapeDataString(session.Key.Value));

    private static Uri SubmitUri(ReviewServer server, ReviewSession session)
        => new(server.Address, $"api/{Uri.EscapeDataString(session.Key.Value)}/review/submit");

    private static Uri AckReviewUri(ReviewServer server, ReviewSession session, long sequence)
        => new(server.Address, $"api/{Uri.EscapeDataString(session.Key.Value)}/review/ack?sequence={sequence}");

    private static Uri AckAnswersUri(ReviewServer server, ReviewSession session, int count)
        => new(server.Address, $"api/{Uri.EscapeDataString(session.Key.Value)}/answers/ack?count={count}");

    private static string SameOrigin(ReviewServer server) => server.Address.GetLeftPart(UriPartial.Authority);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Uri uri, string? origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
        };
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await client.SendAsync(request, cts.Token);
    }

    private static async Task PostAnnotationAsync(
        HttpClient client, ReviewServer server, ReviewSession session, string note)
    {
        var uri = new Uri(server.Address, $"api/{Uri.EscapeDataString(session.Key.Value)}/prompts");
        var payload = JsonSerializer.Serialize(
            new { kind = "element", anchorId = "b00000000000000000000", note });
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", SameOrigin(server));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"seed annotation POST failed: {(int)response.StatusCode}.");
    }

    private static async Task PostAnswerAsync(
        HttpClient client, ReviewServer server, ReviewSession session, string questionId)
    {
        var uri = new Uri(server.Address, $"api/{Uri.EscapeDataString(session.Key.Value)}/answers");
        var payload = JsonSerializer.Serialize(
            new { questionId, mode = "single", values = new[] { "A" }, target = "human" });
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", SameOrigin(server));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"seed answer POST failed: {(int)response.StatusCode}.");
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, Uri uri)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(uri, cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"GET {uri.AbsolutePath} failed: {(int)response.StatusCode}.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
        return doc.RootElement.Clone();
    }

    private static string WriteTempPlan()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "charter-handoff-plan-" + Guid.NewGuid().ToString("N") + ".charter.md");
        File.WriteAllText(path, PlanMarkdown);
        return path;
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
        catch (IOException)
        {
            // A leaked temp file is harmless if the OS still holds a handle during a slow server dispose.
        }
    }
}
