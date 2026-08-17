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
using Charter.Core;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Integration tests for the pre-drain annotation MANAGEMENT API added for Charter #42 (the in-page review
/// panel): a non-destructive list, plus edit and delete by id. The list is the <b>pre-drain queue</b> — an
/// annotation it returns is by definition not yet handed off to the agent — so the two writes act on
/// <c>_pending</c> only and answer <b>404</b> for an id the agent has already drained. The drain contract
/// (<c>GET /api/poll</c>, <c>charter poll</c>, the <c>PollEnvelope</c> wire shape) is deliberately untouched.
/// </summary>
/// <remarks>
/// Gating mirrors the existing routes exactly: the read takes the key on the query string like
/// <c>GET /api/answers</c>; the two writes take the key in the path and are CSRF-gated by
/// <c>AnnotationApi.IsAllowedOrigin</c> like <c>POST /api/{key}/answers</c> and <c>.../answers/ack</c>.
/// </remarks>
[Trait("Category", "AnnotationManagementApi")]
public class AnnotationManagementApiTests
{
    private const string PlanMarkdown =
        "# Charter Annotation Management Plan\n" +
        "\n" +
        "An overview paragraph introducing the plan under review.\n" +
        "\n" +
        "The reviewer annotates this distinctive target paragraph for the round-trip.\n";

    private const string AnchorMarker = "distinctive target";

    // ---- The list: non-destructive, key-gated ------------------------------------------------------------

    [Fact]
    public async Task List_IsNonDestructive_TheAgentsPollStillDeliversTheAnnotation()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var created = await PostAnnotationAsync(client, server, session, "Please clarify this paragraph.");

            // Two consecutive lists return the same annotation — the panel reading its queue must never
            // consume it out from under `charter poll`.
            var first = await ListAsync(client, server, session.Key.Value);
            var second = await ListAsync(client, server, session.Key.Value);
            Assert.Equal(created.Id, Assert.Single(first).Id);
            Assert.Equal(created.Id, Assert.Single(second).Id);
            Assert.Equal("Please clarify this paragraph.", second[0].Note);

            // ...and the agent's drain still delivers it.
            var drained = await PollAsync(client, server, session.Key.Value);
            Assert.Equal(created.Id, Assert.Single(drained).Id);

