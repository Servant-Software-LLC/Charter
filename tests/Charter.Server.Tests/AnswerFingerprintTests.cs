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
/// Charter #75 item 3, server half: when a reviewer answers a <c>:::question</c>, the server records the
/// question's DECLARED SHAPE alongside the answer. That fingerprint is an answer's counterpart to an
/// annotation's anchor — the evidence <c>charter resolve</c> / <c>poll --apply</c> check before folding a
/// decision into a plan, so a plan re-authored at the same path that reuses a question id cannot silently
/// inherit the old document's decision.
///
/// It is computed SERVER-SIDE from the plan on disk and overwrites anything a client sent, because evidence a
/// client can set is not evidence about the client.
/// </summary>
[Trait("Category", "AnswerApi")]
public class AnswerFingerprintTests
{
    private const string Question =
        ":::question\n"
        + "{ \"id\": \"db-choice\", \"title\": \"Which database?\", \"mode\": \"single\", "
        + "\"target\": \"human\", \"options\": [\"Postgres\", \"SQLite\"] }\n"
        + ":::\n";

    private const string PlanMarkdown = "# Fingerprint plan\n\nSome prose.\n\n" + Question;

    [Fact]
    public async Task PostedAnswer_CarriesTheQuestionsFingerprint_AsComputedFromThePlanOnDisk()
    {
        await WithServerAsync(async (server, session, client) =>
        {
            await PostAnswerAsync(client, server, session, new
            {
                questionId = "db-choice",
                mode = "single",
                values = new[] { "Postgres" },
                target = "human",
            });

            var answer = await PeekOneAsync(client, server, session, "db-choice");
            Assert.Equal(
                QuestionIdentity.FingerprintOf(PlanMarkdown, "db-choice"),
                answer.GetProperty("questionFingerprint").GetString());
        });
    }

    [Fact]
    public async Task PostedAnswer_IgnoresAClientSuppliedFingerprint()
    {
        // A client that could set its own fingerprint could vouch for itself, which is exactly what the field
        // exists to prevent. The server always recomputes.
        await WithServerAsync(async (server, session, client) =>
        {
            await PostAnswerAsync(client, server, session, new
            {
                questionId = "db-choice",
                mode = "single",
                values = new[] { "Postgres" },
                target = "human",
                questionFingerprint = "0000000000000000000000000000000000000000000000000000000000000000",
            });

            var answer = await PeekOneAsync(client, server, session, "db-choice");
            Assert.Equal(
                QuestionIdentity.FingerprintOf(PlanMarkdown, "db-choice"),
                answer.GetProperty("questionFingerprint").GetString());
        });
    }

    [Fact]
    public async Task AnswerToAQuestionThatIsNotInThePlan_CarriesNoFingerprint_NoEvidenceIsNotFalseEvidence()
    {
        // An answer whose questionId matches nothing is a documented no-op for the apply, so there is nothing
        // to protect against — and inventing a fingerprint here would make a later comparison meaningless. The
        // field is omitted entirely rather than emitted as null, keeping the shipped wire shape unchanged for
        // every payload that has nothing to say.
        await WithServerAsync(async (server, session, client) =>
        {
            await PostAnswerAsync(client, server, session, new
            {
                questionId = "no-such-question",
                mode = "single",
                values = new[] { "whatever" },
                target = "human",
            });

            var answer = await PeekOneAsync(client, server, session, "no-such-question");
            Assert.False(
                answer.TryGetProperty("questionFingerprint", out _),
                "an answer with no evidence must omit the field, not carry a null or a fabricated value.");
        });
    }

    [Fact]
    public async Task Fingerprint_SurvivesTheSidecarRoundTrip_SoASoloResolveCanStillCheckIt()
    {
        // `charter resolve`'s solo path reads the sidecar, not the server. If the evidence did not persist, the
        // exact case #75 item 3 describes — answer, close the browser, resolve later — would be unguarded.
        var sidecarDirectory = Path.Combine(
            Path.GetTempPath(), "charter-fingerprint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sidecarDirectory);
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            var options = new ReviewServerOptions
            {
                BindAddress = IPAddress.Loopback,
                Port = 0,
                SidecarDirectory = sidecarDirectory,
            };

            using (var server = ReviewServer.Start(session, options))
            {
                using var client = new HttpClient();
                await PostAnswerAsync(client, server, session, new
                {
                    questionId = "db-choice",
                    mode = "single",
                    values = new[] { "Postgres" },
                    target = "human",
                });
            }

            var state = ReviewSidecar.Rehydrate(
                ReviewSidecar.PathForPlan(sidecarDirectory, planPath));
            var answer = Assert.Single(state.Answers);
            Assert.Equal(QuestionIdentity.FingerprintOf(PlanMarkdown, "db-choice"), answer.QuestionFingerprint);
        }
        finally
        {
            TryDelete(planPath);
            TryDeleteDirectory(sidecarDirectory);
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------------------

    private static async Task WithServerAsync(Func<ReviewServer, ReviewSession, HttpClient, Task> body)
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();
            await body(server, session, client);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    private static async Task PostAnswerAsync(
        HttpClient client, ReviewServer server, ReviewSession session, object payload)
    {
        var uri = new Uri(server.Address, $"api/{Uri.EscapeDataString(session.Key.Value)}/answers");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", server.Address.GetLeftPart(UriPartial.Authority));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"the answer should be accepted, got {(int)response.StatusCode}.");
    }

    private static async Task<JsonElement> PeekOneAsync(
        HttpClient client, ReviewServer server, ReviewSession session, string questionId)
    {
        var uri = new Uri(server.Address, "api/answers?key=" + Uri.EscapeDataString(session.Key.Value));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(uri, cts.Token);
        Assert.True(response.IsSuccessStatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (string.Equals(item.GetProperty("questionId").GetString(), questionId, StringComparison.Ordinal))
            {
                return item.Clone();
            }
        }

        throw new Xunit.Sdk.XunitException($"GET /api/answers did not report an answer for '{questionId}'.");
    }

    private static string WriteTempPlan()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "charter-fingerprint-plan-" + Guid.NewGuid().ToString("N") + ".charter.md");
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup. UnauthorizedAccessException belongs here as much as IOException: the
            // sidecar and the review log both persist through a write-then-rename, so a `.tmp-...` file can
            // still be open when a disposing server races this teardown, and Windows reports that as
            // access-denied rather than a sharing violation. Catching only IOException makes a test that
            // has already PASSED report as failed, pointing at its assertion instead of at the cleanup.
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup. UnauthorizedAccessException belongs here as much as IOException: the
            // sidecar and the review log both persist through a write-then-rename, so a `.tmp-...` file can
            // still be open when a disposing server races this teardown, and Windows reports that as
            // access-denied rather than a sharing violation. Catching only IOException makes a test that
            // has already PASSED report as failed, pointing at its assertion instead of at the cleanup.
        }
    }
}
