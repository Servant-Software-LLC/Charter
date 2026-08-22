using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// The FORENSIC half of Charter #7. <c>charter export</c> already renders a self-contained, SDK-free artifact
/// and exits without a server — but the anchor→line source map and everything the interactive review would
/// have put in front of a human (the open decisions, the format diagnostics) lived ONLY in the review server's
/// memory. <see cref="HeadlessRecord"/> is that material, made durable and deterministic.
/// </summary>
/// <remarks>
/// Two properties are load-bearing and each has its own test here:
/// <list type="bullet">
///   <item><description><b>Deterministic in the plan.</b> No clock, no local path — so a harness can diff two
///     runs, and so the record is byte-reproducible from the plan plus the tool version.</description></item>
///   <item><description><b><c>needsHuman</c> is DERIVED, never independently computed.</b> The CLI's exit code
///     reads this same property, so the file on disk and the process exit code can never disagree — the same
///     discipline that keeps <c>anchorStatus</c> and <c>sourceLine</c> in agreement on the poll wire.</description></item>
/// </list>
/// </remarks>
public class HeadlessRecordTests
{
    private const string PlanWithOpenHumanQuestion =
        "---\ncharter-format-version: 1\n---\n\n"
        + "# Storage plan\n\n"
        + "Some prose about the store.\n\n"
        + ":::question\n"
        + "{\"id\": \"store\", \"title\": \"Which store?\", \"mode\": \"single\", "
        + "\"target\": \"human\", \"options\": [\"Postgres\", \"SQLite\"]}\n"
        + ":::\n";

    private const string PlanWithAnsweredQuestion =
        "---\ncharter-format-version: 1\n---\n\n"
        + "# Storage plan\n\n"
        + ":::question\n"
        + "{\"id\": \"store\", \"title\": \"Which store?\", \"mode\": \"single\", "
        + "\"target\": \"human\", \"options\": [\"Postgres\", \"SQLite\"], \"answer\": [\"Postgres\"]}\n"
        + ":::\n";

    private const string PlanWithAgentQuestion =
        "---\ncharter-format-version: 1\n---\n\n"
        + "# Storage plan\n\n"
        + ":::question\n"
        + "{\"id\": \"store\", \"title\": \"Which store?\", \"mode\": \"single\", "
        + "\"target\": \"agent\", \"options\": [\"Postgres\", \"SQLite\"]}\n"
        + ":::\n";

    private static HeadlessRecord Build(string markdown)
        => HeadlessRecord.Build(markdown, "plan.charter.md", "plan.charter.html", "9.9.9");

    private static JsonElement Json(HeadlessRecord record)
        => JsonDocument.Parse(record.ToJson()).RootElement;

    // ---------------------------------------------------------------------------------------------------
    // The source map reaches disk — the gap `export` genuinely left open.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The persisted <c>sourceMap</c> is the SAME map the live server resolves annotations through — every
    /// anchor, every line. Nothing wrote this to disk before: an artifact rendered headlessly could not be
    /// traced back to the markdown it came from once the (never-started) server was gone.
    /// </summary>
    [Fact]
    public void SourceMap_OnDisk_IsAnchorForAnchorIdenticalToSourceMapBuild()
    {
        var expected = SourceMap.Build(PlanWithOpenHumanQuestion);

        var sourceMap = Json(Build(PlanWithOpenHumanQuestion)).GetProperty("sourceMap");

        var written = sourceMap.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());

        Assert.Equal(
            expected.Anchors.OrderBy(a => a, StringComparer.Ordinal),
            written.Keys.OrderBy(a => a, StringComparer.Ordinal));

        foreach (var anchor in expected.Anchors)
        {
            Assert.Equal(expected.LineForAnchor(anchor), written[anchor]);
        }

