using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Integration tests for the <c>:::question</c> ANSWER-submission HTTP API on <see cref="ReviewServer"/> —
/// the route a reviewer's chosen answer travels on its way to the author/agent handoff. Every test starts a
/// REAL server with <see cref="ReviewServer.Start(ReviewSession, ReviewServerOptions?)"/> on default
/// (loopback, ephemeral-port) options and drives it over <see cref="System.Net.Http.HttpClient"/>, disposing
/// the server via <c>using</c> and cleaning the temp plan in a <c>finally</c>.
///
/// This is the TDD "red": every test COMPILES against the existing wave-2/wave-3 surface (no new stubs —
/// <see cref="ReviewServer"/>, <see cref="ReviewSession"/>, <see cref="ReviewServerOptions"/>,
/// <see cref="System.Net.Http.HttpClient"/>) and FAILS at runtime because the answer routes are not routed
/// yet: <c>POST /api/{key}/answers</c> and <c>GET /api/answers?key=…</c> currently fall through to a 404 in
/// the API dispatcher. Task <c>14-implement-answer-submission</c> turns it green; this task must NOT
/// implement the endpoints.
/// </summary>
/// <remarks>
/// Class trait: <c>[Trait("Category","AnswerApi")]</c> — distinct from <c>Category=AnnotationApi</c>,
/// <c>Category=AnnotationStore</c>, and wave-2's <c>Category=ReviewServer</c>.
///
/// DESIGN DECISION this encodes (so the implementer matches it): an answer uses a DEDICATED route, NOT the
/// annotation <c>/api/{key}/prompts</c> + <c>/api/poll</c> pair. An answer's shape (a <c>questionId</c>, a
/// <c>mode</c>, the selected <c>values</c>, and a <c>target</c> of <c>human</c> or <c>agent</c>) differs from
/// an annotation (an <c>anchorId</c> + a <c>note</c>), and reusing <c>/api/poll</c> would force a breaking
/// change to the wave-3 annotation poll contract (whose response is a bare annotation array — a shared-golden
/// break). So the answer round-trip is authored against a NEW <c>POST /api/{key}/answers</c> (submit, key in
/// the PATH segment like <c>prompts</c>, CSRF-gated) plus <c>GET /api/answers?key=…</c> (drain, key on the
/// query string like <c>poll</c>).
///
/// Wire contract the implementer must honour (decided here, mirroring the wave-3 annotation API's gates):
/// <list type="bullet">
///   <item><c>POST /api/{key}/answers</c> accepts a JSON body <c>{ questionId, mode, values, target }</c> and
///   returns 200; it is capability-key gated on the PATH segment and CSRF-gated (a foreign <c>Origin</c> is
///   refused even with a valid key; a same-origin or absent <c>Origin</c> is accepted).</item>
///   <item><c>GET /api/answers?key=…</c> drains the queued answers as JSON — a bare array or a wrapper object,
///   PascalCase or camelCase (the tests read it tolerantly) — and is rejected (non-200) without the key.</item>
/// </list>
/// An answer's identity is its <c>questionId</c> (an opaque, client-chosen id), NOT a parsed anchor, so the
/// round-trip is a pure echo of the submitted answer rather than a source-map resolution.
/// </remarks>
[Trait("Category", "AnswerApi")]
public class AnswerApiTests
{
    // A tiny plan carrying a :::question block — the deliverable surface a reviewer answers. Nothing in these
    // answer-route tests renders the plan; the questionId in the POST body (not the parsed block) is the
    // answer's identity, so the plan only needs to exist for ReviewSession.Create to bind to.
    private const string PlanMarkdown =
        "# Charter Answer API Plan\n" +
        "\n" +
        "An overview paragraph introducing the plan under review.\n" +
        "\n" +
        ":::question\n" +
        "Which colours should the theme ship with? Pick all that apply.\n" +
        ":::\n";

    // ---- 1. Round-trip acceptance (load-bearing) --------------------------------------------------------

    [Fact]
    public async Task RoundTrip_PostAnswer_ThenDrain_EchoesQuestionIdValuesAndTarget()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            const string questionId = "q-theme-colours";
            var values = new[] { "sky-blue", "forest-green" };
            const string target = "human";

            // POST the structured answer as a SAME-ORIGIN request (Origin == the server's own authority; no
            // foreign Origin) to the state-changing answers route, and assert it is accepted.
            using var postResponse = await PostAnswerAsync(
                client, server.Address, session.Key.Value,
                new { questionId, mode = "multi-select", values, target },
                origin: SameOrigin(server.Address));
            Assert.True(
                postResponse.IsSuccessStatusCode,
                $"POST /api/{{key}}/answers should accept the answer, got {(int)postResponse.StatusCode}.");

