using System.Text.Json;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// The <c>charter poll</c> envelope grew TWO independent additive fields from two different features — the
/// round hand-off's <c>reviewSubmitted</c>/<c>reviewSubmission</c> pair (Charter #62) and the git-mediated
/// review log's <c>source</c> (<c>docs/plans/03-git-mediated-team-review.md</c> §5). They were built against
/// different baselines, so nothing until now pinned that they COEXIST, or that adding them left the shipped
/// pending-queue shape alone.
/// </summary>
/// <remarks>
/// The load-bearing claim is the last one: an annotation drained from the pending queue must not grow a
/// <c>review</c> key. <see cref="ReviewAttribution"/> only exists for a comment read out of a committed log,
/// and <c>Annotation.Review</c> carries
/// <see cref="System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull"/> precisely so the shipped
/// drain shape is byte-for-byte what it was. Lose that attribute and every existing consumer starts seeing a
/// <c>"review": null</c> it was never promised.
/// </remarks>
public sealed class PollEnvelopeShapeTests
{
    private static readonly PollSession Session =
        new("http://127.0.0.1:1234/", "/plans/tenant.charter.md", "tenant.charter.md");

    private static readonly Annotation Pending = new(
        Id: "a1",
        Kind: AnnotationKind.Element,
        AnchorId: "b92bb0c5fe0d",
        Note: "Spell out the acceptance criteria.",
        SourceLine: 12);

    [Fact]
    public void A_pending_queue_annotation_carries_no_review_key()
    {
        var root = Parse(PollEnvelope.Serialize(Session, new[] { Pending }, Array.Empty<Answer>()));
        var annotation = root.GetProperty("annotations")[0];

        Assert.False(
            annotation.TryGetProperty("review", out _),
            "a pending-queue annotation must serialize EXACTLY as before — no 'review' key, not even null");
        Assert.Equal("a1", annotation.GetProperty("id").GetString());
        Assert.Equal(12, annotation.GetProperty("sourceLine").GetInt32());
        Assert.Equal("resolved", annotation.GetProperty("anchorStatus").GetString());
    }

    [Fact]
    public void A_review_log_annotation_carries_its_attribution()
    {
        var fromLog = Pending with
        {
            Review = new ReviewAttribution("Bob Chen", "bob@example.com", "human", "contested", "2026-07-26T10:45:12Z"),
        };

        var root = Parse(PollEnvelope.Serialize(
            null, new[] { fromLog }, Array.Empty<Answer>(), source: PollEnvelope.ReviewLogSource));

        var review = root.GetProperty("annotations")[0].GetProperty("review");
        Assert.Equal("bob@example.com", review.GetProperty("authorEmail").GetString());
        Assert.Equal("human", review.GetProperty("actor").GetString());

        // The status is the field an agent must not ignore: contested is NOT resolved (§4.2).
        Assert.Equal("contested", review.GetProperty("status").GetString());
    }

    [Fact]
    public void Source_defaults_to_session_and_the_handoff_pair_is_reported_alongside_it()
    {
        var handoff = new ReviewSubmission(7, DateTimeOffset.UnixEpoch, Annotations: 2, Answers: 1);
        var root = Parse(PollEnvelope.Serialize(
            Session, new[] { Pending }, Array.Empty<Answer>(), drainError: null, reviewSubmission: handoff));

        // Both additions ride the same envelope: neither feature's field displaced the other's.
        Assert.Equal(PollEnvelope.SessionSource, root.GetProperty("source").GetString());
        Assert.True(root.GetProperty("reviewSubmitted").GetBoolean());
        Assert.Equal(7, root.GetProperty("reviewSubmission").GetProperty("sequence").GetInt64());
        Assert.Equal(2, root.GetProperty("reviewSubmission").GetProperty("annotations").GetInt32());
    }

    [Fact]
    public void A_review_log_envelope_has_no_session_and_reports_no_handoff()
    {
        // A hand-off is a property of a LIVE session, so the server-less log read can never carry one. Pinned
        // so a future change cannot quietly start reporting a stale hand-off on the session-less path.
        var root = Parse(PollEnvelope.Serialize(
            null, Array.Empty<Annotation>(), Array.Empty<Answer>(), source: PollEnvelope.ReviewLogSource));

        Assert.Equal(JsonValueKind.Null, root.GetProperty("session").ValueKind);
        Assert.Equal(PollEnvelope.ReviewLogSource, root.GetProperty("source").GetString());
        Assert.False(root.GetProperty("reviewSubmitted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("reviewSubmission").ValueKind);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
