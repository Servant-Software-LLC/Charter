using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Charter.Core;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Charter #221 at the review-log BRIDGE — the two places <c>ReviewLogBridge</c> calls
/// <c>ReviewLogStore.Read</c>, each of which takes its own reading of what came back.
///
/// <para>
/// <b>The view</b> (<c>ReviewLogBridge.BuildView</c> → <c>ReviewLogView.Build</c> → the JSON
/// <c>GET /api/review-log</c> serves) hands the panel a comment list. Today an unread <c>.review/</c> and a
/// plan nobody has commented on produce the same payload — zero comments, empty <c>unreadable</c>, no
/// notice — so the panel empties and focus lands on <c>&lt;body&gt;</c>, which is the reported bug.
/// </para>
///
/// <para>
/// <b>The comment lookup</b> (the plan calls it <c>FindComment</c>; it is spelled
/// <c>ReviewLogBridge.Find</c>, and <c>Edit</c> / <c>Retract</c> / <c>Resolve</c> are the verbs that reach
/// it) answers <i>"no such comment"</i> from the same undifferentiated empty fold — so a directory that was
/// never looked into is reported as a comment that does not exist.
/// </para>
///
/// <para>
/// Task 02 gave <c>ReviewLogRead</c> the three outcomes. Nothing on this side reads them yet, which is why
/// every test here is RED on the current tree — including the two <i>"still"</i> rows, whose job is to stop
/// the fix being bought by answering Unknown to everything.
/// </para>
///
/// Class trait (exact literal for this pair's filters): [Trait("Category","BridgeUnknown")].
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these assert JSON rather than a C# property.</b> The outcome crosses a language boundary no other
/// guardrail in this plan watches: it is added to a PascalCase C# record here, and <c>hydrateLog()</c> in
/// <c>sdk/charter-annotate.js</c> reads its camelCase name off the wire. Asserting the C# property would pin
/// only half of that and leave the two halves free to disagree on the spelling while every check stayed
/// green. So these serialize the view through <see cref="AnnotationApi.JsonOptions"/> — the same options
/// <c>ReviewServer.HandleReviewLog</c> serializes it with — and assert the property name that actually
/// reaches the browser.
/// </para>
/// <para>
/// <b>Why the lookup's answers are compared rather than named.</b> <c>Resolve</c>'s <c>bool</c> is read by
/// <c>ReviewServer.HandleAnnotationResolve</c>, which is outside the implementing task's write scope, so
/// the not-found answer has to stay the <c>false</c> that drives its 404. What these pin is therefore the
/// DISTINCTION — that an unread directory's answer is neither the not-found answer nor the settled-it
/// answer — not the mechanism that carries it.
/// </para>
/// </remarks>
[Trait("Category", "BridgeUnknown")]
public class BridgeUnknownTests : IDisposable
{
    private const string PlanMarkdown =
        "# A plan under team review\n" +
        "\n" +
        "An overview paragraph introducing the plan under review.\n" +
        "\n" +
        "The paragraph a teammate leaves a note against.\n";

    /// <summary>
    /// The wire name the SDK reads the outcome by — camelCase, because that is what
    /// <see cref="AnnotationApi.JsonOptions"/> (web defaults) puts on the wire for every other field of the
    /// view, and what <c>hydrateLog()</c> will branch on. Pinned as a literal on BOTH sides of the boundary
    /// so neither half can be renamed alone.
    /// </summary>
    private const string OutcomeWireName = "outcome";

    /// <summary>Logs were found and folded — the comments are the whole answer.</summary>
    private const string PresentToken = "present";

    /// <summary>The directory was read and holds no logs: nobody has commented, which is a FINDING.</summary>
    private const string EmptyToken = "empty";

    /// <summary>The read could not complete. Never evidence that nobody commented.</summary>
    private const string UnknownToken = "unknown";

