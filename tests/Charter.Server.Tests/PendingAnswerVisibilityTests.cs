using System;
using System.IO;
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
/// A reviewer's answers must be VISIBLE on the page, not merely durable (Charter #120).
/// <para>
/// The reported sequence: five questions answered, the review server force-killed to release a file lock,
/// the plan re-served on a new port — and every question came back BLANK. Nothing had been lost; the sidecar
/// held all six submissions and a later <c>poll --apply</c> returned every one, including the long free-text
/// write-ins. But for the reviewer, a working safety net was indistinguishable from data loss, and the
/// rational response to that is to re-enter twenty minutes of decisions or to stop trusting the tool.
/// </para>
/// <para>
/// The fix is an OVERLAY at render time, not a write: the server renders the plan per request and hands the
/// renderer the answers that are saved-but-not-yet-folded. That reuses the pre-selection, write-in and
/// escaping the renderer already implements for inline answers instead of duplicating it in JavaScript — and
/// it means a JS-free page shows them too. The plan file is still never written by the server.
/// </para>
/// </summary>
[Trait("Category", "PendingAnswerVisibility")]
public class PendingAnswerVisibilityTests
{
    private const string Plan =
        "# Theme Plan\n" +
        "\n" +
        "An overview paragraph.\n" +
        "\n" +
        ":::question\n" +
        "{\"id\":\"q-colour\",\"title\":\"Which accent colour?\",\"mode\":\"single\"," +
        "\"options\":[\"blue\",\"green\"],\"target\":\"human\"}\n" +
        ":::\n";

    private const string PlanWithInlineAnswer =
        "# Theme Plan\n" +
        "\n" +
        "An overview paragraph.\n" +
        "\n" +
        ":::question\n" +
        "{\"id\":\"q-colour\",\"title\":\"Which accent colour?\",\"mode\":\"single\"," +
        "\"options\":[\"blue\",\"green\"],\"target\":\"human\",\"answer\":[\"blue\"]}\n" +
        ":::\n";

