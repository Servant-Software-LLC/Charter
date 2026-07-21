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
/// Integration tests for the annotation HTTP API that extends the wave-2 <see cref="ReviewServer"/> — the
/// server counterpart of the browser annotation SDK's comment-in-place loop. Every test starts a REAL server
/// with <see cref="ReviewServer.Start(ReviewSession, ReviewServerOptions?)"/> on default (loopback,
/// ephemeral-port) options and drives it over <see cref="HttpClient"/>, disposing the server via
/// <c>using</c> and cleaning the temp plan in a <c>finally</c>.
///
/// This is the TDD "red": every test COMPILES against the existing wave-2/wave-3 surface (no new stubs —
/// <see cref="ReviewServer"/>, <see cref="ReviewSession"/>, the <see cref="Annotation"/> record,
/// <see cref="SourceMap"/>, <see cref="HttpClient"/>) and FAILS at runtime because task
/// <c>04-implement-annotation-api</c> has not routed the endpoints yet: the round-trip
/// (<c>POST /api/{key}/prompts</c> then <c>GET /api/poll</c>) does not complete, <c>GET /api/sessions</c> does
/// not return a JSON descriptor, and <c>GET /events</c> does not open a <c>text/event-stream</c> channel.
/// Task 04 turns it green; this task must NOT implement the endpoints.
/// </summary>
/// <remarks>
/// Class trait: <c>[Trait("Category","AnnotationApi")]</c> — distinct from <c>Category=AnnotationStore</c> and
/// wave-2's <c>Category=ReviewServer</c>. The load-bearing M3 acceptance is
/// <see cref="RoundTrip_PostAnnotation_ThenPoll_ResolvesAnchorToSourceLine"/>: it POSTs an annotation, polls,
/// and asserts the returned <c>SourceLine</c> equals <c>SourceMap.Build(plan).LineForAnchor(anchorId)</c> — the
/// content-derived anchor resolved back to the correct 1-based markdown source line. The CSRF invariant is the
/// foreign-<c>Origin</c> POST refusal on the state-changing <c>prompts</c> route.
/// </remarks>
[Trait("Category", "AnnotationApi")]
public class AnnotationApiTests
{
    // A tiny plan with a couple of blocks. The last paragraph carries a distinctive marker so its
    // content-derived block anchor is unambiguous to resolve, and its 1-based source line is deterministic.
    private const string PlanMarkdown =
        "# Charter Annotation API Plan\n" +
        "\n" +
        "An overview paragraph introducing the plan under review.\n" +
        "\n" +
        "The reviewer annotates this distinctive target paragraph for the round-trip.\n";

    private const string AnchorMarker = "distinctive target";

    // ---- 1. Round-trip acceptance (THE M3 acceptance — load-bearing) ------------------------------------

    [Fact]
    public async Task RoundTrip_PostAnnotation_ThenPoll_ResolvesAnchorToSourceLine()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            // Determine a REAL block anchor id (content-derived, from the plan's own block model) and the
            // 1-based markdown source line the server must resolve it back to via SourceMap.LineForAnchor.
            var anchorId = BlockDocument.Parse(PlanMarkdown).Blocks
                .Single(b => b.RawContent.Contains(AnchorMarker, StringComparison.Ordinal)).Id;
            var expectedSourceLine = SourceMap.Build(PlanMarkdown).LineForAnchor(anchorId);
            Assert.NotNull(expectedSourceLine); // sanity: the anchor resolves in the source map

            // POST the annotation (kind = element, the anchorId, a note) — a SAME-ORIGIN request (Origin ==
            // the server's own authority; no foreign Origin) to the state-changing prompts route.
            var promptsUri = new Uri(server.Address, $"api/{Uri.EscapeDataString(session.Key.Value)}/prompts");
            var payload = JsonSerializer.Serialize(new
            {
                kind = "element",
                anchorId,
                note = "Please clarify this target paragraph.",
            });
            using var postRequest = new HttpRequestMessage(HttpMethod.Post, promptsUri)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            postRequest.Headers.TryAddWithoutValidation("Origin", SameOrigin(server.Address));

            using var postResponse = await client.SendAsync(postRequest);
            Assert.True(
                postResponse.IsSuccessStatusCode,
                $"POST /api/{{key}}/prompts should accept the annotation, got {(int)postResponse.StatusCode}.");

