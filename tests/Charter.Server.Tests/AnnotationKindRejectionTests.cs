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
/// Charter #79 — an annotation <c>kind</c> must never DEGRADE SILENTLY. Posting the camelCase
/// <c>"textRange"</c> (the spelling the skill documented for a while, and the one a default C#/JS enum-name
/// convention produces) used to return <b>HTTP 200</b> and store a whole-<c>element</c> annotation, while the
/// submission's <c>quote</c>/<c>start</c>/<c>end</c> survived on the record contradicting the stored kind — so
/// anything downstream branching on <c>kind</c> did the wrong thing with no signal anywhere.
///
/// The contract these tests pin: an unrecognised token is REFUSED with 400 naming the three accepted tokens, is
/// never enqueued, and the three valid tokens still round-trip unchanged. The camelCase spelling is deliberately
/// NOT an alias — a second legal spelling would fork the vocabulary the SDK emits and the skills document,
/// whereas one 400 fixes the client permanently.
/// </summary>
[Trait("Category", "AnnotationApi")]
public class AnnotationKindRejectionTests
{
    private const string PlanMarkdown =
        "# Kind Rejection Plan\n\nA paragraph a reviewer might annotate.\n";

    // ---- 1. The strict parse itself ----------------------------------------------------------------------

    [Theory]
    [InlineData("element", AnnotationKind.Element)]
    [InlineData("text-range", AnnotationKind.TextRange)]
    [InlineData("diagram-node", AnnotationKind.DiagramNode)]
    [InlineData("TEXT-RANGE", AnnotationKind.TextRange)]
    [InlineData("Diagram-Node", AnnotationKind.DiagramNode)]
    public void TryParseKind_AcceptsEverySdkToken_CaseInsensitively(string token, AnnotationKind expected)
    {
        Assert.True(AnnotationApi.TryParseKind(token, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseKind_TreatsAnAbsentTokenAsTheWholeBlock(string? token)
    {
        // Unspecified is not "wrong": `{ anchorId, note }` is the documented block-level submission shape, and
        // the SDK's own element gesture means exactly this. Only a token that SAYS something unrecognisable is
        // a client bug.
        Assert.True(AnnotationApi.TryParseKind(token, out var parsed));
        Assert.Equal(AnnotationKind.Element, parsed);
    }

    [Theory]
    [InlineData("textRange")]      // the #79 headline: camelCase, silently downgraded to element
    [InlineData("diagramNode")]
    [InlineData("text_range")]
    [InlineData("TextRange")]
    [InlineData("not-a-real-kind")]
    public void TryParseKind_RefusesAnUnrecognisedToken_NeverCoercingItToElement(string token)
        => Assert.False(
            AnnotationApi.TryParseKind(token, out _),
            $"'{token}' is not a Charter wire token and must be refused, not read as 'element'.");

    [Fact]
    public void KindTokens_AreExactlyTheThreeWireTokens_SoARejectionCanQuoteThem()
        => Assert.Equal(new[] { "element", "text-range", "diagram-node" }, AnnotationApi.KindTokens);

    // ---- 2. The HTTP ingress -----------------------------------------------------------------------------

    [Fact]
    public async Task Post_CamelCaseKind_Is400_NamesTheAcceptedTokens_AndEnqueuesNothing()
    {
        await WithServerAsync(async (server, session, client, anchorId) =>
        {
            using var response = await PostAsync(client, server, session, anchorId, kind: "textRange");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            // The message must TELL the client what would have worked — that is the whole difference between a
            // 400 and the silent coercion it replaces.
            var body = await response.Content.ReadAsStringAsync();
            using var error = JsonDocument.Parse(body);
            var message = error.RootElement.GetProperty("error").GetString();
            Assert.NotNull(message);
            Assert.Contains("textRange", message, StringComparison.Ordinal);
            foreach (var token in AnnotationApi.KindTokens)
            {
                Assert.Contains(token, message, StringComparison.Ordinal);
            }

            // Refused means REFUSED: nothing reached the pre-drain queue, so there is no half-accepted
            // annotation for the reviewer or the agent to reconcile later.
            Assert.Equal(0, await ListCountAsync(client, server, session));
        });
    }

    [Fact]
    public async Task Post_CamelCaseTextRange_IsNotSilentlyStoredAsAnElementAnnotation()
    {
        // The exact #79 shape: a text-range payload (quote + offsets) whose kind spelling is wrong. The old
        // behaviour kept the fidelity fields AND rewrote the kind to element, producing a record that
        // contradicted itself. It must not be stored at all.
        await WithServerAsync(async (server, session, client, anchorId) =>
        {
            var payload = JsonSerializer.Serialize(new
            {
                kind = "textRange",
                anchorId,
                note = "Tighten this sentence.",
                quote = "a reviewer might annotate",
                start = 2,
                end = 27,
            });

            using var response = await PostRawAsync(client, server, session, payload);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, await ListCountAsync(client, server, session));
        });
    }

    [Theory]
    [InlineData("element")]
    [InlineData("text-range")]
    [InlineData("diagram-node")]
    public async Task Post_EveryValidToken_StillRoundTrips(string kind)
    {
        await WithServerAsync(async (server, session, client, anchorId) =>
        {
            using var response = await PostAsync(client, server, session, anchorId, kind);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var accepted = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(kind, accepted.RootElement.GetProperty("kind").GetString());
        });
    }

    [Fact]
    public async Task Post_WithNoKindAtAll_StillEnqueuesAWholeBlockAnnotation()
    {
        await WithServerAsync(async (server, session, client, anchorId) =>
        {
            var payload = JsonSerializer.Serialize(new { anchorId, note = "A block-level submission." });

            using var response = await PostRawAsync(client, server, session, payload);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var accepted = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("element", accepted.RootElement.GetProperty("kind").GetString());
        });
    }

    // ---- Helpers -----------------------------------------------------------------------------------------

    private static async Task WithServerAsync(
        Func<ReviewServer, ReviewSession, HttpClient, string, Task> body)
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-kind-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, PlanMarkdown);
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var anchorId = BlockDocument.Parse(PlanMarkdown).Blocks
                .Single(b => b.RawContent.Contains("might annotate", StringComparison.Ordinal)).Id;

            await body(server, session, client, anchorId);
        }
        finally
        {
            try
            {
                File.Delete(planPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp plan.
            }
        }
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client, ReviewServer server, ReviewSession session, string anchorId, string kind)
        => PostRawAsync(
            client,
            server,
            session,
            JsonSerializer.Serialize(new { kind, anchorId, note = "A note for the kind contract." }));

    private static async Task<HttpResponseMessage> PostRawAsync(
        HttpClient client, ReviewServer server, ReviewSession session, string payload)
    {
        var uri = new Uri(server.Address, $"api/{Uri.EscapeDataString(session.Key.Value)}/prompts");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", server.Address.GetLeftPart(UriPartial.Authority));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await client.SendAsync(request, cts.Token);
    }

    /// <summary>How many annotations the pre-drain queue holds — the non-destructive check that a refusal refused.</summary>
    private static async Task<int> ListCountAsync(HttpClient client, ReviewServer server, ReviewSession session)
    {
        var uri = new Uri(
            server.Address, "api/annotations?key=" + Uri.EscapeDataString(session.Key.Value));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(uri, cts.Token);
        Assert.True(response.IsSuccessStatusCode);

        using var listed = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
        return listed.RootElement.GetArrayLength();
    }
}