            // Drain: GET /api/answers?key=… returns the queued answer(s). Bounded deadline (no fixed sleeps);
            // the answer is already queued by the awaited POST, so a single drain returns it.
            var answersUri =
                new Uri(server.Address, "api/answers?key=" + Uri.EscapeDataString(session.Key.Value));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var getResponse = await client.GetAsync(answersUri, cts.Token);
            Assert.True(
                getResponse.IsSuccessStatusCode,
                $"GET /api/answers should drain the queued answer, got {(int)getResponse.StatusCode}.");

            var body = await getResponse.Content.ReadAsStringAsync(cts.Token);

            // The DRAINED answer round-trips the SAME questionId, the SAME selected values, and the SAME
            // target — the fields the downstream handoff routes on. Asserting on the GET-drained value (not
            // just the POST status) is what makes this the load-bearing acceptance.
            using var doc = JsonDocument.Parse(body);
            var answer = FindAnswer(doc.RootElement, questionId);
            Assert.True(answer.HasValue, "The drained answers should include the submitted questionId.");
            var found = answer.Value;

            Assert.True(
                TryGetProperty(found, "questionId", out var echoedQuestionId) &&
                echoedQuestionId.ValueKind == JsonValueKind.String &&
                string.Equals(echoedQuestionId.GetString(), questionId, StringComparison.Ordinal),
                "The drained answer should echo the submitted questionId.");
            var drainedValues = ReadValues(found);
            Assert.Equal(values.OrderBy(v => v, StringComparer.Ordinal), drainedValues.OrderBy(v => v, StringComparer.Ordinal));
            Assert.Equal(target, ReadTarget(found), ignoreCase: true);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- 2. Target routing observable (human vs agent) --------------------------------------------------

    [Fact]
    public async Task Targets_HumanAndAgentAnswers_AreBothAccepted_AndEachEchoesItsTarget()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            const string humanQuestionId = "q-human-routed";
            const string agentQuestionId = "q-agent-routed";

            // A human-target answer and an agent-target answer are BOTH accepted (same-origin POSTs).
            using var humanPost = await PostAnswerAsync(
                client, server.Address, session.Key.Value,
                new { questionId = humanQuestionId, mode = "single-select", values = new[] { "ship-it" }, target = "human" },
                origin: SameOrigin(server.Address));
            Assert.True(
                humanPost.IsSuccessStatusCode,
                $"human-target POST /api/{{key}}/answers should be accepted, got {(int)humanPost.StatusCode}.");

            using var agentPost = await PostAnswerAsync(
                client, server.Address, session.Key.Value,
                new { questionId = agentQuestionId, mode = "single-select", values = new[] { "regenerate" }, target = "agent" },
                origin: SameOrigin(server.Address));
            Assert.True(
                agentPost.IsSuccessStatusCode,
                $"agent-target POST /api/{{key}}/answers should be accepted, got {(int)agentPost.StatusCode}.");

            var answersUri =
                new Uri(server.Address, "api/answers?key=" + Uri.EscapeDataString(session.Key.Value));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var getResponse = await client.GetAsync(answersUri, cts.Token);
            Assert.True(
                getResponse.IsSuccessStatusCode,
                $"GET /api/answers should drain the queued answers, got {(int)getResponse.StatusCode}.");

