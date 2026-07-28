using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Charter.Core;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Charter #78 — <c>GET /api/annotations</c> (the panel's list) and <c>/api/poll</c> (the drain) must never
/// report different <c>sourceLine</c>/<c>anchorStatus</c> for the SAME annotation at the same moment.
///
/// The drain re-resolves at hand-off time (#49) so an agent is never handed a stale line; the list route used to
/// emit the value captured at SUBMIT time. Against a replaced plan the two answered
/// <c>sourceLine: 1, anchorStatus: "resolved"</c> and <c>null, "orphaned"</c> respectively — and the list route
/// is public, key-gated, and an obvious thing for an agent to reach for, so #49's failure came back through a
/// route documented as safe.
///
/// These tests bind the two routes together: whatever the drain would say about an annotation, the list says
/// too. Nothing bound them before, which is exactly how they drifted.
/// </summary>
[Trait("Category", "AnnotationApi")]
public class PanelDrainParityTests
{
    private const string TargetParagraph = "The reviewer annotates this distinctive target paragraph.";

    // line 1: heading / 3: overview / 5: TargetParagraph
    private const string OriginalPlan =
        "# Parity Plan\n" +
        "\n" +
        "An overview paragraph introducing the plan under review.\n" +
        "\n" +
        TargetParagraph + "\n";

    // A different document at the same path: not one of the original's blocks survives.
    private const string ReplacementPlan =
        "# Tenant onboarding\n" +
        "\n" +
        "Every tenant gets an isolated schema provisioned at signup time.\n";

    // The same blocks, pushed down by an insertion above them: the anchor still resolves, at a NEW line.
    private const string ShiftedPlan =
        "# Parity Plan\n" +
        "\n" +
        "An overview paragraph introducing the plan under review.\n" +
        "\n" +
        "A newly inserted paragraph.\n" +
        "\n" +
        TargetParagraph + "\n";

    [Fact]
    public async Task ReplacedPlan_PanelAndDrainAgree_BothOrphaned()
    {
        // The #78 reproduction, exactly: annotate, replace the plan under the session, then read BOTH routes.
        await RunAsync(ReplacementPlan, async (listed, drained) =>
        {
            Assert.Equal("orphaned", GetString(listed, "anchorStatus"));
            Assert.Equal(JsonValueKind.Null, Property(listed, "sourceLine").ValueKind);

            AssertAgree(listed, drained);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task EditAboveTheBlock_PanelAndDrainAgree_BothOnTheNewLine()
    {
        // The other direction, and the one that proves the fix is not just "always say orphaned": an edit that
        // MOVES the block must move the line the panel reports too.
        await RunAsync(ShiftedPlan, async (listed, drained) =>
        {
            Assert.Equal("resolved", GetString(listed, "anchorStatus"));
            Assert.Equal(7, GetInt(listed, "sourceLine"));

            AssertAgree(listed, drained);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task UntouchedPlan_PanelAndDrainAgree_OnTheOriginalLine()
    {
        await RunAsync(OriginalPlan, async (listed, drained) =>
        {
            Assert.Equal("resolved", GetString(listed, "anchorStatus"));
            Assert.Equal(5, GetInt(listed, "sourceLine"));

            AssertAgree(listed, drained);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task UnreadablePlan_PanelSaysOrphaned_NotAStaleButConfidentLine()
    {
        // An unreadable plan resolves no anchor honestly, so the drain reports the whole batch as orphaned. The
        // list must reach the same verdict rather than falling back on the submit-time snapshot — a line nothing
        // can verify is the one thing worse than no line.
        var planPath = WriteTempPlan(OriginalPlan);
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var anchorId = AnchorOf(OriginalPlan, TargetParagraph);
            await PostAnnotationAsync(client, server.Address, session.Key.Value, anchorId);

            File.Delete(planPath);

            var listed = await ListOneAsync(client, server.Address, session.Key.Value, anchorId);
            Assert.Equal("orphaned", GetString(listed, "anchorStatus"));
            Assert.Equal(JsonValueKind.Null, Property(listed, "sourceLine").ValueKind);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    /// <summary>
    /// Annotate <see cref="OriginalPlan"/>, rewrite the plan to <paramref name="rewritten"/>, then read the SAME
    /// annotation from the list route and from the drain — in that order, so the non-destructive list is taken
    /// while the annotation is genuinely still pending, exactly as the panel takes it.
    /// </summary>
    private static async Task RunAsync(string rewritten, Func<JsonElement, JsonElement, Task> assert)
    {
        var planPath = WriteTempPlan(OriginalPlan);
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var anchorId = AnchorOf(OriginalPlan, TargetParagraph);
            await PostAnnotationAsync(client, server.Address, session.Key.Value, anchorId);

            await File.WriteAllTextAsync(planPath, rewritten);

            var listed = await ListOneAsync(client, server.Address, session.Key.Value, anchorId);
            var drained = await DrainOneAsync(client, server.Address, session.Key.Value, anchorId);
            await assert(listed, drained);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    /// <summary>The parity assertion itself: one field, one meaning, whichever route served it.</summary>
    private static void AssertAgree(JsonElement listed, JsonElement drained)
    {
        Assert.Equal(GetString(drained, "anchorStatus"), GetString(listed, "anchorStatus"));
        Assert.Equal(Property(drained, "sourceLine").ValueKind, Property(listed, "sourceLine").ValueKind);
        if (Property(drained, "sourceLine").ValueKind != JsonValueKind.Null)
        {
            Assert.Equal(GetInt(drained, "sourceLine"), GetInt(listed, "sourceLine"));
        }
    }

    private static string AnchorOf(string markdown, string marker)
        => BlockDocument.Parse(markdown).Blocks
            .Single(b => b.RawContent.Contains(marker, StringComparison.Ordinal)).Id;

    private static async Task PostAnnotationAsync(HttpClient client, Uri address, string key, string anchorId)
    {
        var uri = new Uri(address, $"api/{Uri.EscapeDataString(key)}/prompts");
        var payload = JsonSerializer.Serialize(new
        {
            kind = "element",
            anchorId,
            note = "A note whose target may move or vanish.",
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", address.GetLeftPart(UriPartial.Authority));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"the annotation should be accepted, got {(int)response.StatusCode}.");
    }

    private static Task<JsonElement> ListOneAsync(HttpClient client, Uri address, string key, string anchorId)
        => FetchOneAsync(client, new Uri(address, "api/annotations?key=" + Uri.EscapeDataString(key)), anchorId);

    private static Task<JsonElement> DrainOneAsync(HttpClient client, Uri address, string key, string anchorId)
        => FetchOneAsync(client, new Uri(address, "api/poll?key=" + Uri.EscapeDataString(key)), anchorId);

    private static async Task<JsonElement> FetchOneAsync(HttpClient client, Uri uri, string anchorId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync(uri, cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"GET {uri.AbsolutePath} should succeed, got {(int)response.StatusCode}.");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (string.Equals(GetString(item, "anchorId"), anchorId, StringComparison.Ordinal))
            {
                return item.Clone();
            }
        }

        throw new Xunit.Sdk.XunitException($"{uri.AbsolutePath} did not report the submitted annotation.");
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new Xunit.Sdk.XunitException($"The annotation should carry a {name} field.");
    }

    private static string? GetString(JsonElement element, string name) => Property(element, name).GetString();

    private static int GetInt(JsonElement element, string name) => Property(element, name).GetInt32();

    private static string WriteTempPlan(string markdown)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "charter-parity-" + Guid.NewGuid().ToString("N") + ".charter.md");
        File.WriteAllText(path, markdown);
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
            // Best-effort cleanup.
        }
    }
}