            // Long-poll: GET /api/poll?key=... blocks briefly and returns the queued annotation. Bounded
            // deadline (no fixed sleeps) — the store fast-paths because the annotation is already queued.
            var pollUri = new Uri(server.Address, "api/poll?key=" + Uri.EscapeDataString(session.Key.Value));
            using var pollCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var pollResponse = await client.GetAsync(pollUri, pollCts.Token);
            Assert.True(
                pollResponse.IsSuccessStatusCode,
                $"GET /api/poll should return the queued annotation, got {(int)pollResponse.StatusCode}.");

            var pollBody = await pollResponse.Content.ReadAsStringAsync(pollCts.Token);

            // The polled annotation carries SourceLine == SourceMap.Build(plan).LineForAnchor(anchorId):
            // submit -> store -> poll -> anchor-resolved-to-source-line.
            using var polled = JsonDocument.Parse(pollBody);
            var actualSourceLine = FindSourceLineForAnchor(polled.RootElement, anchorId);
            Assert.Equal(expectedSourceLine, actualSourceLine);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- 2. /api/sessions --------------------------------------------------------------------------------

    [Fact]
    public async Task Sessions_ReturnsJsonDescriptorWithKey_AndIsRejectedWithout()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // With the capability key: a JSON session descriptor naming the plan under review.
            var sessionsUri =
                new Uri(server.Address, "api/sessions?key=" + Uri.EscapeDataString(session.Key.Value));
            using var response = await client.GetAsync(sessionsUri, cts.Token);
            Assert.True(
                response.IsSuccessStatusCode,
                $"GET /api/sessions with the key should return the session descriptor, got {(int)response.StatusCode}.");

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            Assert.Contains("json", mediaType, StringComparison.OrdinalIgnoreCase);

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            using var descriptor = JsonDocument.Parse(body);
            Assert.Equal(JsonValueKind.Object, descriptor.RootElement.ValueKind);
            // The descriptor identifies the source under review (its source path / a session id). The temp
            // plan's file name (no separators, so unaffected by JSON escaping) must appear.
            Assert.Contains(Path.GetFileName(session.SourcePath), body, StringComparison.Ordinal);

            // Without the capability key the descriptor is refused (non-200).
            var sessionsNoKeyUri = new Uri(server.Address, "api/sessions");
            using var unauthorized = await client.GetAsync(sessionsNoKeyUri, cts.Token);
            Assert.NotEqual(HttpStatusCode.OK, unauthorized.StatusCode);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- 3. /events SSE reload ---------------------------------------------------------------------------

