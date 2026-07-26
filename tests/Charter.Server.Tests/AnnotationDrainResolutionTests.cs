using System;
using System.IO;
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
/// Charter #49 — an annotation's <c>sourceLine</c> must be resolved at DRAIN time, against the plan as it is
/// at handoff, not frozen at submit time. A submit-time snapshot goes stale the moment anything above the
/// annotated block changes line count (an agent edit, or <c>poll --apply</c> folding an answer into a
/// multi-line <c>:::question</c>), and the agent then edits the WRONG line — a silent misdirection in exactly
/// the seam Architecture B exists to enable.
///
/// The drained wire shape therefore carries an explicit <c>anchorStatus</c> alongside <c>sourceLine</c>:
/// <c>resolved</c> with a line, or <c>orphaned</c> with <c>sourceLine: null</c> when the anchor no longer
/// exists — so the agent is told "the block you commented on has changed" (with the stored <c>quote</c> /
/// <c>nodeId</c> as the recovery hint) instead of being handed a confidently wrong number.
/// </summary>
[Trait("Category", "AnnotationApi")]
public class AnnotationDrainResolutionTests
{
    private const string TargetParagraph = "The reviewer annotates this distinctive target paragraph.";

    // line 1: # Plan / 3: overview / 5: TargetParagraph
    private const string PlanMarkdown =
        "# Drain Resolution Plan\n" +
        "\n" +
        "An overview paragraph introducing the plan under review.\n" +
        "\n" +
        TargetParagraph + "\n";

    // ---- 1. The line is re-resolved at drain, against the CURRENT file ------------------------------------

