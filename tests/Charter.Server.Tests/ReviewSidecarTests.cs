using System;
using System.Collections.Generic;
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
/// Tests for the server-owned durability sidecar (§1.6) and the peek/ack answer contract it underpins. The
/// load-bearing case is <see cref="Answer_SurvivesServerRestart_ViaSidecar"/>: an answer submitted to one
/// server process rehydrates into a fresh server on the same session/sidecar — proving a
/// <c>charter review</c> crash before drain loses nothing (the durable half the destructive drain shipped
/// without). Also covers the on-disk sidecar round-trip (write/rehydrate/delete-when-empty) and the HTTP
/// peek-is-non-destructive / ack-removes semantics.
/// </summary>
[Trait("Category", "ReviewSidecar")]
public class ReviewSidecarTests
{
    private const string PlanMarkdown =
        "# Sidecar Plan\n\n:::question\n" +
        "{\"id\":\"q-1\",\"title\":\"Pick one\",\"mode\":\"single\",\"options\":[\"A\",\"B\"],\"target\":\"human\"}\n:::\n";

    // ---- Durability: an answer survives a server restart via the sidecar (load-bearing) ------------------

    [Fact]
    public async Task Answer_SurvivesServerRestart_ViaSidecar()
    {
        var sidecarDir = NewTempDir();
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            var options = new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0, SidecarDirectory = sidecarDir };

            // Server #1: the reviewer submits an answer, which the server persists to its sidecar before the 200.
            using (var server1 = ReviewServer.Start(session, options))
            {
                using var client = new HttpClient();
                await PostAnswerAsync(client, server1.Address, session.Key.Value, "q-1", new[] { "A" });
            } // dispose == the review process exits (or crashes) BEFORE any agent drained the answer.

            var sidecarPath = ReviewSidecar.PathForPlan(sidecarDir, planPath);
            Assert.True(File.Exists(sidecarPath), "the submit should have persisted a durable sidecar.");