    [Fact]
    public async Task Events_OpensServerSentEventStream()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            // The push-based live-reload channel: GET /events?key=... opens a text/event-stream response.
            // ResponseHeadersRead + a bounded deadline so the test reads the headers without draining the
            // open-ended SSE body.
            var eventsUri = new Uri(server.Address, "events?key=" + Uri.EscapeDataString(session.Key.Value));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response =
                await client.GetAsync(eventsUri, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            Assert.True(
                response.IsSuccessStatusCode,
                $"GET /events with the key should open the reload channel, got {(int)response.StatusCode}.");
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- 4. CSRF / same-origin on the state-changing POST ------------------------------------------------

    [Fact]
    public async Task Prompts_WithForeignOrigin_AreRejected_CsrfSameOrigin()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            // A POST carrying a FOREIGN (cross-site) Origin header is refused even WITH a valid capability key:
            // capability key + CSRF on state-changing routes. (The exact CSRF mechanism — same-origin check
            // vs. per-session token — is the implementer's call; here we assert only the observable refusal.)
            var promptsUri = new Uri(server.Address, $"api/{Uri.EscapeDataString(session.Key.Value)}/prompts");
            var payload = JsonSerializer.Serialize(new
            {
                kind = "element",
                anchorId = "b00000000000000000000",
                note = "cross-site forgery attempt",
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, promptsUri)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Origin", "https://charter-review.attacker.example");

            using var response = await client.SendAsync(request);
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- 5. Malformed body + unknown anchor on the state-changing POST ------------------------------------

    [Fact]
    public async Task Prompts_MalformedJsonBody_ReturnsBadRequest()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            // A valid key + same-origin request whose body is malformed JSON is a client error (400), NOT a
            // server fault (500) — the deserialize is guarded exactly like the answers route.
            var promptsUri = new Uri(server.Address, $"api/{Uri.EscapeDataString(session.Key.Value)}/prompts");
            using var request = new HttpRequestMessage(HttpMethod.Post, promptsUri)
            {
                Content = new StringContent("{", Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Origin", SameOrigin(server.Address));

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Prompts_UnknownAnchor_IsAcceptedWithNullSourceLine()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            // An anchorId that resolves to no block in the plan (the live-reload edit race: the reviewer
            // annotated a block that a concurrent edit removed). It must still be ACCEPTED (200) and enqueued,
            // with SourceLine null rather than rejected — the note is not silently dropped.
            const string unknownAnchor = "b0000000000000000000000000000000";
            var promptsUri = new Uri(server.Address, $"api/{Uri.EscapeDataString(session.Key.Value)}/prompts");
            var payload = JsonSerializer.Serialize(new
            {
                kind = "element",
                anchorId = unknownAnchor,
                note = "note on a block a concurrent edit removed",
            });
            using var postRequest = new HttpRequestMessage(HttpMethod.Post, promptsUri)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            postRequest.Headers.TryAddWithoutValidation("Origin", SameOrigin(server.Address));

            using var postResponse = await client.SendAsync(postRequest);
            Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);

            // Poll and confirm it was enqueued with a NULL source line (not resolved, not dropped).
            var pollUri = new Uri(server.Address, "api/poll?key=" + Uri.EscapeDataString(session.Key.Value));
            using var pollCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var pollResponse = await client.GetAsync(pollUri, pollCts.Token);
            Assert.True(pollResponse.IsSuccessStatusCode);

            var pollBody = await pollResponse.Content.ReadAsStringAsync(pollCts.Token);
            using var polled = JsonDocument.Parse(pollBody);
            var annotation = FindAnnotation(polled.RootElement, unknownAnchor);
            Assert.True(annotation.HasValue, "The unknown-anchor annotation should be enqueued, not dropped.");
            Assert.True(
                TryGetProperty(annotation.Value, "sourceLine", out var sourceLine),
                "The annotation should carry a sourceLine field.");
            Assert.Equal(JsonValueKind.Null, sourceLine.ValueKind);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    /// <summary>The scheme+host+port of <paramref name="address"/> — a same-origin value for the Origin header.</summary>
    private static string SameOrigin(Uri address) => address.GetLeftPart(UriPartial.Authority);

    /// <summary>
    /// Recursively locate the annotation object whose (case-insensitive) <c>anchorId</c> equals
    /// <paramref name="anchorId"/>, tolerating a bare array or a wrapper object. Returns the object element so
    /// the caller can inspect fields (e.g. a null <c>sourceLine</c>) that <see cref="FindSourceLineForAnchor"/>
    /// — which only returns numeric source lines — cannot distinguish from absence.
    /// </summary>
    private static JsonElement? FindAnnotation(JsonElement element, string anchorId)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindAnnotation(item, anchorId) is { } fromArray)
                    {
                        return fromArray;
                    }
                }

                return null;

            case JsonValueKind.Object:
                if (TryGetProperty(element, "anchorId", out var anchor) &&
                    anchor.ValueKind == JsonValueKind.String &&
                    string.Equals(anchor.GetString(), anchorId, StringComparison.Ordinal))
                {
                    return element;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (FindAnnotation(property.Value, anchorId) is { } fromObject)
                    {
                        return fromObject;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    private static string WriteTempPlan()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "charter-annotation-plan-" + Guid.NewGuid().ToString("N") + ".mdx");
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

    /// <summary>
    /// Recursively locate the annotation object whose (case-insensitive) <c>anchorId</c> equals
    /// <paramref name="anchorId"/> and return its numeric <c>sourceLine</c>, or <see langword="null"/> if no
    /// such resolved annotation is present. Tolerates the implementer's serialization shape (a bare array, or
    /// a wrapper object) and casing (PascalCase or camelCase), so the round-trip contract is on the value —
    /// SourceLine == the anchor's markdown line — not on an incidental JSON layout.
    /// </summary>
    private static int? FindSourceLineForAnchor(JsonElement element, string anchorId)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindSourceLineForAnchor(item, anchorId) is { } fromArray)
                    {
                        return fromArray;
                    }
                }

                return null;

            case JsonValueKind.Object:
                if (TryGetProperty(element, "anchorId", out var anchor) &&
                    anchor.ValueKind == JsonValueKind.String &&
                    string.Equals(anchor.GetString(), anchorId, StringComparison.Ordinal) &&
                    TryGetProperty(element, "sourceLine", out var sourceLine) &&
                    sourceLine.ValueKind == JsonValueKind.Number &&
                    sourceLine.TryGetInt32(out var resolved))
                {
                    return resolved;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (FindSourceLineForAnchor(property.Value, anchorId) is { } fromObject)
                    {
                        return fromObject;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