        Assert.NotEmpty(written);
    }

    /// <summary>Anchors are emitted in ascending source-line order, so the file reads top-to-bottom like the plan.</summary>
    [Fact]
    public void SourceMap_OnDisk_IsOrderedByAscendingSourceLine()
    {
        var lines = Json(Build(PlanWithOpenHumanQuestion))
            .GetProperty("sourceMap")
            .EnumerateObject()
            .Select(p => p.Value.GetInt32())
            .ToList();

        Assert.Equal(lines.OrderBy(l => l), lines);
    }

    // ---------------------------------------------------------------------------------------------------
    // The decisions interactive review would have elicited.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every <c>:::question</c> is recorded whole — the form a human would have filled in, plus the anchor and
    /// source line that say WHERE in the plan the decision lives.
    /// </summary>
    [Fact]
    public void Questions_CarryTheFullSpecPlusItsAnchorAndSourceLine()
    {
        var question = Assert.Single(Json(Build(PlanWithOpenHumanQuestion)).GetProperty("questions").EnumerateArray());

        Assert.Equal("store", question.GetProperty("id").GetString());
        Assert.Equal("Which store?", question.GetProperty("title").GetString());
        Assert.Equal("single", question.GetProperty("mode").GetString());
        Assert.Equal("human", question.GetProperty("target").GetString());
        Assert.Equal(
            new[] { "Postgres", "SQLite" },
            question.GetProperty("options").EnumerateArray().Select(o => o.GetString()).ToArray());
        Assert.False(question.GetProperty("answered").GetBoolean());
        Assert.Empty(question.GetProperty("answer").EnumerateArray());

        // The anchor must be a REAL anchor of this plan — the same id the rendered HTML carries — so a reader
        // can go artifact element -> anchor -> sourceMap -> markdown line.
        var anchorId = question.GetProperty("anchorId").GetString()!;
        Assert.Contains(anchorId, SourceMap.Build(PlanWithOpenHumanQuestion).Anchors);
        Assert.Equal(SourceMap.Build(PlanWithOpenHumanQuestion).LineForAnchor(anchorId), question.GetProperty("sourceLine").GetInt32());
    }

    /// <summary>An already-answered question is recorded as decided and carries the value that settled it.</summary>
    [Fact]
    public void Questions_RecordAnAnsweredQuestionAsAnsweredWithItsValues()
    {
        var question = Assert.Single(Json(Build(PlanWithAnsweredQuestion)).GetProperty("questions").EnumerateArray());

        Assert.True(question.GetProperty("answered").GetBoolean());
        Assert.Equal(
            new[] { "Postgres" },
            question.GetProperty("answer").EnumerateArray().Select(o => o.GetString()).ToArray());
    }

    // ---------------------------------------------------------------------------------------------------
    // needsHuman — the escalation signal, and the three things that raise it.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>An open question addressed to a human is the whole point: nobody was there to answer it.</summary>
    [Fact]
    public void NeedsHuman_IsTrue_ForAnOpenQuestionTargetingAHuman()
    {
        var record = Build(PlanWithOpenHumanQuestion);

        Assert.True(record.NeedsHuman);
        Assert.True(Json(record).GetProperty("needsHuman").GetBoolean());
    }

    /// <summary>
    /// A question whose body Charter cannot parse has an UNKNOWN target, so it escalates. Assuming it was for
    /// the agent would let an unattended crew sail past a decision nobody can even read — the same "never
    /// proceed on a false empty" rule the poll exit codes exist for.
    /// </summary>
    [Fact]
    public void NeedsHuman_IsTrue_ForAQuestionWhoseBodyCannotBeParsed()
    {
        const string plan = "# Plan\n\n:::question\n{not json at all\n:::\n";

        var record = Build(plan);

        Assert.True(record.NeedsHuman);
        Assert.Contains(
            Json(record).GetProperty("notes").EnumerateArray(),
            note => note.GetProperty("kind").GetString() == "malformed-question");
    }

    /// <summary>
    /// Duplicate question ids escalate: an answer would resolve into BOTH blocks, and both
    /// <c>poll --apply</c> and <c>resolve</c> refuse the write — so the plan cannot be settled unattended.
    /// </summary>
    [Fact]
    public void NeedsHuman_IsTrue_ForDuplicateQuestionIds()
    {
        const string plan =
            "# Plan\n\n"
            + ":::question\n{\"id\": \"q\", \"title\": \"A?\", \"mode\": \"bool\", \"target\": \"agent\"}\n:::\n\n"
            + ":::question\n{\"id\": \"q\", \"title\": \"B?\", \"mode\": \"bool\", \"target\": \"agent\"}\n:::\n";

        var record = Build(plan);

        Assert.True(record.NeedsHuman);
        Assert.Contains(
            Json(record).GetProperty("notes").EnumerateArray(),
            note => note.GetProperty("kind").GetString() == "duplicate-question-id");
    }

    /// <summary>A settled decision is not outstanding — an answered human question does NOT escalate.</summary>
    [Fact]
    public void NeedsHuman_IsFalse_WhenEveryHumanQuestionIsAnswered()
    {
        Assert.False(Build(PlanWithAnsweredQuestion).NeedsHuman);
    }

    /// <summary>
    /// An open question addressed to the AGENT is the agent's own work, not an escalation — the flattened
    /// handoff already honours <c>target</c> for exactly this reason. It is still recorded.
    /// </summary>
    [Fact]
    public void NeedsHuman_IsFalse_ForAnOpenQuestionTargetingTheAgent()
    {
        var record = Build(PlanWithAgentQuestion);

        Assert.False(record.NeedsHuman);
        Assert.Single(Json(record).GetProperty("questions").EnumerateArray());
    }

    /// <summary>
    /// A missing format-version marker is a WARNING everywhere else in the CLI (it never changes an exit
    /// code), so it is recorded but must not escalate. Keeping the warning/escalation line where the rest of
    /// the tool already draws it is what stops <c>needsHuman</c> becoming a meaningless always-true flag.
    /// </summary>
    [Fact]
    public void NeedsHuman_IsFalse_ForAMissingVersionMarker_ButTheNoteIsStillRecorded()
    {
        const string plan = "# Plan\n\nJust prose, no marker.\n";

        var record = Build(plan);

        Assert.False(record.NeedsHuman);
        Assert.Contains(
            Json(record).GetProperty("notes").EnumerateArray(),
            note => note.GetProperty("kind").GetString() == "missing-version-marker");
    }

    /// <summary>An unrecognized <c>:::foo</c> is recorded as a note — visible in hindsight, but not blocking.</summary>
    [Fact]
    public void Notes_RecordAnUnknownDirectiveWithItsSourceLine()
    {
        const string plan = "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::sparkle\nbody\n:::\n";

        var note = Assert.Single(
            Json(Build(plan)).GetProperty("notes").EnumerateArray(),
            n => n.GetProperty("kind").GetString() == "unknown-directive");

        Assert.Equal(7, note.GetProperty("sourceLine").GetInt32());
    }

    // ---------------------------------------------------------------------------------------------------
    // Determinism and the no-local-path rule.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The record is a pure function of the plan text and the tool version — no clock, no random, no machine
    /// state. Two runs over the same plan are byte-identical, so a harness can diff them and a reviewer can
    /// reproduce one.
    /// </summary>
    [Fact]
    public void ToJson_IsByteIdenticalAcrossRepeatedBuildsOfTheSamePlan()
    {
        Assert.Equal(Build(PlanWithOpenHumanQuestion).ToJson(), Build(PlanWithOpenHumanQuestion).ToJson());
    }

    /// <summary>
    /// The plan is identified by NAME and CONTENT HASH, never by path — the record must be safe to hand on,
    /// exactly like the exported artifact, and the hash is what lets a human in hindsight prove WHICH revision
    /// of the plan the artifact beside it was rendered from.
    /// </summary>
    [Fact]
    public void Record_IdentifiesThePlanByBasenameAndContentHash_NeverByPath()
    {
        var record = HeadlessRecord.Build(
            PlanWithOpenHumanQuestion, "plan.charter.md", "plan.charter.html", "9.9.9");

        var json = Json(record);
        Assert.Equal("plan.charter.md", json.GetProperty("plan").GetString());
        Assert.Equal("plan.charter.html", json.GetProperty("artifact").GetString());
        Assert.Equal("9.9.9", json.GetProperty("charterVersion").GetString());

        var expectedHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(PlanWithOpenHumanQuestion))).ToLowerInvariant();
        Assert.Equal(expectedHash, json.GetProperty("planSha256").GetString());

        // Nothing that could be a local filesystem path may appear anywhere in the serialized record.
        var text = record.ToJson();
        Assert.DoesNotContain(":\\", text, StringComparison.Ordinal);
        Assert.DoesNotContain("file://", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Build</c> rejects a path where a NAME is required, rather than silently writing a local path into a
    /// record whose whole contract is that it carries none.
    /// </summary>
    [Theory]
    [InlineData("C:\\plans\\plan.charter.md", "plan.charter.html")]
    [InlineData("plan.charter.md", "/tmp/out/plan.charter.html")]
    public void Build_RefusesAPathWhereAFileNameIsRequired(string planName, string artifactName)
    {
        Assert.Throws<ArgumentException>(
            () => HeadlessRecord.Build(PlanWithOpenHumanQuestion, planName, artifactName, "9.9.9"));
    }

    /// <summary>A plan with no questions and no diagnostics still produces a complete, well-formed record.</summary>
    [Fact]
    public void Build_OnACleanPlanWithNoQuestions_ProducesAnEmptyQuestionListAndNoEscalation()
    {
        const string plan = "---\ncharter-format-version: 1\n---\n\n# Plan\n\nJust prose.\n";

        var record = Build(plan);
        var json = Json(record);

        Assert.False(record.NeedsHuman);
        Assert.Empty(json.GetProperty("questions").EnumerateArray());
        Assert.Empty(json.GetProperty("notes").EnumerateArray());
        // Bound to the code constant, never a re-typed literal: the schema number is a contract, and
        // HeadlessRecordContractTests is what holds the SHAPE to its documentation when it moves.
        Assert.Equal(HeadlessRecord.Schema, json.GetProperty("schema").GetInt32());
        Assert.NotEmpty(json.GetProperty("sourceMap").EnumerateObject());
    }
}
