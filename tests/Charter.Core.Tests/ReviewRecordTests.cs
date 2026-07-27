using System.Text.Json;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// The review-record schema (<c>docs/plans/03-git-mediated-team-review.md</c> §4): what a line must carry,
/// what a malformed line does (reports, never throws at the fold), and the forward-compatibility property
/// that makes rules 6 and 7 possible — an older Charter must not destroy a newer Charter's data.
/// </summary>
[Trait("Category", "ReviewLog")]
public class ReviewRecordTests
{
    [Fact]
    public void Parse_CreateRecord_ReadsEveryDeclaredField()
    {
        const string line =
            """
            {"v":1,"id":"cmt_9f3a1c22","op":"create","ts":"2026-07-26T10:45:12Z",
             "author":{"name":"Alice Ng","email":"alice@example.com"},"actor":"human",
             "anchor":{"blockId":"b92bb0c5fe0d7b8448379","kind":"element",
                       "quote":"the read path will be built after","base":"sha256:1f4c"},
             "body":"Is Postgres right here, given the latency budget?"}
            """;

        // The design's own example record, whitespace and all: every field it declares must round-trip.
        var record = ReviewRecord.Parse(line.ReplaceLineEndings(" "));

        Assert.Equal(1, record.Version);
        Assert.Equal("cmt_9f3a1c22", record.Id);
        Assert.Equal("create", record.Op);
        Assert.Equal(ReviewOpKind.Create, record.OpKind);
        Assert.Equal("2026-07-26T10:45:12Z", record.Ts);
        Assert.Equal("Alice Ng", record.Author.Name);
        Assert.Equal("alice@example.com", record.Author.Email);
        Assert.Equal(ReviewActors.Human, record.Actor);
        Assert.Equal("b92bb0c5fe0d7b8448379", record.Anchor!.BlockId);
        Assert.Equal("element", record.Anchor.Kind);
        Assert.Equal("the read path will be built after", record.Anchor.Quote);
        Assert.Equal("sha256:1f4c", record.Anchor.Base);
        Assert.Equal("Is Postgres right here, given the latency budget?", record.Body);
    }

    [Fact]
    public void Parse_StateRecord_ReadsPrevAndTarget()
    {
        var record = ReviewRecord.Parse(Rec.Resolve("r1", target: "c1", author: Rec.Bob, prev: "o1"));

        Assert.Equal(ReviewOpKind.Resolve, record.OpKind);
        Assert.Equal("c1", record.Target);
        Assert.Equal("o1", record.Prev);
        Assert.True(ReviewOps.IsStateOp(record.OpKind));
    }

    [Fact]
    public void Parse_PrevNull_MeansObservedNothing()
    {
        var record = ReviewRecord.Parse(Rec.Resolve("r1", target: "c1", author: Rec.Bob, prev: null));

        Assert.Null(record.Prev);
    }

    [Fact]
    public void UnknownFields_SurviveRoundTrip_AtEveryLevel()
    {
        // A record written by a NEWER Charter: unknown members at the top level, inside author, and inside
        // anchor. Rules 6/7 only hold if an older Charter that ever rewrites a log preserves all of them.
        const string line =
            """
            {"v":1,"id":"c1","op":"create","ts":"2026-07-26T10:45:12Z","author":{"name":"Alice","email":"alice@example.com","handle":"@alice"},"actor":"human","anchor":{"blockId":"b1","kind":"element","quote":"q","base":"sha256:aa","region":{"start":3,"end":9}},"body":"why?","severity":"blocking","labels":["latency","cost"]}
            """;

        var round = ReviewRecord.Parse(ReviewRecord.Parse(line).ToJson());

        Assert.Equal("\"blocking\"", round.Extensions["severity"]);
        Assert.Equal("[\"latency\",\"cost\"]", round.Extensions["labels"]);
        Assert.Equal("\"@alice\"", round.Author.Extensions["handle"]);
        Assert.Equal("{\"start\":3,\"end\":9}", round.Anchor!.Extensions["region"]);
    }