    /// <summary>
    /// THE regression. Answer, kill the server, serve the same plan again — the answer must be on the page.
    /// The second server is a genuinely new instance rehydrating from the sidecar, which is exactly what the
    /// reviewer hit (their new server even bound a different port).
    /// </summary>
    [Fact]
    public async Task AnAnswerSurvivesAServerRestart_AndIsVisibleOnTheNewlyServedPage()
    {
        var fixture = NewFixture(Plan);
        try
        {
            using (var first = Start(fixture))
            {
                await PostAnswer(first, fixture.Key(first), "q-colour", "green");
            }

            // A brand-new server over the same plan — a different port, the same sidecar.
            using var second = Start(fixture);
            var html = await GetPage(second, fixture.Key(second));

            Assert.Contains("data-answered=\"true\"", html);
            Assert.Contains("value=\"green\" checked", html);
            Assert.DoesNotContain("value=\"blue\" checked", html);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    /// A free-text write-in is the answer most expensive to reproduce and the one the reviewer specifically
    /// lost sight of. It matches no declared option, so it must come back through the renderer's write-in
    /// path rather than being dropped for not being on the list.
    /// </summary>
    [Fact]
    public async Task AFreeTextWriteIn_ComesBackVerbatim_NotDroppedForMatchingNoOption()
    {
        const string writeIn = "neither — use the brand palette from the 2025 refresh";
        var fixture = NewFixture(Plan);
        try
        {
            using (var first = Start(fixture))
            {
                await PostAnswer(first, fixture.Key(first), "q-colour", writeIn);
            }

            using var second = Start(fixture);
            var html = await GetPage(second, fixture.Key(second));

            Assert.Contains("data-answered=\"true\"", html);
            Assert.Contains("2025 refresh", html);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    /// "Saved" and "the agent has it" are different facts, and the page must not assert the second when only
    /// the first is true — the same honesty #115 forced on the hand-off status line.
    /// </summary>
    [Fact]
    public async Task APendingAnswer_IsMarkedAsSavedButNotYetSent()
    {
        var fixture = NewFixture(Plan);
        try
        {
            using var server = Start(fixture);
            await PostAnswer(server, fixture.Key(server), "q-colour", "green");

            var html = await GetPage(server, fixture.Key(server));

            Assert.Contains("data-answer-pending=\"true\"", html);
            Assert.Contains("not yet sent", html);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    /// A queued answer is LATER than the one folded into the plan, so it wins. Otherwise a reviewer who
    /// changed their mind would keep being shown the decision they just replaced.
    /// </summary>
    [Fact]
    public async Task APendingAnswer_OverridesTheOneAlreadyFoldedIntoThePlan()
    {
        var fixture = NewFixture(PlanWithInlineAnswer);
        try
        {
            using var server = Start(fixture);
            await PostAnswer(server, fixture.Key(server), "q-colour", "green");

            var html = await GetPage(server, fixture.Key(server));

            Assert.Contains("value=\"green\" checked", html);
            Assert.DoesNotContain("value=\"blue\" checked", html);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    /// An emptied submission is a RETRACTION (#63), not the absence of one. Treating an empty queued answer as
    /// "nothing pending" would silently restore the answer the reviewer just cleared.
    /// </summary>
    [Fact]
    public async Task APendingRetraction_ReopensAQuestionThePlanRecordsAsAnswered()
    {
        var fixture = NewFixture(PlanWithInlineAnswer);
        try
        {
            using var server = Start(fixture);
            await PostAnswer(server, fixture.Key(server), "q-colour", values: Array.Empty<string>());

            var html = await GetPage(server, fixture.Key(server));

            Assert.DoesNotContain("value=\"blue\" checked", html);
            Assert.DoesNotContain("data-answered=\"true\"", html);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    /// The buffer is append-only, so a reviewer who changed their mind has two entries for one question. The
    /// agent is told the whole history on drain; the PAGE must show only the current decision.
    /// </summary>
    [Fact]
    public async Task WhenAnAnswerWasChanged_ThePageShowsTheLatestOne()
    {
        var fixture = NewFixture(Plan);
        try
        {
            using var server = Start(fixture);
            await PostAnswer(server, fixture.Key(server), "q-colour", "blue");
            await PostAnswer(server, fixture.Key(server), "q-colour", "green");

            var html = await GetPage(server, fixture.Key(server));

            Assert.Contains("value=\"green\" checked", html);
            Assert.DoesNotContain("value=\"blue\" checked", html);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    /// Continuity across the drain. Once <c>poll --apply</c> folds the answer into the plan and acks it, the
    /// queue is empty and the overlay contributes nothing — the inline answer must carry the page from there.
    /// A gap here would reproduce the original bug one step later.
    /// </summary>
    [Fact]
    public async Task AfterTheAnswerIsFoldedIntoThePlanAndDrained_ThePageStillShowsItAnswered()
    {
        var fixture = NewFixture(Plan);
        try
        {
            using var server = Start(fixture);
            await PostAnswer(server, fixture.Key(server), "q-colour", "blue");

            // What `poll --apply` does: fold the answer inline, then commit it out of the queue.
            File.WriteAllText(fixture.PlanPath, PlanWithInlineAnswer);
            await DrainAndAck(server, fixture.Key(server));

            var html = await GetPage(server, fixture.Key(server));

            Assert.Contains("value=\"blue\" checked", html);
            Assert.Contains("data-answered=\"true\"", html);
            Assert.DoesNotContain("data-answer-pending=\"true\"", html);   // it is delivered now, not pending
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    /// Case 2 of #120, composed end to end: an <c>--apply</c> that folds an answer into the plan must reach the
    /// page the reviewer is looking at, or they keep staring at the open question they already answered. The
    /// reload machinery (<c>PlanWatch</c> → <c>/events</c>) and the per-request render are each covered
    /// elsewhere; what was never asserted is that the two compose for THIS write.
    /// </summary>
    [Fact]
    public async Task FoldingAnAnswerIntoThePlan_DeliversAReloadToTheOpenPage()
    {
        var fixture = NewFixture(Plan);
        try
        {
            using var server = Start(fixture);
            var key = fixture.Key(server);
            await PostAnswer(server, key, "q-colour", "blue");

            using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = new HttpClient();
            var eventsUri = new Uri(server.Address, "events?key=" + Uri.EscapeDataString(key));
            using var response = await client.GetAsync(
                eventsUri, HttpCompletionOption.ResponseHeadersRead, overall.Token);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

            await using var stream = await response.Content.ReadAsStreamAsync(overall.Token);
            var received = new StringBuilder();
            var buffer = new byte[2048];

            // Wait for the connect ping so the stream is provably live BEFORE the write — otherwise a reload
            // racing the connect could be missed and the test would flake rather than fail.
            await ReadUntil(stream, buffer, received, "event: ping", overall.Token);

            File.WriteAllText(fixture.PlanPath, PlanWithInlineAnswer);

            Assert.True(
                await ReadUntil(stream, buffer, received, "event: reload", overall.Token),
                "an apply that rewrites the plan must reload the reviewer's page; without it they keep "
                    + "reading the open question they already answered (Charter #120, case 2)");

            var html = await GetPage(server, key);
            Assert.Contains("value=\"blue\" checked", html);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task<bool> ReadUntil(
        Stream stream, byte[] buffer, StringBuilder received, string marker, CancellationToken token)
    {
        while (!received.ToString().Contains(marker, StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read <= 0)
            {
                return false;
            }

            received.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return true;
    }

    // ---- harness ---------------------------------------------------------------------------------------

    private sealed class Fixture : IDisposable
    {
        public required string Root { get; init; }

        public required string PlanPath { get; init; }

        public required string SidecarDirectory { get; init; }

        public string Key(ReviewServer server) => _keys[server];

        private readonly Dictionary<ReviewServer, string> _keys = new();

        public void Remember(ReviewServer server, string key) => _keys[server] = key;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static Fixture NewFixture(string markdown)
    {
        var root = Path.Combine(Path.GetTempPath(), "charter-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var planPath = Path.Combine(root, "plan.charter.md");
        File.WriteAllText(planPath, markdown);
        var sidecars = Path.Combine(root, "sidecars");
        Directory.CreateDirectory(sidecars);

        return new Fixture { Root = root, PlanPath = planPath, SidecarDirectory = sidecars };
    }

    private static ReviewServer Start(Fixture fixture)
    {
        var session = ReviewSession.Create(fixture.PlanPath);
        var server = ReviewServer.Start(session, new ReviewServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            SidecarDirectory = fixture.SidecarDirectory,

            // Shorten the keep-alive beat, the same INTERNAL test seam PlanWatchStreamTests uses. The beat is
            // also the safety net that delivers a change the OS watcher misses, so a 15s production beat would
            // make an otherwise-correct test look like a 30s hang.
            EventStreamBeat = TimeSpan.FromMilliseconds(150),
        });
        fixture.Remember(server, session.Key.Value);
        return server;
    }

    private static async Task PostAnswer(ReviewServer server, string key, string questionId, string value)
        => await PostAnswer(server, key, questionId, new[] { value });

    private static async Task PostAnswer(
        ReviewServer server, string key, string questionId, IReadOnlyList<string> values)
    {
        using var client = new HttpClient();
        var uri = new Uri(server.Address, "api/" + Uri.EscapeDataString(key) + "/answers");
        var payload = JsonSerializer.Serialize(new
        {
            questionId,
            mode = "single",
            values,
            target = "human",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", server.Address.GetLeftPart(UriPartial.Authority));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"POST answers returned {(int)response.StatusCode}.");
    }

    private static async Task<string> GetPage(ReviewServer server, string key)
    {
        using var client = new HttpClient();
        var uri = new Uri(server.Address, "?key=" + Uri.EscapeDataString(key));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(uri, cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"GET / returned {(int)response.StatusCode}.");
        return await response.Content.ReadAsStringAsync(cts.Token);
    }

    /// <summary>Peek then ack — the two halves of what a real drain does to the answer queue.</summary>
    private static async Task DrainAndAck(ReviewServer server, string key)
    {
        using var client = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var peekUri = new Uri(server.Address, "api/answers?key=" + Uri.EscapeDataString(key));
        using var peek = await client.GetAsync(peekUri, cts.Token);
        Assert.True(peek.IsSuccessStatusCode);
        var body = await peek.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(body);
        var count = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.GetArrayLength()
            : 1;

        var ackUri = new Uri(
            server.Address, "api/" + Uri.EscapeDataString(key) + "/answers/ack?count=" + count);
        using var request = new HttpRequestMessage(HttpMethod.Post, ackUri);
        request.Headers.Add("Origin", server.Address.GetLeftPart(UriPartial.Authority));
        using var ack = await client.SendAsync(request, cts.Token);
        Assert.True(ack.IsSuccessStatusCode, $"POST answers/ack returned {(int)ack.StatusCode}.");
    }
}
