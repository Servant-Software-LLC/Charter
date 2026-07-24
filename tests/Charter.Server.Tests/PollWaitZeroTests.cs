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
/// Tests the additive <c>/api/poll?wait=0</c> immediate-drain mode on <see cref="ReviewServer"/>. With
/// <c>wait=0</c> the server SKIPS the long-poll wait and returns whatever is queued right now — <c>[]</c> fast
/// when empty — which is what <c>charter poll</c> uses for its non-blocking default. The browser SDK never
/// sends the parameter, so the DEFAULT poll still long-polls and every existing annotation test is unchanged.
/// Each test starts a REAL loopback server and drives it over <see cref="HttpClient"/>.
/// </summary>
[Trait("Category", "PollWaitZero")]
public class PollWaitZeroTests
{
    private const string PlanMarkdown =
        "# Wait Zero Plan\n\nAn overview paragraph the reviewer can annotate.\n";

    [Fact]
    public async Task WaitZero_EmptyStore_ReturnsEmptyArray_Fast()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var uri = new Uri(server.Address, "api/poll?key=" + Uri.EscapeDataString(session.Key.Value) + "&wait=0");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var stopwatch = Stopwatch.StartNew();
            using var response = await client.GetAsync(uri, cts.Token);
            stopwatch.Stop();

            Assert.True(response.IsSuccessStatusCode, $"wait=0 poll should return 200, got {(int)response.StatusCode}.");
            var body = await response.Content.ReadAsStringAsync(cts.Token);

            using var doc = JsonDocument.Parse(body);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.Equal(0, doc.RootElement.GetArrayLength());

            // Well under the ~30s PollTimeout: wait=0 must NOT have blocked on the long-poll wait.
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"wait=0 on an empty store should return immediately, took {stopwatch.Elapsed}.");
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task WaitZero_WithQueuedAnnotation_DrainsItImmediately()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            // Enqueue one annotation through the real submit route, then drain it with wait=0.
            await PostAnnotationAsync(client, server.Address, session.Key.Value, "Drain me immediately.");

            var uri = new Uri(server.Address, "api/poll?key=" + Uri.EscapeDataString(session.Key.Value) + "&wait=0");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await client.GetAsync(uri, cts.Token);
            Assert.True(response.IsSuccessStatusCode);

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.Equal(1, doc.RootElement.GetArrayLength());
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task DefaultPoll_EmptyStore_StillLongPolls_DoesNotReturnImmediately()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            // NO wait=0 parameter: the default poll must block (long-poll) on an empty store rather than return
            // an empty array at once. Prove it is still pending after a second, then cancel it (avoiding the
            // full ~30s timeout) — deterministic, no fixed sleep gating the assertion.
            var uri = new Uri(server.Address, "api/poll?key=" + Uri.EscapeDataString(session.Key.Value));
            using var cts = new CancellationTokenSource();
            var pollTask = client.GetAsync(uri, cts.Token);

            var settled = await Task.WhenAny(pollTask, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.NotSame(pollTask, settled); // the poll did NOT complete within a second — it is long-polling

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pollTask);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    private static async Task PostAnnotationAsync(HttpClient client, Uri address, string key, string note)
    {
        var promptsUri = new Uri(address, $"api/{Uri.EscapeDataString(key)}/prompts");
        var payload = JsonSerializer.Serialize(new { kind = "element", anchorId = "b00000000000000000000", note });
        using var request = new HttpRequestMessage(HttpMethod.Post, promptsUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", address.GetLeftPart(UriPartial.Authority));

        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"seed POST should succeed, got {(int)response.StatusCode}.");
    }

    private static string WriteTempPlan()
    {
        var path = Path.Combine(Path.GetTempPath(), "charter-waitzero-plan-" + Guid.NewGuid().ToString("N") + ".mdx");
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