    [Fact]
    public async Task Drain_AfterAnEditThatShiftsTheBlockDown_ReportsTheCurrentLine_NotTheSubmitTimeLine()
    {
        var planPath = WriteTempPlan(PlanMarkdown);
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var anchorId = AnchorOf(PlanMarkdown, TargetParagraph);
            var submitTimeLine = SourceMap.Build(PlanMarkdown).LineForAnchor(anchorId);
            Assert.Equal(5, submitTimeLine);

            await PostAnnotationAsync(client, server.Address, session.Key.Value, anchorId, "Clarify this.");

            // The agent edits the plan ABOVE the annotated block: two extra lines, so the target moves 5 -> 7.
            // Its own content is untouched, so the anchor is unchanged — only its LINE moved.
            const string edited =
                "# Drain Resolution Plan\n" +
                "\n" +
                "An overview paragraph introducing the plan under review.\n" +
                "\n" +
                "A newly inserted paragraph.\n" +
                "\n" +
                TargetParagraph + "\n";
            await File.WriteAllTextAsync(planPath, edited);
            Assert.Equal(7, SourceMap.Build(edited).LineForAnchor(anchorId));

            var drained = await DrainOneAsync(client, server.Address, session.Key.Value, anchorId);

            // The teeth: the drained line is the CURRENT one (7), not the submit-time snapshot (5).
            Assert.Equal(7, GetInt(drained, "sourceLine"));
            Assert.Equal("resolved", GetString(drained, "anchorStatus"));
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- 2. An anchor that no longer resolves drains as an EXPLICIT orphan --------------------------------

    [Fact]
    public async Task Drain_AfterTheAnnotatedBlockItselfChanged_ReportsNullLine_AndOrphanedStatus_KeepingTheQuote()
    {
        var planPath = WriteTempPlan(PlanMarkdown);
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var anchorId = AnchorOf(PlanMarkdown, TargetParagraph);
            const string quote = "distinctive target paragraph";

            await PostAnnotationAsync(
                client, server.Address, session.Key.Value, anchorId, "Tighten this sentence.",
                kind: "text-range", quote: quote);

            // The annotated block's OWN content is rewritten, so its content-derived anchor ceases to exist.
            const string edited =
                "# Drain Resolution Plan\n" +
                "\n" +
                "An overview paragraph introducing the plan under review.\n" +
                "\n" +
                "The reviewer annotates this REWORDED target paragraph.\n";
            await File.WriteAllTextAsync(planPath, edited);
            Assert.Null(SourceMap.Build(edited).LineForAnchor(anchorId));

            var drained = await DrainOneAsync(client, server.Address, session.Key.Value, anchorId);

            // Explicit on the wire: no line, and a named orphan status — not a confidently wrong number.
            Assert.Equal(JsonValueKind.Null, Property(drained, "sourceLine").ValueKind);
            Assert.Equal("orphaned", GetString(drained, "anchorStatus"));

            // The recovery hint travels with it: the reviewer's own words survive the drain.
            Assert.Equal(quote, GetString(drained, "quote"));
            Assert.Equal("Tighten this sentence.", GetString(drained, "note"));
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- 3. Sidecar rehydrate resolves against the file as it is NOW, not as it was ------------------------

    [Fact]
    public async Task Drain_AfterSidecarRehydrate_AcrossAnExternalFileChange_ReportsTheCurrentLine()
    {
        var sidecarDir = NewTempDir();
        var planPath = WriteTempPlan(PlanMarkdown);
        try
        {
            var session = ReviewSession.Create(planPath);
            var options = new ReviewServerOptions
            {
                BindAddress = IPAddress.Loopback,
                Port = 0,
                SidecarDirectory = sidecarDir,
            };

            var anchorId = AnchorOf(PlanMarkdown, TargetParagraph);

            // Server #1: the reviewer annotates, then the review process goes away before any agent drained it.
            using (var server1 = ReviewServer.Start(session, options))
            {
                using var client = new HttpClient();
                await PostAnnotationAsync(client, server1.Address, session.Key.Value, anchorId, "Persisted note.");
            }

            // The plan changes WHILE THE SERVER IS DOWN — the case a persisted submit-time line cannot see.
            const string edited =
                "# Drain Resolution Plan\n" +
                "\n" +
                "An overview paragraph introducing the plan under review.\n" +
                "\n" +
                "A paragraph added while the review server was down.\n" +
                "\n" +
                "And another one.\n" +
                "\n" +
                TargetParagraph + "\n";
            await File.WriteAllTextAsync(planPath, edited);
            Assert.Equal(9, SourceMap.Build(edited).LineForAnchor(anchorId));

            // Server #2 rehydrates the queued annotation and drains it — resolved against the CURRENT file.
            using (var server2 = ReviewServer.Start(session, options))
            {
                using var client = new HttpClient();
                var drained = await DrainOneAsync(client, server2.Address, session.Key.Value, anchorId);
                Assert.Equal(9, GetInt(drained, "sourceLine"));
                Assert.Equal("resolved", GetString(drained, "anchorStatus"));
            }
        }
        finally
        {
            TryDeleteDir(sidecarDir);
            TryDelete(planPath);
        }
    }

    // ---- 4. The unchanged case still resolves (no regression) ---------------------------------------------

    [Fact]
    public async Task Drain_WithNoIntervalEdit_StillReportsTheAnchorsLine_AsResolved()
    {
        var planPath = WriteTempPlan(PlanMarkdown);
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var anchorId = AnchorOf(PlanMarkdown, TargetParagraph);
            await PostAnnotationAsync(client, server.Address, session.Key.Value, anchorId, "No edit happened.");

            var drained = await DrainOneAsync(client, server.Address, session.Key.Value, anchorId);
            Assert.Equal(5, GetInt(drained, "sourceLine"));
            Assert.Equal("resolved", GetString(drained, "anchorStatus"));
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- 5. The pre-drain panel list still sees the submit-time resolution ---------------------------------

    [Fact]
    public async Task PendingList_KeepsTheSubmitTimeLine_ForTheInPageReviewPanel()
    {
        var planPath = WriteTempPlan(PlanMarkdown);
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var anchorId = AnchorOf(PlanMarkdown, TargetParagraph);
            await PostAnnotationAsync(client, server.Address, session.Key.Value, anchorId, "Panel note.");

            // GET /api/annotations is the reviewer's PRE-DRAIN queue view — it reports the line as resolved when
            // the note was taken, which is what the reviewer is looking at. Drain-time re-resolution is for the
            // agent-facing handoff, and must not have disturbed this read.
            var listUri = new Uri(server.Address, "api/annotations?key=" + Uri.EscapeDataString(session.Key.Value));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await client.GetAsync(listUri, cts.Token);
            Assert.True(response.IsSuccessStatusCode);

            using var listed = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            var entry = listed.RootElement[0];
            Assert.Equal(5, GetInt(entry, "sourceLine"));
            Assert.Equal("resolved", GetString(entry, "anchorStatus"));
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private static string AnchorOf(string markdown, string marker)
        => BlockDocument.Parse(markdown).Blocks
            .Single(b => b.RawContent.Contains(marker, StringComparison.Ordinal)).Id;

    private static async Task PostAnnotationAsync(
        HttpClient client,
        Uri address,
        string key,
        string anchorId,
        string note,
        string kind = "element",
        string? quote = null)
    {
        var promptsUri = new Uri(address, $"api/{Uri.EscapeDataString(key)}/prompts");
        var payload = JsonSerializer.Serialize(new { kind, anchorId, note, quote });
        using var request = new HttpRequestMessage(HttpMethod.Post, promptsUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", address.GetLeftPart(UriPartial.Authority));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, cts.Token);
        Assert.True(
            response.IsSuccessStatusCode,
            $"POST /api/{{key}}/prompts should accept the annotation, got {(int)response.StatusCode}.");
    }

    private static async Task<JsonElement> DrainOneAsync(HttpClient client, Uri address, string key, string anchorId)
    {
        var pollUri = new Uri(address, "api/poll?key=" + Uri.EscapeDataString(key));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(pollUri, cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"GET /api/poll should drain, got {(int)response.StatusCode}.");

        using var drained = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
        foreach (var item in drained.RootElement.EnumerateArray())
        {
            if (string.Equals(GetString(item, "anchorId"), anchorId, StringComparison.Ordinal))
            {
                return item.Clone();
            }
        }

        throw new Xunit.Sdk.XunitException("The submitted annotation was not present in the drained batch.");
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
            Path.GetTempPath(), "charter-drain-plan-" + Guid.NewGuid().ToString("N") + ".charter.md");
        File.WriteAllText(path, markdown);
        return path;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-drain-" + Guid.NewGuid().ToString("N"));
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
            // A leaked temp file is harmless if the OS still holds a handle during a slow server dispose.
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
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