    private static readonly ReviewAuthor Alice = new("Alice Ng", "alice@example.com");
    private static readonly ReviewAuthor Bob = new("Bob Chen", "bob@example.com");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "charter-bridge-unknown-" + Guid.NewGuid().ToString("N"));

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

    // ---- the view served on /api/review-log ---------------------------------------------------------------

    /// <summary>
    /// The panel's half of the defect. A <c>.review/</c> that is not there was never looked into, so the view
    /// the panel receives has to say so — an empty comment list with nothing beside it is the payload that
    /// tells <c>hydrateLog()</c> the reviewer's notes are gone, and the SDK cannot tell that from a plan
    /// nobody has commented on.
    /// </summary>
    [Fact]
    public void The_view_carries_the_unknown_outcome()
    {
        var plan = WritePlan("unread-view.charter.md");
        var bridge = BridgeFor(plan, Bob);
        Assert.False(
            Directory.Exists(bridge.Directory),
            "the case under test is a review directory that is NOT there");

        var view = Parse(ServedJson(bridge));

        Assert.Equal(UnknownToken, OutcomeOf(view));

        // ...and it still carries nothing, because nothing was learned. The outcome is the view saying what
        // its emptiness MEANS; it is never licence to invent entries.
        Assert.Empty(view.GetProperty("comments").EnumerateArray());
        Assert.Empty(view.GetProperty("diagnostics").EnumerateArray());
        Assert.Empty(view.GetProperty("unreadable").EnumerateArray());
    }

    /// <summary>
    /// The first discriminator. A read that found logs must serve exactly what it serves today, entry for
    /// entry — this change adds a state, it does not reshape the existing ones.
    /// </summary>
    /// <remarks>
    /// Every assertion here except the last already holds on the current tree; the outcome is the single
    /// clause that makes this red. That is the point of the ordering — the fix can only turn it green by
    /// leaving everything above it true, so "label every read Unknown" fails here instead of passing.
    /// </remarks>
    [Fact]
    public void A_present_read_still_serves_its_comments()
    {
        var plan = WritePlan("present-view.charter.md");
        var written = new ReviewLogWriter(plan, Alice).AppendCreate(
            Anchor(AnchorsOf(PlanMarkdown)[1], "the write path"),
            "The write path needs a retry budget.");

        var view = Parse(ServedJson(BridgeFor(plan, Bob)));

        var comment = Assert.Single(view.GetProperty("comments").EnumerateArray());
        Assert.Equal(written.Id, comment.GetProperty("id").GetString());
        Assert.Equal("The write path needs a retry budget.", comment.GetProperty("body").GetString());
        Assert.Equal(Alice.Email, comment.GetProperty("authorEmail").GetString());
        Assert.Equal("Alice Ng", comment.GetProperty("authorName").GetString());
        Assert.Equal(ReviewActors.Human, comment.GetProperty("actor").GetString());
        Assert.Equal(ReviewStatusTokens.Open, comment.GetProperty("status").GetString());

        // The anchor is a real block of this plan, so it still resolves rather than orphaning.
        Assert.Equal(ReviewStatusTokens.AnchorResolved, comment.GetProperty("anchorStatus").GetString());

        // Bob is reading Alice's note, so it is not his to retract — the identity the panel branches on.
        Assert.False(comment.GetProperty("mine").GetBoolean());
        Assert.Equal(Bob.Email, view.GetProperty("selfEmail").GetString());
        Assert.Empty(view.GetProperty("unreadable").EnumerateArray());

        Assert.Equal(PresentToken, OutcomeOf(view));
    }

    /// <summary>
    /// The seam nothing else in this plan can see. The outcome is added to a PascalCase C# record on this
    /// side of the boundary and read by its camelCase JSON name on the other, and the browser-level tests
    /// cannot tell a server that never emits the field from one that emits it under a different name — a
    /// fabricated response body agrees with whatever the test made up. So the name and the three tokens are
    /// pinned HERE, as the bytes, against the same serializer options the route uses.
    /// </summary>
    [Fact]
    public void The_outcome_serializes_under_the_wire_name_the_SDK_reads()
    {
        var plan = WritePlan("wire.charter.md");
        new ReviewLogWriter(plan, Alice).AppendCreate(
            Anchor(AnchorsOf(PlanMarkdown)[1]), "A note that reaches the panel over the wire.");

        var present = ServedJson(BridgeFor(plan, Bob));
        var empty = Serialize(ReviewLogView.Build(ReviewLogRead.Empty, PlanMarkdown, Bob.Email));
        var unknown = Serialize(ReviewLogView.Build(ReviewLogRead.Unknown, PlanMarkdown, Bob.Email));

        // camelCase, like every other field of this view — the C# property name must not reach the SDK.
        Assert.Contains("\"" + OutcomeWireName + "\"", unknown, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Outcome\"", unknown, StringComparison.Ordinal);

        // All three tokens, spelled out. `empty` and `unknown` are the pair the SDK has to tell apart, and a
        // number would leave the browser branching on an ordinal that reorders the day a member is inserted.
        Assert.Equal(PresentToken, OutcomeOf(Parse(present)));
        Assert.Equal(EmptyToken, OutcomeOf(Parse(empty)));
        Assert.Equal(UnknownToken, OutcomeOf(Parse(unknown)));
    }

    // ---- the comment lookup behind edit / retract / resolve -----------------------------------------------

    /// <summary>
    /// The lookup's half of the defect: <i>"I could not read the log"</i> answered as <i>"there is no such
    /// comment"</i>. The comment is real and the reviewer is looking straight at it in the panel; the
    /// directory was momentarily not there, which is a fact about the read and not about the comment.
    /// </summary>
    /// <remarks>
    /// Asserted as a three-way distinction because both wrong answers are reachable and both are lies. The
    /// bridge must not answer the way it answers a genuinely absent id (the panel is told its note is gone),
    /// and it must not answer the way it answers a comment it actually settled (the panel is told a resolve
    /// happened that never did). The final assertion closes the third route: manufacturing a durable,
    /// teammate-visible record on the strength of a read that failed.
    /// </remarks>
    [Fact]
    public void FindComment_does_not_report_not_found_on_an_unread_directory()
    {
        // (a) What a resolve the bridge could genuinely make answers.
        var settled = WritePlan("settled.charter.md");
        var target = new ReviewLogWriter(settled, Alice).AppendCreate(
            Anchor(AnchorsOf(PlanMarkdown)[1]), "Is Postgres right here, given the latency budget?");
        var resolved = AnswerOfResolve(BridgeFor(settled, Bob), target.Id);

        // (b) What a directory the bridge genuinely READ answers for an id it does not hold.
        var populated = WritePlan("populated.charter.md");
        new ReviewLogWriter(populated, Alice).AppendCreate(
            Anchor(AnchorsOf(PlanMarkdown)[1]), "A note, but not the one being asked for.");
        var notFound = AnswerOfResolve(BridgeFor(populated, Bob), "cmt_no_log_here_carries_this_id");

        // (c) The case under test: the directory is not there, so the read learned nothing about ANY id.
        var unread = WritePlan("unread-find.charter.md");
        var bridge = BridgeFor(unread, Bob);
        Assert.False(
            Directory.Exists(bridge.Directory),
            "the case under test is a review directory that is NOT there");

        var onUnread = AnswerOfResolve(bridge, target.Id);

        Assert.NotEqual(notFound, onUnread);
        Assert.NotEqual(resolved, onUnread);

        // A read that failed is no basis for a record teammates will pull. The directory is created lazily on
        // the first append (plan-03 §5.0), so its continued absence is the whole proof that nothing was written.
        Assert.False(
            Directory.Exists(bridge.Directory),
            "a lookup against an unread directory must not append a record — there is no evidence to write one against");
    }

    /// <summary>
    /// The second discriminator, and the reason the fix cannot be bought by treating every read as Unknown: a
    /// comment id that no log carries is genuinely not there, and saying so is correct. A directory that was
    /// READ — whether it holds other people's logs or none at all — is evidence, and the answer built on it
    /// must stay the plain <c>false</c> the server's 404 branch reads.
    /// </summary>
    /// <remarks>
    /// Red on the current tree for one reason only: today that correct not-found is the SAME value the bridge
    /// hands back for a directory it never opened, so the last assertion cannot hold. Everything above it
    /// already passes and must keep passing.
    /// </remarks>
    [Fact]
    public void FindComment_still_reports_not_found_for_a_genuinely_absent_id()
    {
        const string absentId = "cmt_no_record_anywhere_carries_this_id";

        // A directory holding a teammate's log — read, folded, and simply without this id in it.
        var populated = WritePlan("populated-absent.charter.md");
        new ReviewLogWriter(populated, Alice).AppendCreate(
            Anchor(AnchorsOf(PlanMarkdown)[1]), "Is Postgres right here, given the latency budget?");
        var populatedBridge = BridgeFor(populated, Bob);

        // ...and one that is there and holds NO logs: "nobody has commented" is a finding too, so a miss
        // against it is every bit as much a real not-found as a miss against a populated fold.
        var emptied = WritePlan("emptied-absent.charter.md");
        var emptiedBridge = BridgeFor(emptied, Bob);
        Directory.CreateDirectory(emptiedBridge.Directory);

        Assert.False(populatedBridge.Resolve(absentId));
        Assert.False(emptiedBridge.Resolve(absentId));

        // And that answer must be the bridge's NOT-FOUND answer, not the one it owes a directory it could not
        // read. Today the two are one value, which is the conflation this whole plan exists to undo.
        var unread = WritePlan("unread-absent.charter.md");
        var unreadBridge = BridgeFor(unread, Bob);
        Assert.False(
            Directory.Exists(unreadBridge.Directory),
            "the comparison needs a review directory that is NOT there");

        Assert.NotEqual(AnswerOfResolve(populatedBridge, absentId), AnswerOfResolve(unreadBridge, absentId));
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private string WritePlan(string fileName)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, PlanMarkdown);
        return path;
    }

    /// <summary>
    /// A bridge reading as <paramref name="self"/>. It carries a writer because the lookup short-circuits to
    /// null without one, which would answer these questions before the read is ever consulted — and because
    /// a Charter that cannot write is a Charter whose panel has no edit/retract/resolve to refuse.
    /// </summary>
    private static ReviewLogBridge BridgeFor(string plan, ReviewAuthor self)
        => new(plan, new ReviewLogWriter(plan, self));

    /// <summary>
    /// The bytes <c>GET /api/review-log</c> puts on the wire: the projection, through the SAME serializer
    /// options <c>ReviewServer.HandleReviewLog</c> uses, so the property names asserted here are the ones the
    /// browser SDK actually receives.
    /// </summary>
    private static string ServedJson(ReviewLogBridge bridge) => Serialize(bridge.BuildView(PlanMarkdown));

    private static string Serialize(ReviewLogView view)
        => JsonSerializer.Serialize(view, AnnotationApi.JsonOptions);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The outcome token off the wire, read by the name the SDK reads it by — case-sensitively, because
    /// <c>JsonDocument</c> is, and so is the browser.
    /// </summary>
    private static string OutcomeOf(JsonElement view)
    {
        Assert.True(
            view.TryGetProperty(OutcomeWireName, out var outcome),
            $"the served view carries no '{OutcomeWireName}' property, so the panel has nothing to branch on: "
                + "an unread review directory reaches the SDK as a view byte-identical to an empty one");

        Assert.Equal(JsonValueKind.String, outcome.ValueKind);
        return outcome.GetString()!;
    }

    /// <summary>
    /// What the bridge ANSWERED when asked to resolve <paramref name="commentId"/>: the value it returned,
    /// boxed, or the type of what it raised.
    /// </summary>
    /// <remarks>
    /// Deliberately untyped. <c>Resolve</c>'s <c>bool</c> is read by <c>ReviewServer.HandleAnnotationResolve</c>
    /// — a file the implementing task may not edit — so the not-found answer has to remain the <c>false</c>
    /// that drives its 404, and any third answer has to arrive by some other route. These tests pin that the
    /// answers are DISTINGUISHABLE and leave the route to the implementation.
    /// </remarks>
    private static object AnswerOfResolve(ReviewLogBridge bridge, string commentId)
    {
        try
        {
            return bridge.Resolve(commentId);
        }
        catch (Exception raised)
        {
            return raised.GetType();
        }
    }

    private static IReadOnlyList<string> AnchorsOf(string markdown)
        => SourceMap.Build(markdown).Anchors.OrderBy(a => a, StringComparer.Ordinal).ToList();

    private static ReviewAnchor Anchor(string blockId, string? quote = null)
        => new(blockId, "element", quote, null);
}