            var body = await getResponse.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);

            var humanAnswer = FindAnswer(doc.RootElement, humanQuestionId);
            var agentAnswer = FindAnswer(doc.RootElement, agentQuestionId);
            Assert.True(humanAnswer.HasValue, "The human-target answer should be drained.");
            Assert.True(agentAnswer.HasValue, "The agent-target answer should be drained.");

            // The DRAINED answers preserve their target — the field the downstream handoff routes on.
            Assert.Equal("human", ReadTarget(humanAnswer.Value), ignoreCase: true);
            Assert.Equal("agent", ReadTarget(agentAnswer.Value), ignoreCase: true);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- 3. CSRF / same-origin on the state-changing POST -----------------------------------------------

    [Fact]
    public async Task PostAnswer_WithForeignOrigin_IsRejected_CsrfSameOrigin()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            // A POST carrying a FOREIGN (cross-site) Origin header is refused even WITH a valid capability key:
            // capability key + CSRF on the state-changing answers route. The exact CSRF mechanism (same-origin
            // check vs. per-session token) is the implementer's call; here we assert only the observable
            // refusal. The happy-path POST in the round-trip test is same-origin.
            using var response = await PostAnswerAsync(
                client, server.Address, session.Key.Value,
                new { questionId = "q-forged", mode = "single-select", values = new[] { "malicious" }, target = "agent" },
                origin: "https://charter-review.attacker.example");

            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- 4. Capability-key gate on the drain ------------------------------------------------------------

    [Fact]
    public async Task DrainAnswers_WithoutOrWithWrongKey_IsRejected()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // No key at all — refused (the drain must not leak queued answers to an unauthorized reader).
            var noKeyUri = new Uri(server.Address, "api/answers");
            using var noKey = await client.GetAsync(noKeyUri, cts.Token);
            Assert.NotEqual(HttpStatusCode.OK, noKey.StatusCode);

            // A wrong key — refused (a guessed ephemeral port is not enough; the capability key gates access).
            var wrongKeyUri =
                new Uri(server.Address, "api/answers?key=" + Uri.EscapeDataString("not-the-real-key"));
            using var wrongKey = await client.GetAsync(wrongKeyUri, cts.Token);
            Assert.NotEqual(HttpStatusCode.OK, wrongKey.StatusCode);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- 5. Malformed body on the state-changing POST ---------------------------------------------------

    [Fact]
    public async Task PostAnswer_MalformedJsonBody_ReturnsBadRequest()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            // A valid key + same-origin request whose body is malformed JSON is a client error (400), NOT a
            // server fault (500) — pins the 400 guard the answers and prompts routes now share.
            var answersUri = new Uri(server.Address, $"api/{Uri.EscapeDataString(session.Key.Value)}/answers");
            using var request = new HttpRequestMessage(HttpMethod.Post, answersUri)
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

    // ---- Helpers ----------------------------------------------------------------------------------------

    /// <summary>
    /// POST <paramref name="answer"/> (serialized as JSON) to <c>/api/{key}/answers</c>, optionally carrying an
    /// <c>Origin</c> header. The returned response is buffered (default <c>ResponseContentRead</c>), so the
    /// caller owns and disposes it while the request message is released here.
    /// </summary>
    private static async Task<HttpResponseMessage> PostAnswerAsync(
        HttpClient client, Uri serverAddress, string key, object answer, string? origin)
    {
        var answersUri = new Uri(serverAddress, $"api/{Uri.EscapeDataString(key)}/answers");
        using var request = new HttpRequestMessage(HttpMethod.Post, answersUri)
        {
            Content = new StringContent(JsonSerializer.Serialize(answer), Encoding.UTF8, "application/json"),
        };
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        return await client.SendAsync(request);
    }

    /// <summary>The scheme+host+port of <paramref name="address"/> — a same-origin value for the Origin header.</summary>
    private static string SameOrigin(Uri address) => address.GetLeftPart(UriPartial.Authority);

    private static string WriteTempPlan()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "charter-answer-plan-" + Guid.NewGuid().ToString("N") + ".mdx");
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
    /// Recursively locate the answer object whose (case-insensitive) <c>questionId</c> equals
    /// <paramref name="questionId"/>. Tolerates the implementer's serialization shape (a bare array or a
    /// wrapper object) and casing (PascalCase or camelCase), so the round-trip contract is on the value — the
    /// echoed questionId/values/target — not on an incidental JSON layout.
    /// </summary>
    private static JsonElement? FindAnswer(JsonElement element, string questionId)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindAnswer(item, questionId) is { } fromArray)
                    {
                        return fromArray;
                    }
                }

                return null;

            case JsonValueKind.Object:
                if (TryGetProperty(element, "questionId", out var qid) &&
                    qid.ValueKind == JsonValueKind.String &&
                    string.Equals(qid.GetString(), questionId, StringComparison.Ordinal))
                {
                    return element;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (FindAnswer(property.Value, questionId) is { } fromObject)
                    {
                        return fromObject;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>Read the answer's selected <c>values</c> as strings, tolerating a JSON array or a single string.</summary>
    private static List<string> ReadValues(JsonElement answer)
    {
        if (!TryGetProperty(answer, "values", out var values))
        {
            return new List<string>();
        }

        return values.ValueKind switch
        {
            JsonValueKind.Array => values.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!)
                .ToList(),
            JsonValueKind.String => new List<string> { values.GetString()! },
            _ => new List<string>(),
        };
    }

    /// <summary>Read the answer's <c>target</c> (<c>human</c>/<c>agent</c>) string, or null when absent.</summary>
    private static string? ReadTarget(JsonElement answer)
        => TryGetProperty(answer, "target", out var target) && target.ValueKind == JsonValueKind.String
            ? target.GetString()
            : null;

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