            // Server #2: same session, same sidecar directory — it must REHYDRATE the queued answer on start.
            using (var server2 = ReviewServer.Start(session, options))
            {
                using var client = new HttpClient();
                var answers = await PeekAnswersAsync(client, server2.Address, session.Key.Value);
                var answer = Assert.Single(answers);
                Assert.Equal("q-1", answer.QuestionId);
                Assert.Equal(new[] { "A" }, answer.Values);
            }
        }
        finally
        {
            TryDeleteDir(sidecarDir);
            TryDelete(planPath);
        }
    }

    // ---- HTTP contract: peek is non-destructive, ack removes --------------------------------------------

    [Fact]
    public async Task Peek_IsNonDestructive_ThenAck_RemovesTheAppliedPrefix()
    {
        var sidecarDir = NewTempDir();
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0, SidecarDirectory = sidecarDir });
            using var client = new HttpClient();

            await PostAnswerAsync(client, server.Address, session.Key.Value, "q-1", new[] { "A" });

            // Two peeks return the SAME answer — a peek reports without removing (report-don't-remove, §1.6).
            Assert.Single(await PeekAnswersAsync(client, server.Address, session.Key.Value));
            Assert.Single(await PeekAnswersAsync(client, server.Address, session.Key.Value));

            // Ack the front 1 (the applied prefix): now the store — and the sidecar — are empty.
            using (var ack = await AckAsync(client, server.Address, session.Key.Value, count: 1))
            {
                Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
            }

            Assert.Empty(await PeekAnswersAsync(client, server.Address, session.Key.Value));
            var sidecarPath = ReviewSidecar.PathForPlan(sidecarDir, planPath);
            Assert.False(File.Exists(sidecarPath), "a fully-drained session deletes its sidecar (no husk).");
        }
        finally
        {
            TryDeleteDir(sidecarDir);
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Ack_WithForeignOrigin_IsRejected_CsrfSameOrigin()
    {
        var sidecarDir = NewTempDir();
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0, SidecarDirectory = sidecarDir });
            using var client = new HttpClient();

            await PostAnswerAsync(client, server.Address, session.Key.Value, "q-1", new[] { "A" });

            // A commit carrying a FOREIGN Origin is refused even with a valid key (CSRF on the state-changing
            // ack), so the answer is NOT removed.
            var ackUri = new Uri(server.Address, $"api/{Uri.EscapeDataString(session.Key.Value)}/answers/ack?count=1");
            using var request = new HttpRequestMessage(HttpMethod.Post, ackUri) { Content = new StringContent(string.Empty) };
            request.Headers.TryAddWithoutValidation("Origin", "https://charter-review.attacker.example");
            using var response = await client.SendAsync(request);
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

            Assert.Single(await PeekAnswersAsync(client, server.Address, session.Key.Value));
        }
        finally
        {
            TryDeleteDir(sidecarDir);
            TryDelete(planPath);
        }
    }

    // ---- The on-disk sidecar file round-trip ------------------------------------------------------------

    [Fact]
    public void Rehydrate_MissingSidecar_IsEmpty_NeverThrows()
    {
        var state = ReviewSidecar.Rehydrate(Path.Combine(Path.GetTempPath(), "charter-no-such-" + Guid.NewGuid().ToString("N") + ".review.json"));
        Assert.Empty(state.Annotations);
        Assert.Empty(state.Answers);
    }

    [Fact]
    public void WriteState_ThenRehydrate_RoundTripsAnswers_AndDeletesWhenEmpty()
    {
        var dir = NewTempDir();
        try
        {
            var planPath = Path.Combine(dir, "plan.charter.md");
            var sidecarPath = ReviewSidecar.PathForPlan(dir, planPath);
            var answers = new[] { new Answer("q-1", "single-select", new[] { "A" }, "human") };

            ReviewSidecar.WriteState(sidecarPath, planPath, Array.Empty<Annotation>(), answers);
            var restored = ReviewSidecar.Rehydrate(sidecarPath);
            var answer = Assert.Single(restored.Answers);
            Assert.Equal("q-1", answer.QuestionId);
            Assert.Equal(new[] { "A" }, answer.Values);

            // Persisting an empty state deletes the file rather than leaving an empty husk.
            ReviewSidecar.WriteState(sidecarPath, planPath, Array.Empty<Annotation>(), Array.Empty<Answer>());
            Assert.False(File.Exists(sidecarPath));
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    private static async Task PostAnswerAsync(HttpClient client, Uri address, string key, string questionId, string[] values)
    {
        var answersUri = new Uri(address, $"api/{Uri.EscapeDataString(key)}/answers");
        var payload = JsonSerializer.Serialize(new { questionId, mode = "single", values, target = "human" });
        using var request = new HttpRequestMessage(HttpMethod.Post, answersUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", address.GetLeftPart(UriPartial.Authority));
        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"seed answer POST should succeed, got {(int)response.StatusCode}.");
    }

    private static async Task<List<Answer>> PeekAnswersAsync(HttpClient client, Uri address, string key)
    {
        var uri = new Uri(address, "api/answers?key=" + Uri.EscapeDataString(key));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(uri, cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"peek should succeed, got {(int)response.StatusCode}.");
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        return JsonSerializer.Deserialize<List<Answer>>(body, AnnotationApi.JsonOptions) ?? new List<Answer>();
    }

    private static async Task<HttpResponseMessage> AckAsync(HttpClient client, Uri address, string key, int count)
    {
        var uri = new Uri(address, $"api/{Uri.EscapeDataString(key)}/answers/ack?count={count}");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(string.Empty) };
        request.Headers.TryAddWithoutValidation("Origin", address.GetLeftPart(UriPartial.Authority));
        return await client.SendAsync(request);
    }

    private static string WriteTempPlan()
    {
        var path = Path.Combine(Path.GetTempPath(), "charter-sidecar-plan-" + Guid.NewGuid().ToString("N") + ".charter.md");
        File.WriteAllText(path, PlanMarkdown);
        return path;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-sidecar-" + Guid.NewGuid().ToString("N"));
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
        catch (IOException)
        {
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
        }
    }
}