            // After the hand-off the pre-drain queue is empty: the panel shows nothing pending.
            Assert.Empty(await ListAsync(client, server, session.Key.Value));
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task List_WithWrongOrMissingKey_IsRefused()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            using var wrongKey = await client.GetAsync(new Uri(server.Address, "api/annotations?key=not-the-key"));
            Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);

            using var noKey = await client.GetAsync(new Uri(server.Address, "api/annotations"));
            Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- Update ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_ChangesTheNote_AndTheListAndTheDrainBothReflectIt()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var created = await PostAnnotationAsync(client, server, session, "first draft of the note");

            using var response = await UpdateAsync(client, server, session.Key.Value, created.Id, "the edited note");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var listed = Assert.Single(await ListAsync(client, server, session.Key.Value));
            Assert.Equal("the edited note", listed.Note);
            // Identity and the resolved anchor survive the edit untouched.
            Assert.Equal(created.Id, listed.Id);
            Assert.Equal(created.AnchorId, listed.AnchorId);
            Assert.Equal(created.SourceLine, listed.SourceLine);

            // The agent receives the EDITED note, not the first draft.
            var drained = Assert.Single(await PollAsync(client, server, session.Key.Value));
            Assert.Equal("the edited note", drained.Note);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Update_UnknownId_IsNotFound()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            using var response = await UpdateAsync(
                client, server, session.Key.Value, "0123456789abcdef0123456789abcdef", "edit into the void");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Update_AfterTheAgentDrainedIt_IsNotFound_AlreadyHandedOff()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var created = await PostAnnotationAsync(client, server, session, "a note the agent takes first");
            await PollAsync(client, server, session.Key.Value);

            // 404 means "already handed off to the agent" — the SDK surfaces that as a friendly message, not
            // an error. Editing a delivered note is not a thing: the agent already has it.
            using var response = await UpdateAsync(client, server, session.Key.Value, created.Id, "too late");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Update_WithForeignOrigin_IsForbidden_CsrfSameOrigin()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var created = await PostAnnotationAsync(client, server, session, "a note to forge an edit of");

            using var response = await UpdateAsync(
                client, server, session.Key.Value, created.Id, "cross-site edit",
                origin: "https://charter-review.attacker.example");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            // The note is untouched.
            Assert.Equal("a note to forge an edit of", Assert.Single(await ListAsync(client, server, session.Key.Value)).Note);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Update_WithWrongKey_IsUnauthorized()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var created = await PostAnnotationAsync(client, server, session, "a note to forge an edit of");

            using var response = await UpdateAsync(client, server, "not-the-key", created.Id, "unauthorized edit");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Update_WithMalformedJsonBody_IsBadRequest()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var created = await PostAnnotationAsync(client, server, session, "a note");

            var uri = new Uri(
                server.Address, $"api/{Uri.EscapeDataString(session.Key.Value)}/annotations/{created.Id}");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
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

    // ---- Delete ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheAnnotation_FromTheListAndFromTheAgentsDrain()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var keep = await PostAnnotationAsync(client, server, session, "a note the reviewer keeps");
            var retract = await PostAnnotationAsync(client, server, session, "a note the reviewer retracts");

            using var response = await DeleteAsync(client, server, session.Key.Value, retract.Id);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var listed = Assert.Single(await ListAsync(client, server, session.Key.Value));
            Assert.Equal(keep.Id, listed.Id);

            // The retracted note never reaches the agent — deleting a PENDING annotation is a true retraction.
            var drained = Assert.Single(await PollAsync(client, server, session.Key.Value));
            Assert.Equal(keep.Id, drained.Id);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Delete_UnknownOrAlreadyDrainedId_IsNotFound()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            using var unknown = await DeleteAsync(
                client, server, session.Key.Value, "0123456789abcdef0123456789abcdef");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

            var created = await PostAnnotationAsync(client, server, session, "a note the agent takes first");
            await PollAsync(client, server, session.Key.Value);

            using var drainedAlready = await DeleteAsync(client, server, session.Key.Value, created.Id);
            Assert.Equal(HttpStatusCode.NotFound, drainedAlready.StatusCode);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Delete_WithForeignOrigin_IsForbidden_CsrfSameOrigin()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var created = await PostAnnotationAsync(client, server, session, "a note a foreign page must not delete");

            using var response = await DeleteAsync(
                client, server, session.Key.Value, created.Id,
                origin: "https://charter-review.attacker.example");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            Assert.Single(await ListAsync(client, server, session.Key.Value));
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    [Fact]
    public async Task Delete_WithWrongKey_IsUnauthorized()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var created = await PostAnnotationAsync(client, server, session, "a note");

            using var response = await DeleteAsync(client, server, "not-the-key", created.Id);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            Assert.Single(await ListAsync(client, server, session.Key.Value));
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    // ---- BLOCKER 3: deleting the last pending annotation must NOT leave the long-poll hot -----------------

    [Fact]
    public async Task Delete_EmptyingTheQueue_LeavesTheLongPollBlocking_NotSpinning()
    {
        var planPath = WriteTempPlan();
        using var pollCts = new CancellationTokenSource();
        try
        {
            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(
                session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
            using var client = new HttpClient();

            var created = await PostAnnotationAsync(client, server, session, "a note the reviewer retracts");
            using (var deleted = await DeleteAsync(client, server, session.Key.Value, created.Id))
            {
                Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            }

            // The wake signal is edge-triggered. If the delete emptied the buffer without re-arming it, this
            // long-poll (no wait=0) returns INSTANTLY with [] — and `charter poll --wait` becomes a hot loop.
            // It must instead stay blocked until the server's own poll timeout.
            var pollUri = new Uri(server.Address, "api/poll?key=" + Uri.EscapeDataString(session.Key.Value));
            var poll = client.GetAsync(pollUri, pollCts.Token);
            var finished = await Task.WhenAny(poll, Task.Delay(TimeSpan.FromSeconds(2), pollCts.Token));

            Assert.False(
                ReferenceEquals(finished, poll),
                "GET /api/poll returned immediately after the only pending annotation was deleted — the wake " +
                "signal was left completed on an empty buffer (permanent hot loop).");
        }
        finally
        {
            pollCts.Cancel();
            TryDelete(planPath);
        }
    }

    // ---- Durability: the sidecar tracks the edit and the delete ------------------------------------------

    [Fact]
    public async Task Sidecar_ReflectsTheEditAndTheDelete_SoARestartRestoresTheManagedQueue()
    {
        var sidecarDir = NewTempDir();
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            var options = new ReviewServerOptions
            {
                BindAddress = IPAddress.Loopback,
                Port = 0,
                SidecarDirectory = sidecarDir,
            };

            string keptId;
            using (var server = ReviewServer.Start(session, options))
            {
                using var client = new HttpClient();
                var kept = await PostAnnotationAsync(client, server, session, "first draft");
                var retracted = await PostAnnotationAsync(client, server, session, "a note to retract");
                keptId = kept.Id;

                using (var updated = await UpdateAsync(client, server, session.Key.Value, kept.Id, "edited before handoff"))
                {
                    Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
                }

                using (var deleted = await DeleteAsync(client, server, session.Key.Value, retracted.Id))
                {
                    Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
                }
            } // the review process exits before any agent drained.

            // A fresh server on the same session rehydrates ONLY the kept annotation, carrying the edit.
            using (var restarted = ReviewServer.Start(session, options))
            {
                using var client = new HttpClient();
                var listed = Assert.Single(await ListAsync(client, restarted, session.Key.Value));
                Assert.Equal(keptId, listed.Id);
                Assert.Equal("edited before handoff", listed.Note);
            }
        }
        finally
        {
            TryDeleteDir(sidecarDir);
            TryDelete(planPath);
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------------------

    /// <summary>
    /// Charter #158 — a reviewer's reply is BOTH recorded and delivered. The record alone would make the
    /// panel button look like it worked while the agent received nothing until after the review.
    /// </summary>
    [Fact]
    public async Task Reply_IsRecordedAndAlsoDeliveredToTheAgentsDrain()
    {
        var planPath = WriteTempPlan();
        try
        {
            var session = ReviewSession.Create(planPath);
            var writer = new ReviewLogWriter(planPath, new ReviewAuthor("David Maltby", "david@example.com"));
            using var server = ReviewServer.Start(session, new ReviewServerOptions
            {
                BindAddress = IPAddress.Loopback,
                Port = 0,
                ReviewLog = writer,
            });
            using var client = new HttpClient();

            var created = await PostAnnotationAsync(client, server, session, "Please clarify this paragraph.");
            Assert.Single(await PollAndAckAsync(client, server, session));   // the agent takes the note

            var uri = new Uri(
                server.Address,
                $"api/{Uri.EscapeDataString(session.Key.Value)}/annotations/{Uri.EscapeDataString(created.Id)}/reply");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { note = "It is not — that paragraph is about something else." }),
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Origin", SameOrigin(server.Address));

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // RECORDED: the reply is in the durable log, in the parent's thread, in the HUMAN's voice.
            var comment = Assert.Single(ReviewLogStore.ReadForPlan(planPath).State.Comments);
            var reply = Assert.Single(comment.Replies);
            Assert.Equal(ReviewActors.Human, reply.Actor);

            // DELIVERED: and it reaches the agent's drain in this round, carrying its parent.
            var drained = await PollAndAckAsync(client, server, session);
            var delivered = Assert.Single(drained);
            Assert.Equal(created.Id, delivered.ReplyTo);
            Assert.Contains("something else", delivered.Note, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    private static async Task<Annotation> PostAnnotationAsync(
        HttpClient client, ReviewServer server, ReviewSession session, string note)
    {
        var anchorId = BlockDocument.Parse(PlanMarkdown).Blocks
            .Single(b => b.RawContent.Contains(AnchorMarker, StringComparison.Ordinal)).Id;

        var uri = new Uri(server.Address, $"api/{Uri.EscapeDataString(session.Key.Value)}/prompts");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { kind = "element", anchorId, note }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", SameOrigin(server.Address));

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var created = JsonSerializer.Deserialize<Annotation>(
            await response.Content.ReadAsStringAsync(), AnnotationApi.JsonOptions);
        Assert.NotNull(created);
        return created!;
    }

    private static async Task<IReadOnlyList<Annotation>> ListAsync(
        HttpClient client, ReviewServer server, string key)
    {
        var uri = new Uri(server.Address, "api/annotations?key=" + Uri.EscapeDataString(key));
        using var response = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "json",
            response.Content.Headers.ContentType?.MediaType ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        return JsonSerializer.Deserialize<List<Annotation>>(
            await response.Content.ReadAsStringAsync(), AnnotationApi.JsonOptions) ?? new List<Annotation>();
    }

    /// <summary>
    /// Drain AND acknowledge, which is what a real agent does. An un-acked batch stays in flight and blocks
    /// the next drain for the whole visibility window (at-least-once delivery, working as designed) — so a
    /// test that polls twice without acking measures that window rather than the behaviour it means to.
    /// </summary>
    private static async Task<IReadOnlyList<Annotation>> PollAndAckAsync(
        HttpClient client, ReviewServer server, ReviewSession session)
    {
        var uri = new Uri(
            server.Address, "api/poll?wait=0&key=" + Uri.EscapeDataString(session.Key.Value));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(uri, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var drained = JsonSerializer.Deserialize<List<Annotation>>(
            await response.Content.ReadAsStringAsync(cts.Token), AnnotationApi.JsonOptions)
            ?? new List<Annotation>();

        if (response.Headers.TryGetValues(ReviewServer.DrainSequenceHeader, out var values))
        {
            var ackUri = new Uri(
                server.Address,
                $"api/{Uri.EscapeDataString(session.Key.Value)}/annotations/ack?sequence={values.First()}");
            using var ack = new HttpRequestMessage(HttpMethod.Post, ackUri);
            ack.Headers.TryAddWithoutValidation("Origin", SameOrigin(server.Address));
            using var ackResponse = await client.SendAsync(ack, cts.Token);
            Assert.Equal(HttpStatusCode.OK, ackResponse.StatusCode);
        }

        return drained;
    }

    private static async Task<IReadOnlyList<Annotation>> PollAsync(
        HttpClient client, ReviewServer server, string key)
    {
        var uri = new Uri(
            server.Address, "api/poll?wait=0&key=" + Uri.EscapeDataString(key));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(uri, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonSerializer.Deserialize<List<Annotation>>(
            await response.Content.ReadAsStringAsync(cts.Token), AnnotationApi.JsonOptions)
            ?? new List<Annotation>();
    }

    private static async Task<HttpResponseMessage> UpdateAsync(
        HttpClient client, ReviewServer server, string key, string id, string note, string? origin = null)
    {
        var uri = new Uri(server.Address, $"api/{Uri.EscapeDataString(key)}/annotations/{Uri.EscapeDataString(id)}");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { note }), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", origin ?? SameOrigin(server.Address));
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> DeleteAsync(
        HttpClient client, ReviewServer server, string key, string id, string? origin = null)
    {
        var uri = new Uri(
            server.Address, $"api/{Uri.EscapeDataString(key)}/annotations/{Uri.EscapeDataString(id)}/delete");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.TryAddWithoutValidation("Origin", origin ?? SameOrigin(server.Address));
        return await client.SendAsync(request);
    }

    private static string SameOrigin(Uri address) => address.GetLeftPart(UriPartial.Authority);

    private static string WriteTempPlan()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "charter-annotation-mgmt-" + Guid.NewGuid().ToString("N") + ".charter.md");
        File.WriteAllText(path, PlanMarkdown);
        return path;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-mgmt-sidecar-" + Guid.NewGuid().ToString("N"));
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

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Same as TryDelete: a leaked temp dir is harmless.
        }
    }
}
