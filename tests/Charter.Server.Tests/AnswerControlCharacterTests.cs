using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Charter.Core;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// The answer route refuses a value carrying a control character (Charter #202) — the first of the two
/// entry points an answer can arrive through, and the one whose value ends up written INTO the plan.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not a breach of Charter #186's asymmetry.</b> That rule says validation is a function of WHO
/// supplied the value: a human at a review page holds authority to exceed the declared <c>options</c> (the
/// "Something else" write-in), an <c>--answers</c> invocation does not. A control character is not a decision
/// anybody holds authority over — it is a malformation of the CARRIER. A reviewer's authority is over what
/// they decide, not over whether the decision is expressible as a line of text.
/// </para>
/// <para>
/// <b>No reviewer can reach this gate from the shipped page</b>, and that is deliberate rather than lucky. A
/// <c>free-text</c> question renders a <c>&lt;textarea&gt;</c>, whose API value is CRLF-normalized to LF by
/// the browser; the "Something else" write-in is an <c>&lt;input type="text"&gt;</c>, whose value sanitization
/// strips CR and LF outright. So the gate only ever fires for a NON-browser client posting to the loopback API
/// with a valid capability key — which is a real channel, because whatever it queues is what
/// <c>charter poll --apply</c> writes into the <c>.charter.md</c>.
/// </para>
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","AnswerControlCharacter")].
/// </remarks>
[Trait("Category", "AnswerControlCharacter")]
public class AnswerControlCharacterTests
{
    private const string PlanMarkdown =
        "# Plan\n\n:::question\n"
        + "{\"id\": \"why\", \"title\": \"Why this approach?\", \"mode\": \"free-text\", "
        + "\"target\": \"human\"}\n:::\n";

    [Fact]
    public async Task ACarriageReturnInAValue_IsRefusedWithA400_AndNothingIsQueued()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            using var response = await PostAnswerAsync(
                client, server.Address, session.Key.Value, ["alpha\rbeta"]);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            // The refusal must also be a non-event: an answer the server rejected must not sit in the queue
            // waiting for a drain that would write it into the plan.
            var queued = await client.GetStringAsync(
                new Uri(server.Address, $"api/answers?key={Uri.EscapeDataString(session.Key.Value)}"));
            Assert.DoesNotContain("alpha", queued, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(planPath);
        }
    }

    [Fact]
    public async Task TheRefusalSaysWhichCharacterAndWhichValue()
    {
        // A 400 with an empty body leaves a script author unable to tell a rule from a bug (Charter #170/#178,
        // one layer down). The body names the character by code point; it never echoes the raw one.
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            using var response = await PostAnswerAsync(
                client, server.Address, session.Key.Value, ["alpha\rbeta"]);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Contains("U+000D", body, StringComparison.Ordinal);
            Assert.DoesNotContain('\r', body);
        }
        finally
        {
            File.Delete(planPath);
        }
    }

    [Fact]
    public async Task AMultiLineFreeTextAnswer_IsSTILLACCEPTED()
    {
        // The boundary. The page gives a free-text question a <textarea>; a reviewer writing two sentences on
        // two lines must not be told their answer is invalid by the very page that offered them the box.
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            using var response = await PostAnswerAsync(
                client, server.Address, session.Key.Value, ["one\ntwo"]);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var queued = await client.GetStringAsync(
                new Uri(server.Address, $"api/answers?key={Uri.EscapeDataString(session.Key.Value)}"));
            Assert.Contains("one", queued, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(planPath);
        }
    }

    [Fact]
    public async Task ARefusedAnswer_NeverReachesTheFlattenedPlan()
    {
        // The whole point, asserted end to end rather than at the boundary: the answer is refused, so
        // `poll --apply` has nothing to write, so the plan still carries no `answer`, so the handoff emits an
        // OPEN question and the handed-off document contains no bare CR at all.
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            using var response = await PostAnswerAsync(
                client, server.Address, session.Key.Value, ["alpha\rbeta"]);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var flatten = HandoffMarkdown.Emit(File.ReadAllText(planPath));

            Assert.DoesNotContain("\r", flatten, StringComparison.Ordinal);
            Assert.Contains(HandoffMarkdown.OpenQuestionMarker, flatten, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(planPath);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static string WriteTempPlan()
    {
        var path = Path.Combine(Path.GetTempPath(), $"charter-202-{Guid.NewGuid():N}.charter.md");
        File.WriteAllText(path, PlanMarkdown);
        return path;
    }

    private static async Task<HttpResponseMessage> PostAnswerAsync(
        HttpClient client, Uri serverAddress, string key, string[] values)
    {
        var uri = new Uri(serverAddress, $"api/{Uri.EscapeDataString(key)}/answers");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new { questionId = "why", mode = "free-text", values, target = "human" }),
                Encoding.UTF8,
                "application/json"),
        };

        return await client.SendAsync(request);
    }
}
