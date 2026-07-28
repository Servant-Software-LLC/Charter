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
/// Step 3 of <c>docs/plans/03-git-mediated-team-review.md</c>: <c>charter review</c> reads EVERY author's log
/// beside the plan, folds them, and serves that state to the page — so the panel shows a teammate's comments,
/// not only this machine's pending queue. The panel actions (comment / edit / retract / resolve) append
/// records to this author's log.
/// </summary>
/// <remarks>
/// The one route added here is <c>GET /api/review-log</c>, which serves a PROJECTION the server computed. The
/// absence of a static-file branch is asserted directly (<see cref="ReviewLogFiles_AreNeverServedOverHttp"/>):
/// the confinement root is the plan's own DIRECTORY, so a static branch would make every sibling file under
/// <c>docs/plans/</c> a key-gated HTTP-readable resource in one line.
/// </remarks>
[Trait("Category", "ReviewLogPanel")]
public class ReviewLogPanelTests : IDisposable
{
    private const string PlanMarkdown =
        "# Charter Team Review Plan\n" +
        "\n" +
        "An overview paragraph introducing the plan under review.\n" +
        "\n" +
        "The reviewer annotates this distinctive target paragraph for the round-trip.\n";

    private static readonly ReviewAuthor Alice = new("Alice Ng", "alice@example.com");
    private static readonly ReviewAuthor Bob = new("Bob Chen", "bob@example.com");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "charter-review-panel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is harmless.
        }

        GC.SuppressFinalize(this);
    }

    // ---- reading every author's log ----------------------------------------------------------------------

    /// <summary>
    /// The payoff of the whole design: two people's separate files fold into ONE panel view, each entry
    /// carrying its own author and actor.
    /// </summary>
    [Fact]
    public async Task TwoAuthorsLogs_FoldIntoOnePanelView_EachWithItsAuthorAndActor()
    {
        var plan = WritePlan();
        var anchors = AnchorsOf(PlanMarkdown);

        new ReviewLogWriter(plan, Alice).AppendCreate(Anchor(anchors[1]), "Is Postgres right here?");
        new ReviewLogWriter(plan, Bob) { }.AppendCreate(Anchor(anchors[2], quote: "the write path"),
            "The write path needs a retry budget.", ReviewActors.Agent);

        using var server = StartServer(plan, Alice, out var session);
        using var client = new HttpClient();

        var view = await ReadViewAsync(client, server, session.Key.Value);
        var comments = view.GetProperty("comments").EnumerateArray().ToList();
        Assert.Equal(2, comments.Count);

        var fromAlice = comments.Single(c => c.GetProperty("authorEmail").GetString() == Alice.Email);
        Assert.Equal("Alice Ng", fromAlice.GetProperty("authorName").GetString());
        Assert.Equal("human", fromAlice.GetProperty("actor").GetString());
        Assert.Equal("open", fromAlice.GetProperty("status").GetString());
        Assert.True(fromAlice.GetProperty("mine").GetBoolean());

        var fromBob = comments.Single(c => c.GetProperty("authorEmail").GetString() == Bob.Email);
        Assert.Equal("Bob Chen", fromBob.GetProperty("authorName").GetString());
        Assert.Equal("agent", fromBob.GetProperty("actor").GetString());
        Assert.False(fromBob.GetProperty("mine").GetBoolean());
        Assert.Equal("The write path needs a retry budget.", fromBob.GetProperty("body").GetString());
    }

    /// <summary>
    /// A contested comment shows BOTH sides with their authors. The fold hands over both precisely so the
    /// disagreement is visible instead of being ordered away by a clock nobody synchronized (§4.2).
    /// </summary>
    [Fact]
    public async Task ContestedComment_RendersBothSidesWithTheirAuthors_AndIsNotResolved()
    {
        var plan = WritePlan();
        var anchors = AnchorsOf(PlanMarkdown);

        var alice = new ReviewLogWriter(plan, Alice);
        var comment = alice.AppendCreate(Anchor(anchors[1]), "Is Postgres right here?");

        // Neither settlement observed the other: prev is null on both, so they are concurrent by construction
        // — exactly the offline-reviewer case the design refuses to order by timestamp.
        var resolve = new ReviewLogWriter(plan, Bob).AppendResolve(comment.Id, prev: null);
        var reopen = alice.Append(new ReviewRecord
        {
            Version = ReviewRecord.CurrentVersion,
            Id = ReviewLogWriter.NewId(ReviewOpKind.Reopen),
            Op = ReviewOps.Token(ReviewOpKind.Reopen),
            Author = Alice,
            Target = comment.Id,
        });

        using var server = StartServer(plan, Alice, out var session);
        using var client = new HttpClient();

        var view = await ReadViewAsync(client, server, session.Key.Value);
        var folded = Assert.Single(
            view.GetProperty("comments").EnumerateArray().ToList(),
            c => c.GetProperty("id").GetString() == comment.Id);

        Assert.Equal("contested", folded.GetProperty("status").GetString());

        var sides = folded.GetProperty("sides").EnumerateArray().ToList();
        Assert.Equal(2, sides.Count);
        Assert.Contains(sides, s => s.GetProperty("id").GetString() == resolve.Id
            && s.GetProperty("op").GetString() == "resolve"
            && s.GetProperty("authorEmail").GetString() == Bob.Email);
        Assert.Contains(sides, s => s.GetProperty("id").GetString() == reopen.Id
            && s.GetProperty("op").GetString() == "reopen"
            && s.GetProperty("authorEmail").GetString() == Alice.Email);
    }

    /// <summary>
    /// A retract hides the BODY and keeps the THREAD: replies are other people's words and are never removed
    /// by someone else's retract (§4.2).
    /// </summary>
    [Fact]
    public async Task RetractedComment_HidesItsBody_ButKeepsItsReplies()
    {
        var plan = WritePlan();
        var anchors = AnchorsOf(PlanMarkdown);

        var alice = new ReviewLogWriter(plan, Alice);
        var comment = alice.AppendCreate(Anchor(anchors[1]), "a candid objection Alice later withdraws");

        var bob = new ReviewLogWriter(plan, Bob);
        bob.Append(new ReviewRecord
        {
            Version = ReviewRecord.CurrentVersion,
            Id = ReviewLogWriter.NewId(ReviewOpKind.Reply),
            Op = ReviewOps.Token(ReviewOpKind.Reply),
            Author = Bob,
            Target = comment.Id,
            Body = "Agreed, and here is why.",
        });

        alice.AppendRetract(comment.Id, prev: null);

        using var server = StartServer(plan, Alice, out var session);
        using var client = new HttpClient();

        var folded = Assert.Single(
            (await ReadViewAsync(client, server, session.Key.Value)).GetProperty("comments").EnumerateArray());

        Assert.Equal("retracted", folded.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, folded.GetProperty("body").ValueKind);

        var reply = Assert.Single(folded.GetProperty("replies").EnumerateArray());
        Assert.Equal("Agreed, and here is why.", reply.GetProperty("body").GetString());
        Assert.Equal(Bob.Email, reply.GetProperty("authorEmail").GetString());
        Assert.False(reply.GetProperty("retracted").GetBoolean());
    }

    /// <summary>
    /// An anchor resolves by EXACT block-id match or it is orphaned (§4.3) — no ladder, no guess. The orphan
    /// still lists, with its quote, and "orphaned" is a NEUTRAL fact: it is never reported as "addressed",
    /// because folding a <c>:::question</c> answer orphans every comment on that block though nobody
    /// addressed anything.
    /// </summary>
    [Fact]
    public async Task OrphanedComment_ListsWithItsQuote_AndIsNeverCalledAddressed()
    {
        var plan = WritePlan();

        new ReviewLogWriter(plan, Alice).AppendCreate(
            new ReviewAnchor("b-no-such-block-in-this-plan", "element", "the read path will be built after", null),
            "this block has since changed");

        using var server = StartServer(plan, Alice, out var session);
        using var client = new HttpClient();

        var raw = await client.GetStringAsync(ViewUrl(server, session.Key.Value));
        using var document = JsonDocument.Parse(raw);
        var folded = Assert.Single(document.RootElement.GetProperty("comments").EnumerateArray());

        Assert.Equal("orphaned", folded.GetProperty("anchorStatus").GetString());
        Assert.Equal(JsonValueKind.Null, folded.GetProperty("sourceLine").ValueKind);
        Assert.Equal("the read path will be built after", folded.GetProperty("quote").GetString());
        Assert.Equal("this block has since changed", folded.GetProperty("body").GetString());

        // The status dimension is untouched by orphaning: the comment is still OPEN, never "addressed".
        Assert.Equal("open", folded.GetProperty("status").GetString());
        Assert.DoesNotContain("addressed", raw, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A <c>git pull</c> landing a teammate's log mid-session must REFRESH the panel, not leave it silently
    /// showing the fold taken at startup. The server watches the <c>.review/</c> directory alongside the plan
    /// file and pushes a distinct <c>review-log</c> event, so the panel re-reads without navigating the page
    /// (which would discard a half-typed note).
    /// </summary>
    [Fact]
    public async Task ALogArrivingWhileTheServerRuns_PushesAReviewLogEventAndShowsUpInTheView()
    {
        var plan = WritePlan();
        var anchors = AnchorsOf(PlanMarkdown);

        using var server = StartServer(plan, Alice, out var session);
        using var client = new HttpClient();

        // The startup fold is empty...
        Assert.Empty((await ReadViewAsync(client, server, session.Key.Value)).GetProperty("comments").EnumerateArray());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var events = ReadEventsUntilAsync(server, session.Key.Value, "review-log", cts.Token);

        // ...then a teammate's log lands beside the plan, exactly as a pull would deliver it. Re-written on a
        // cadence so the test never depends on one write landing after the watcher is armed.
        var bob = new ReviewLogWriter(plan, Bob);
        var pulled = bob.AppendCreate(Anchor(anchors[1]), "arriving mid-session from a teammate");
        while (!events.IsCompleted && !cts.IsCancellationRequested)
        {
            await Task.Delay(150, CancellationToken.None);
            if (events.IsCompleted)
            {
                break;
            }

            bob.AppendCreate(Anchor(anchors[2]), "and another, to re-arm the watcher");
        }

        Assert.True(await events, "the server must push a `review-log` event when `.review/` changes");

        var comments = (await ReadViewAsync(client, server, session.Key.Value))
            .GetProperty("comments").EnumerateArray().ToList();
        Assert.Contains(comments, c => c.GetProperty("id").GetString() == pulled.Id);
        Assert.All(comments, c => Assert.Equal(Bob.Email, c.GetProperty("authorEmail").GetString()));
    }

    [Fact]
    public async Task ReviewLogRoute_WithWrongOrMissingKey_IsRefused()
    {
        var plan = WritePlan();
        using var server = StartServer(plan, Alice, out var session);
        using var client = new HttpClient();

        using var wrongKey = await client.GetAsync(new Uri(server.Address, "api/review-log?key=not-the-key"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);

        using var noKey = await client.GetAsync(new Uri(server.Address, "api/review-log"));
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
    }

    /// <summary>
    /// The security constraint stated in the build order, asserted rather than assumed: the review logs are
    /// read SERVER-SIDE into a projection, and there is no static-file branch that could hand a caller the raw
    /// bytes of a file that merely happens to sit under the served root.
    /// </summary>
    [Fact]
    public async Task ReviewLogFiles_AreNeverServedOverHttp()
    {
        var plan = WritePlan();
        var anchors = AnchorsOf(PlanMarkdown);
        var alice = new ReviewLogWriter(plan, Alice);
        alice.AppendCreate(Anchor(anchors[1]), "a secret-ish review note");

        // A sibling file under the served root (the plan's own directory) that is not the plan.
        var sibling = Path.Combine(Path.GetDirectoryName(plan)!, "secrets.txt");
        await File.WriteAllTextAsync(sibling, "SIBLING-FILE-CONTENT");

        using var server = StartServer(plan, Alice, out var session);
        using var client = new HttpClient();

        var relative = Path.GetFileName(alice.ReviewDirectory) + "/" + Path.GetFileName(alice.LogPath);
        foreach (var path in new[] { relative, "secrets.txt" })
        {
            var body = await client.GetStringAsync(new Uri(server.Address, path + "?key=" + session.Key.Value));

            // Every path under the root renders the PLAN; nothing serves a file's bytes.
            Assert.Contains("Charter Team Review Plan", body, StringComparison.Ordinal);
            Assert.DoesNotContain("SIBLING-FILE-CONTENT", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"op\":\"create\"", body, StringComparison.Ordinal);
        }
    }

    // ---- the panel's actions append records --------------------------------------------------------------

    /// <summary>
    /// The in-page composer now PERSISTS: a submitted annotation becomes a durable <c>create</c> record in
    /// this author's log, and the record's id IS the annotation's id, so the panel's later edit / retract /
    /// resolve address one identity. The existing pre-drain queue is dual-written and untouched.
    /// </summary>
    [Fact]
    public async Task PostingAnAnnotation_AppendsACreateRecord_AndDualWritesThePendingQueue()
    {
        var plan = WritePlan();
        using var server = StartServer(plan, Alice, out var session);
        using var client = new HttpClient();

        var created = await PostAnnotationAsync(client, server, session, "Please clarify this paragraph.");

        // The log has it, attributed to this author...
        var folded = Assert.Single(ReviewLogStore.ReadForPlan(plan).State.Comments);
        Assert.Equal(created.Id, folded.Id);
        Assert.Equal(Alice.Email, folded.Author.Email);
        Assert.Equal("Please clarify this paragraph.", folded.Body);
        Assert.Equal(created.AnchorId, folded.Anchor.BlockId);

        // `anchor.base` is stamped at write time — records are immutable, so a record that never carried the
        // plan's content hash could never acquire one, and the orphan diff (build-order step 5) needs it.
        Assert.StartsWith("sha256:", folded.Anchor.Base!, StringComparison.Ordinal);
        Assert.Equal("sha256:".Length + 64, folded.Anchor.Base!.Length);

        // ...and the SHIPPED drain path still delivers it, unchanged.
        var drained = await PollAsync(client, server, session.Key.Value);
        Assert.Equal(created.Id, Assert.Single(drained).GetProperty("id").GetString());
    }

    [Fact]
    public async Task EditingAnAnnotation_AppendsAnEditRecord_AndASecondEditIsNotConcurrent()
    {
        var plan = WritePlan();
        using var server = StartServer(plan, Alice, out var session);
        using var client = new HttpClient();

        var created = await PostAnnotationAsync(client, server, session, "first draft of the note");

        using (var first = await PostAsync(client, server, session.Key.Value, $"annotations/{created.Id}",
            "{\"note\":\"the edited note\"}"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var second = await PostAsync(client, server, session.Key.Value, $"annotations/{created.Id}",
            "{\"note\":\"the twice-edited note\"}"))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        // `prev` came from the fold's state heads, so the second edit OBSERVED the first — no concurrent-edit
        // diagnostic, and the latest body stands rather than reverting to the last agreed one.
        var read = ReviewLogStore.ReadForPlan(plan);
        Assert.Empty(read.State.Diagnostics);
        Assert.Equal("the twice-edited note", Assert.Single(read.State.Comments).Body);
    }

    [Fact]
    public async Task DeletingAnAnnotation_AppendsARetractRecord_WhichHidesTheBodyAndKeepsTheEntry()
    {
        var plan = WritePlan();
        using var server = StartServer(plan, Alice, out var session);
        using var client = new HttpClient();

        var created = await PostAnnotationAsync(client, server, session, "a note the reviewer withdraws");

        using var response = await PostAsync(
            client, server, session.Key.Value, $"annotations/{created.Id}/delete", body: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var folded = Assert.Single(ReviewLogStore.ReadForPlan(plan).State.Comments);
        Assert.Equal(ReviewCommentStatus.Retracted, folded.Status);
        Assert.Null(folded.Body);

        // ...and the pre-drain queue was dual-written, so the agent never receives the withdrawn note.
        Assert.Empty(await PollAsync(client, server, session.Key.Value));
    }

    /// <summary>Resolve is open to ANYONE — review is collaborative — and always attributed (§4.2).</summary>
    [Fact]
    public async Task ResolvingAnotherAuthorsComment_AppendsAResolveRecordAttributedToTheResolver()
    {
        var plan = WritePlan();
        var anchors = AnchorsOf(PlanMarkdown);
        var bobs = new ReviewLogWriter(plan, Bob).AppendCreate(Anchor(anchors[1]), "Bob's objection.");

        using var server = StartServer(plan, Alice, out var session);
        using var client = new HttpClient();

        using var response = await PostAsync(
            client, server, session.Key.Value, $"annotations/{bobs.Id}/resolve", body: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var folded = Assert.Single(ReviewLogStore.ReadForPlan(plan).State.Comments);
        Assert.Equal(ReviewCommentStatus.Resolved, folded.Status);
        Assert.Equal(Alice.Email, Assert.Single(folded.ResolutionRecords).Author.Email);
    }

    /// <summary>
    /// Retract is valid ONLY from the comment's own author — otherwise a teammate could silently delete a
    /// blocking objection (§4.2). The route refuses rather than writing a record the fold would reject anyway.
    /// </summary>
    [Fact]
    public async Task DeletingAnotherAuthorsComment_IsRefused_AndWritesNothing()
    {
        var plan = WritePlan();
        var anchors = AnchorsOf(PlanMarkdown);
        var bobs = new ReviewLogWriter(plan, Bob).AppendCreate(Anchor(anchors[1]), "Bob's blocking objection.");

        using var server = StartServer(plan, Alice, out var session);
        using var client = new HttpClient();

        using var response = await PostAsync(
            client, server, session.Key.Value, $"annotations/{bobs.Id}/delete", body: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var folded = Assert.Single(ReviewLogStore.ReadForPlan(plan).State.Comments);
        Assert.Equal(ReviewCommentStatus.Open, folded.Status);
        Assert.Equal("Bob's blocking objection.", folded.Body);
        Assert.DoesNotContain(
            ReviewLogPaths.EnumerateLogs(ReviewLogPaths.DirectoryForPlan(plan)),
            p => Path.GetFileName(p).StartsWith("alice-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithNoWriterConfigured_TheViewIsEmptyAndTheShippedQueueBehaviourIsUnchanged()
    {
        var plan = WritePlan();
        var session = ReviewSession.Create(plan);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        using var client = new HttpClient();

        var created = await PostAnnotationAsync(client, server, session, "a local-only note");

        var view = await ReadViewAsync(client, server, session.Key.Value);
        Assert.Empty(view.GetProperty("comments").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("selfEmail").ValueKind);
        Assert.False(Directory.Exists(ReviewLogPaths.DirectoryForPlan(plan)));

        // The pre-drain queue is exactly as it was before this slice — including the 404 after a drain.
        Assert.Single(await PollAsync(client, server, session.Key.Value));
        using var afterDrain = await PostAsync(
            client, server, session.Key.Value, $"annotations/{created.Id}/delete", body: null);
        Assert.Equal(HttpStatusCode.NotFound, afterDrain.StatusCode);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private string WritePlan()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "team.charter.md");
        File.WriteAllText(path, PlanMarkdown);
        return path;
    }

    private static ReviewServer StartServer(string plan, ReviewAuthor author, out ReviewSession session)
    {
        session = ReviewSession.Create(plan);
        return ReviewServer.Start(session, new ReviewServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            ReviewLog = new ReviewLogWriter(plan, author),
        });
    }

    private static IReadOnlyList<string> AnchorsOf(string markdown)
        => SourceMap.Build(markdown).Anchors.OrderBy(a => a, StringComparer.Ordinal).ToList();

    private static ReviewAnchor Anchor(string blockId, string? quote = null)
        => new(blockId, "element", quote, null);

    private static Uri ViewUrl(ReviewServer server, string key)
        => new UriBuilder(server.Address) { Path = "api/review-log", Query = "key=" + key }.Uri;

    private static async Task<JsonElement> ReadViewAsync(HttpClient client, ReviewServer server, string key)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync(ViewUrl(server, key)));
        return document.RootElement.Clone();
    }

    /// <summary>What the prompts route echoed back — read straight off the wire, not through a model type.</summary>
    private sealed record Created(string Id, string AnchorId);

    private static async Task<Created> PostAnnotationAsync(
        HttpClient client, ReviewServer server, ReviewSession session, string note)
    {
        var anchorId = SourceMap.Build(PlanMarkdown).Anchors.OrderBy(a => a, StringComparer.Ordinal).ElementAt(1);
        var payload = JsonSerializer.Serialize(new { kind = "element", anchorId, note });
        using var response = await PostAsync(client, server, session.Key.Value, "prompts", payload);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new Created(
            document.RootElement.GetProperty("id").GetString()!,
            document.RootElement.GetProperty("anchorId").GetString()!);
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client, ReviewServer server, string key, string route, string? body)
    {
        var url = new Uri(server.Address, $"api/{key}/{route}");
        var content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json");
        return client.PostAsync(url, content);
    }

    private static async Task<IReadOnlyList<JsonElement>> PollAsync(
        HttpClient client, ReviewServer server, string key)
    {
        var url = new UriBuilder(server.Address) { Path = "api/poll", Query = "key=" + key + "&wait=0" }.Uri;
        using var document = JsonDocument.Parse(await client.GetStringAsync(url));
        return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    /// <summary>
    /// Read the SSE stream until the named event arrives (or the token trips). Never a fixed sleep — the test
    /// waits on the server's own frame.
    /// </summary>
    private static async Task<bool> ReadEventsUntilAsync(
        ReviewServer server, string key, string eventName, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var url = new UriBuilder(server.Address) { Path = "events", Query = "key=" + key }.Uri;

        try
        {
            using var response = await client.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    return false;
                }

                if (line.StartsWith("event: ", StringComparison.Ordinal)
                    && string.Equals(line["event: ".Length..], eventName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The deadline elapsed; the assertion below reports it.
        }

        return false;
    }
}