    [Fact]
    public void ToJson_IsOneLineWithNoControlCharacters()
    {
        // The writer's hard rules (§3): one JSON object per line, never pretty-printed, no raw control bytes
        // (a NUL makes git treat the log as binary and bypass the merge driver).
        var record = ReviewRecord.Parse(Rec.Create("c1", Rec.Alice, body: "line one\nline two\ttabbed\u0000nul"));

        var json = record.ToJson();

        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain('\r', json);
        Assert.All(json, c => Assert.False(char.IsControl(c)));
        Assert.Equal("line one\nline two\ttabbed\u0000nul", ReviewRecord.Parse(json).Body);
    }

    [Fact]
    public void ToJson_IsCanonical_ForRecordsThatDifferOnlyInSpacingAndFieldOrder()
    {
        // Two byte-different lines that mean the same record must serialize identically, or the fold would
        // report an ordinary duplicate as a conflicting one.
        const string spaced = """{ "op": "create", "v": 1, "id": "c1", "author": { "email": "alice@example.com", "name": "Alice" }, "anchor": { "blockId": "b1" }, "body": "why?", "tags": [ 1, 2 ] }""";
        const string tight = """{"v":1,"id":"c1","op":"create","author":{"name":"Alice","email":"alice@example.com"},"anchor":{"blockId":"b1"},"body":"why?","tags":[1,2]}""";

        Assert.Equal(ReviewRecord.Parse(tight).ToJson(), ReviewRecord.Parse(spaced).ToJson());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"id":"c1","op":"create","author":{"email":"a@b.c"},"anchor":{"blockId":"b1"},"body":"x"}""")]
    [InlineData("""{"v":1,"op":"create","author":{"email":"a@b.c"},"anchor":{"blockId":"b1"},"body":"x"}""")]
    [InlineData("""{"v":1,"id":"c1","author":{"email":"a@b.c"},"anchor":{"blockId":"b1"},"body":"x"}""")]
    [InlineData("""{"v":1,"id":"c1","op":"create","anchor":{"blockId":"b1"},"body":"x"}""")]
    [InlineData("""{"v":1,"id":"c1","op":"create","author":{"name":"A"},"anchor":{"blockId":"b1"},"body":"x"}""")]
    [InlineData("""{"v":1,"id":"c1","op":"create","author":{"email":"a@b.c"},"body":"x"}""")]
    [InlineData("""{"v":1,"id":"c1","op":"create","author":{"email":"a@b.c"},"anchor":{"blockId":"b1"}}""")]
    [InlineData("""{"v":1,"id":"c1","op":"create","author":{"email":"a@b.c"},"anchor":{"kind":"element"},"body":"x"}""")]
    [InlineData("""{"v":1,"id":"r1","op":"reply","author":{"email":"a@b.c"},"body":"x"}""")]
    [InlineData("""{"v":1,"id":"r1","op":"resolve","author":{"email":"a@b.c"}}""")]
    public void TryParse_MalformedOrSchemaInvalidLine_ReportsInsteadOfThrowing(string line)
    {
        var parsed = ReviewRecord.TryParse(line, out var record, out var error);

        Assert.False(parsed);
        Assert.Null(record);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_UnknownVersion_SkipsThisVersionsOpSchema()
    {
        // A v2 "resolve" may not even have a "target" — this Charter does not know v2's shape, so validating
        // it against v1's schema would misreport a future record as malformed instead of letting the fold
        // retain-and-not-apply it (rule 7).
        const string line = """{"v":2,"id":"r1","op":"resolve","author":{"email":"bob@example.com"},"targets":["c1","c2"]}""";

        Assert.True(ReviewRecord.TryParse(line, out var record, out _));
        Assert.Equal(2, record!.Version);
        Assert.Equal(ReviewOpKind.Resolve, record.OpKind);
        Assert.Equal("[\"c1\",\"c2\"]", record.Extensions["targets"]);
    }

    [Fact]
    public void TryParse_UnknownOp_ParsesAndKeepsItsToken()
    {
        var record = ReviewRecord.Parse(Rec.Record("endorse", "e1", Rec.Bob, target: "c1"));

        Assert.Equal(ReviewOpKind.Unknown, record.OpKind);
        Assert.Equal("endorse", record.Op);
        Assert.Equal("endorse", ReviewRecord.Parse(record.ToJson()).Op);
    }

    [Fact]
    public void Actor_DefaultsToHumanAndKeepsAnUnknownToken()
    {
        const string noActor = """{"v":1,"id":"c1","op":"create","author":{"email":"a@b.c"},"anchor":{"blockId":"b1"},"body":"x"}""";

        Assert.Equal(ReviewActors.Human, ReviewRecord.Parse(noActor).Actor);
        Assert.Equal("swarm", ReviewRecord.Parse(Rec.Create("c1", Rec.Alice, actor: "swarm")).Actor);
        Assert.Equal(ReviewActors.Agent, ReviewRecord.Parse(Rec.Create("c2", Rec.Alice, actor: "agent")).Actor);
    }

    [Fact]
    public void Timestamp_IsParsedInvariantlyAndIsMachineIndependent()
    {
        // Same instant, two spellings, one of them offset-less: the parsed value must not depend on the
        // machine's time zone, because it orders threads for display on every teammate's machine.
        var zulu = ReviewRecord.Parse(Rec.Create("c1", Rec.Alice, ts: "2026-07-26T10:45:12Z")).Timestamp;
        var offset = ReviewRecord.Parse(Rec.Create("c2", Rec.Alice, ts: "2026-07-26T12:45:12+02:00")).Timestamp;
        var naive = ReviewRecord.Parse(Rec.Create("c3", Rec.Alice, ts: "2026-07-26T10:45:12")).Timestamp;

        Assert.Equal(zulu, offset);
        Assert.Equal(zulu, naive);
        Assert.Equal(TimeSpan.Zero, zulu!.Value.Offset);
        Assert.Null(ReviewRecord.Parse(Rec.Create("c4", Rec.Alice, ts: "whenever")).Timestamp);
    }

    [Fact]
    public void Ops_TokensRoundTripThroughBothDirections()
    {
        foreach (var kind in Enum.GetValues<ReviewOpKind>().Where(k => k != ReviewOpKind.Unknown))
        {
            Assert.Equal(kind, ReviewOps.Parse(ReviewOps.Token(kind)));
        }

        Assert.Equal(ReviewOpKind.Unknown, ReviewOps.Parse("endorse"));
        Assert.Equal(ReviewOpKind.Unknown, ReviewOps.Parse(null));
    }

    [Fact]
    public void Anchor_IsCarriedFaithfullyAndNeverResolved()
    {
        // §4.3: the fold does not resolve anchors (that needs the plan) and never re-binds a stale one. It
        // carries blockId + quote + base so the caller can match EXACTLY or render an informative orphan.
        var comment = Rec.Fold(Rec.Create("c1", Rec.Alice, blockId: "b92bb0c5")).OnlyComment();

        Assert.Equal("b92bb0c5", comment.Anchor.BlockId);
        Assert.Equal("element", comment.Anchor.Kind);
        Assert.Equal("the read path", comment.Anchor.Quote);
        Assert.Equal("sha256:1f4c", comment.Anchor.Base);
    }

    [Fact]
    public void ExtensionKeys_CannotCollideWithKnownMembersWhenWritten()
    {
        // A hand-built record whose extension bag shadows a known member must not emit a duplicate JSON key.
        var record = ReviewRecord.Parse(Rec.Create("c1", Rec.Alice)) with
        {
            Extensions = new Dictionary<string, string>(StringComparer.Ordinal) { ["body"] = "\"forged\"", ["ok"] = "true" },
        };

        using var document = JsonDocument.Parse(record.ToJson());

        Assert.Equal("Is Postgres right here?", document.RootElement.GetProperty("body").GetString());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
    }
}
