using System.Linq;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// The authoring agent's lean on a <c>:::question</c> (Charter #125). It has usually just read the code and
/// the trade-offs, so withholding which option it would choose makes the reviewer re-derive a conclusion it
/// already reached.
/// <para>
/// The load-bearing decision here is that it is a FIELD, not a <c>(Recommended)</c> suffix inside the option
/// text — which is what the convention looks like on screen and what a first pass would reach for. The suffix
/// breaks two things that only surface later, and both are asserted below: the option text is also the
/// submitted VALUE, and the option list is hashed into the question FINGERPRINT.
/// </para>
/// </summary>
[Trait("Category", "RecommendedOption")]
public class RecommendedOptionTests
{
    private static string Question(string extra = "") =>
        "# Plan\n\n:::question\n" +
        "{\"id\":\"db\",\"title\":\"Which datastore?\",\"mode\":\"single\"," +
        "\"options\":[\"Postgres\",\"DynamoDB\"],\"target\":\"human\"" + extra + "}\n" +
        ":::\n";

    [Fact]
    public void TheRecommendedOptionIsMarkedForTheReviewer()
    {
        var html = CharterRenderer.Render(Question(",\"recommended\":\"Postgres\""));

        Assert.Contains("Postgres (Recommended)", html);
        Assert.DoesNotContain("DynamoDB (Recommended)", html);
    }

    /// <summary>
    /// THE reason this is a field. The option's text is also its submitted value, so a marker authored into
    /// the option would be recorded as the human's decision — <c>"Postgres (Recommended)"</c> — and carried
    /// into the answer, the plan, and everything downstream of handoff.
    /// </summary>
    [Fact]
    public void TheMarkerNeverReachesTheSubmittedValue()
    {
        var html = CharterRenderer.Render(Question(",\"recommended\":\"Postgres\""));

        Assert.Contains("value=\"Postgres\"", html);
        Assert.DoesNotContain("value=\"Postgres (Recommended)\"", html);
    }

    /// <summary>
    /// The second reason. <see cref="QuestionIdentity.Fingerprint"/> hashes the OPTIONS, so a marker living in
    /// the option list would change the fingerprint — staling an answer the human already gave, which
    /// <c>charter resolve</c> then refuses to apply without <c>--apply-stale-answers</c>. An agent revising its
    /// own opinion must never invalidate a human's decision.
    /// </summary>
    [Fact]
    public void AddingOrWithdrawingARecommendation_DoesNotStaleAnAnswerAlreadyGiven()
    {
        var none = QuestionSpec.Parse(Body());
        var leaning = QuestionSpec.Parse(Body(",\"recommended\":\"Postgres\""));
        var changedMind = QuestionSpec.Parse(Body(",\"recommended\":\"DynamoDB\""));

        Assert.Equal(QuestionIdentity.Fingerprint(none), QuestionIdentity.Fingerprint(leaning));
        Assert.Equal(QuestionIdentity.Fingerprint(none), QuestionIdentity.Fingerprint(changedMind));

        // ...and the control: a real change to what was ASKED still stales, so the guard above is not just
        // "the fingerprint ignores everything".
        var renamedOption = QuestionSpec.Parse(
            "{\"id\":\"db\",\"title\":\"Which datastore?\",\"mode\":\"single\"," +
            "\"options\":[\"Postgres 16\",\"DynamoDB\"],\"target\":\"human\"}");
        Assert.NotEqual(QuestionIdentity.Fingerprint(none), QuestionIdentity.Fingerprint(renamedOption));
    }

    /// <summary>
    /// A recommendation naming no declared option — the option was renamed or dropped — degrades to "no
    /// recommendation". Failing the block would turn an optional authoring hint into a way to break a plan.
    /// </summary>
    [Fact]
    public void ARecommendationMatchingNoOption_IsIgnored_NotAnError()
    {
        var spec = QuestionSpec.Parse(Body(",\"recommended\":\"Cassandra\""));

        Assert.Null(spec.Recommended);
        Assert.True(QuestionSpec.Validate(Body(",\"recommended\":\"Cassandra\"")).Ok);
        Assert.DoesNotContain("(Recommended)", CharterRenderer.Render(Question(",\"recommended\":\"Cassandra\"")));
    }

    [Fact]
    public void AQuestionWithoutARecommendation_RendersExactlyAsBefore()
    {
        Assert.Equal(CharterRenderer.Render(Question()), CharterRenderer.Render(Question()));
        Assert.DoesNotContain("(Recommended)", CharterRenderer.Render(Question()));
        Assert.Null(QuestionSpec.Parse(Body()).Recommended);
    }

    /// <summary>An older Charter ignores unknown body keys, so a plan carrying a recommendation still parses
    /// and renders there — the field is additive, not a format break.</summary>
    [Fact]
    public void TheFieldIsAdditive_AQuestionCarryingItIsStillValid()
    {
        var (ok, error) = QuestionSpec.Validate(Body(",\"recommended\":\"Postgres\""));

        Assert.True(ok, error);
    }

    /// <summary>
    /// The lean travels downstream. On a resolved question it is the sharper signal: an answer differing from
    /// the recommendation is a human deliberately overriding the agent, and work built from the plan must not
    /// drift back to the option they rejected.
    /// </summary>
    [Fact]
    public void HandoffCarriesTheRecommendation_SoTheBreakdownCanSeeTheOverride()
    {
        var markdown = Question(",\"recommended\":\"Postgres\",\"answer\":[\"DynamoDB\"]");

        var handoff = HandoffMarkdown.Emit(markdown);

        Assert.Contains("recommended: `Postgres`", handoff);
        Assert.Contains("DynamoDB", handoff);
    }

    /// <summary>
    /// Applying an answer must not drop the recommendation. The resolution kernel splices a single JSON key
    /// rather than re-serializing from the parsed spec, which is what makes every other body key survive —
    /// asserted here so a future rewrite of that kernel cannot quietly lose this one.
    /// </summary>
    [Fact]
    public void ResolvingAnAnswer_PreservesTheRecommendation()
    {
        var markdown = Question(",\"recommended\":\"Postgres\"");

        var applied = QuestionResolution.Apply(
            markdown,
            new System.Collections.Generic.Dictionary<string, IReadOnlyList<string>>
            {
                ["db"] = new[] { "DynamoDB" },
            });

        Assert.Contains("recommended", applied);
        var spec = BlockDocument.Parse(applied).Blocks
            .Where(b => b.Kind == BlockKind.Question)
            .Select(b => QuestionSpec.Parse(BodyOf(b.RawContent)))
            .Single();
        Assert.Equal("Postgres", spec.Recommended);
        Assert.Equal(new[] { "DynamoDB" }, spec.Answer);
    }

    private static string Body(string extra = "") =>
        "{\"id\":\"db\",\"title\":\"Which datastore?\",\"mode\":\"single\"," +
        "\"options\":[\"Postgres\",\"DynamoDB\"],\"target\":\"human\"" + extra + "}";

    /// <summary>The JSON body of a rendered <c>:::question</c> block's raw content.</summary>
    private static string BodyOf(string rawContent)
    {
        var start = rawContent.IndexOf('{');
        var end = rawContent.LastIndexOf('}');
        return rawContent[start..(end + 1)];
    }
}
