using System;
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
/// Integration tests for <see cref="ReviewClient"/> — the loopback client <c>charter poll</c> drives — against
/// a REAL <see cref="ReviewServer"/>. Covers proving liveness (via <c>GET /api/sessions</c>, including the
/// source-path match), the wrong-key and disposed-server negatives, and the annotation/answer drains returning
/// queued items. Deadlines are bounded; nothing sleeps.
/// </summary>
[Trait("Category", "ReviewClient")]
public class ReviewClientTests
{
    private const string PlanMarkdown =
        "# Review Client Plan\n\nAn overview paragraph the reviewer annotates.\n";

    [Fact]
    public async Task Probe_LiveServer_MatchingSource_ReturnsSession()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new ReviewClient(server.Address, session.Key.Value);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var live = await client.ProbeAsync(session.SourcePath, cts.Token);

            Assert.NotNull(live);
            Assert.Equal(session.SourcePath, live!.SourcePath);
            Assert.Equal(Path.GetFileName(session.SourcePath), live.SourceFile);
            Assert.Equal(server.Address.GetLeftPart(UriPartial.Authority) + "/", live.Address);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Probe_FromCapabilityUrl_ReturnsSession()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

            using var client = ReviewClient.FromCapabilityUrl($"{server.Address}?key={session.Key.Value}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var live = await client.ProbeAsync(expectedSourcePath: null, cts.Token);

            Assert.NotNull(live);
            Assert.Equal(session.SourcePath, live!.SourcePath);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Probe_WrongKey_IsNotLive()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new ReviewClient(server.Address, "not-the-real-key");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Assert.Null(await client.ProbeAsync(session.SourcePath, cts.Token));
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Probe_SourcePathMismatch_IsNotLive()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new ReviewClient(server.Address, session.Key.Value);

            // The descriptor claimed a DIFFERENT source than the live server serves (a recycled port): reject.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var mismatch = Path.Combine(Path.GetTempPath(), "charter-some-other-plan.mdx");
            Assert.Null(await client.ProbeAsync(mismatch, cts.Token));
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Probe_DisposedServer_IsNotLive()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            Uri address;
            using (var server = ReviewServer.Start(
                       session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 }))
            {
                address = server.Address;
            }

            // The server (and its listener) are disposed: a connection to the freed port is refused.
            using var client = new ReviewClient(address, session.Key.Value);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Assert.Null(await client.ProbeAsync(session.SourcePath, cts.Token));
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Drains_ReturnQueuedAnnotationAndAnswer()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var seed = new HttpClient();

            await PostAnnotationAsync(seed, server.Address, session.Key.Value, "Please clarify.");
            await PostAnswerAsync(seed, server.Address, session.Key.Value, "q-1", new[] { "A" });

            using var client = new ReviewClient(server.Address, session.Key.Value);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // The non-blocking (wait=0) annotation drain returns the queued annotation.
            var annotations = await client.DrainAnnotationsAsync(wait: false, cts.Token);
            Assert.Single(annotations);
            Assert.Equal("Please clarify.", annotations[0].Note);

            var answers = await client.DrainAnswersAsync(cts.Token);
            Assert.Single(answers);
            Assert.Equal("q-1", answers[0].QuestionId);
            Assert.Equal(new[] { "A" }, answers[0].Values);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public void FromCapabilityUrl_MissingKey_Throws()
        => Assert.Throws<FormatException>(() => ReviewClient.FromCapabilityUrl("http://127.0.0.1:5000/"));

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
        Assert.True(response.IsSuccessStatusCode);
    }

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
        Assert.True(response.IsSuccessStatusCode);
    }

    private static string WriteTempPlan()
    {
        var path = Path.Combine(Path.GetTempPath(), "charter-reviewclient-plan-" + Guid.NewGuid().ToString("N") + ".mdx");
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
