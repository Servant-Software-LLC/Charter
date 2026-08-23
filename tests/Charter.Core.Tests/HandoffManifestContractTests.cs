using System.Text.Json;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// The drift guard for <c>&lt;out-stem&gt;.manifest.json</c> (Charter #187): it BINDS the manifest's emitted
/// shape — and the meanings most likely to move under it — to the one document a consumer reads,
/// <c>skills/charter/references/handoff.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// It follows the FIXED template, not the original. <see cref="HeadlessRecordContractTests"/> as first written
/// bound field NAMES only, and Charter #188 proved that is not enough: <c>answered</c> changed meaning — from
/// "the array has elements" to "the array records a decision" — while every assertion in it stayed green,
/// because they all check that a token appears somewhere. <b>A drift test that pins names is a
/// spell-checker.</b>
/// </para>
/// <para>
/// So four meanings are bound BEHAVIOURALLY here as well as documentally, each chosen because it is silently
/// changeable and each because changing it breaks a reproduction check with nobody at fault: what
/// <c>answerSource</c> can never mean, what <c>handoffSha256</c> is computed over, what <c>answersSha256</c>
/// covers, and the fact that <c>answered</c> here is NARROWER than the identically-named field in the headless
/// record.
/// </para>
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","HandoffManifestContract")].
/// </remarks>
[Trait("Category", "HandoffManifestContract")]
public class HandoffManifestContractTests
{
    /// <summary>
    /// The STABLE CORE a consumer may assert on across versions, declared here as the test-side source of
    /// truth. A field named here that stops being emitted fails the build: it is the half that cannot move
    /// without a <see cref="HandoffManifest.Schema"/> bump.
    /// </summary>
    private static readonly string[] StableTopLevelFields =
    {
        "schema", "charterVersion", "planSha256", "answersSha256", "handoffSha256", "malformedQuestions",
    };

    /// <summary>The stable core of the <c>gate</c> object.</summary>
    private static readonly string[] StableGateFields =
    {
        "flagPassed", "needsHuman", "exitCode", "unmatchedAnswerIds",
    };

    /// <summary>The stable core of a <c>questions[]</c> entry.</summary>
    private static readonly string[] StableQuestionFields = { "id", "answered", "answer", "answerSource" };

    /// <summary>
    /// The three fields whose ABSENCE is load-bearing. They are the headless record's, they describe the
    /// RENDERED artifact, and joining any of them into the flattened output joins against the wrong file.
    /// </summary>
    private static readonly string[] ForbiddenFields = { "artifact", "sourceMap", "anchorId" };

    // A plan exercising every emitted shape at once: an inline-answered question, an open human question (so
    // there is a blocker to serialize), and a question the answers file fills.
    private const string RichPlan =
        "---\ncharter-format-version: 1\n---\n\n"
        + "# Plan\n\n"
        + ":::question\n{\"id\":\"db\",\"title\":\"Which database?\",\"mode\":\"single\",\"target\":\"human\","
        + "\"options\":[\"Postgres\",\"MySQL\"],\"answer\":[\"Postgres\"],\"recommended\":\"Postgres\"}\n:::\n\n"
        + ":::question\n{\"id\":\"cache\",\"title\":\"Which cache?\",\"mode\":\"single\",\"target\":\"human\","
        + "\"options\":[\"Redis\",\"in-memory\"],\"recommended\":\"Redis\"}\n:::\n\n"
        + ":::question\n{\"id\":\"queue\",\"title\":\"Which queue?\",\"mode\":\"single\",\"target\":\"human\","
        + "\"options\":[\"SQS\",\"RabbitMQ\"],\"recommended\":\"SQS\"}\n:::\n";

    private const string RichAnswersJson = "{\"queue\": [\"SQS\"], \"gone\": [\"x\"]}";

    // ---- the field set is documented -------------------------------------------------------------------------

    [Fact]
    public void EveryEmittedField_IsDocumentedInHandoffMd()
    {
        var doc = ReadDoc();
        var root = Rich().RootElement;

        foreach (var property in root.EnumerateObject())
        {
            AssertDocumented(doc, property.Name, "a top-level manifest field");
        }

        foreach (var property in root.GetProperty("gate").EnumerateObject())
        {
            AssertDocumented(doc, property.Name, "a gate field");
        }

        foreach (var property in root.GetProperty("questions")[0].EnumerateObject())
        {
            AssertDocumented(doc, property.Name, "a questions[] field");
        }

        foreach (var property in root.GetProperty("gate").GetProperty("blockers")[0].EnumerateObject())
        {
            AssertDocumented(doc, property.Name, "a gate.blockers[] field");
        }
    }

    [Fact]
    public void TheDocumentedSchemaNumber_IsTheCodesOwn()
    {
        // The example a consumer copies must be the CURRENT shape. This is the exact assertion the headless
        // record's #142 regression would have failed.
        Assert.Contains($"\"schema\": {HandoffManifest.Schema}", ReadDoc(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStableCore_IsEmitted_AndIsDeclaredStableInTheDoc()
    {
        var doc = ReadDoc();
        var root = Rich().RootElement;

        foreach (var field in StableTopLevelFields)
        {
            Assert.True(
                root.TryGetProperty(field, out _),
                $"the manifest no longer emits '{field}', which is declared part of its STABLE CORE. Removing "
                    + "one is a HandoffManifest.Schema bump plus an edit to handoff.md and to this test.");
        }

        foreach (var field in StableGateFields)
        {
            Assert.True(
                root.GetProperty("gate").TryGetProperty(field, out _),
                $"the manifest no longer emits gate.{field}, which is declared part of its STABLE CORE.");
        }

        foreach (var field in StableQuestionFields)
        {
            Assert.True(
                root.GetProperty("questions")[0].TryGetProperty(field, out _),
                $"the manifest no longer emits questions[].{field}, which is declared part of its STABLE CORE.");
        }

        Assert.Contains("Stable core", doc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDoc_DeclaresTheNonContractHalf_Explicitly()
    {
        // A consumer with no way to tell a load-bearing field from a presentational one will assert on all of
        // them. The names are the trap here: they are the first fields a reader reaches for and the ones that
        // mean least, because effectively every Guardrails handoff is called plan.md.
        var doc = ReadDoc();

        Assert.Contains("decoration", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`questions[].title`", doc, StringComparison.Ordinal);
        Assert.Contains("JSON key order", doc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QuestionsAreDocumentOrdered_AndTheDocSaysSo()
    {
        var ids = Rich().RootElement.GetProperty("questions").EnumerateArray()
            .Select(question => question.GetProperty("id").GetString())
            .ToList();

        Assert.Equal(new[] { "db", "cache", "queue" }, ids);
        Assert.Contains("document order", ReadDoc(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- the meanings, bound behaviourally ------------------------------------------------------------------

    [Fact]
    public void AnswerSource_CanNeverMeanTheFileOverrodeThePlan_AndTheREFUSALIsWhatBindsIt()
    {
        // Bound as the REFUSAL, not as the token. Asserting only that `inline` and `answers-file` are the two
        // spellings would stay green if the merge started letting a file replace a recorded decision -- the
        // manifest would just say `answers-file` about an override, which is the exact audit-not-safety trade
        // Charter #186 rejected. So: the rules must REJECT the entry, AND the resolved answer must be the
        // plan's, AND the manifest must say `inline`. All three, in one test.
        var supplied = HandoffAnswers.Read("{\"db\": [\"MySQL\"]}");

        Assert.NotEmpty(AnswerRules.CheckAll(RichPlan, supplied.Values));

        var db = Question(Manifest(RichPlan, supplied), "db");

        Assert.Equal(HandoffGate.InlineAnswerSource, db.GetProperty("answerSource").GetString());
        Assert.Equal(
            new[] { "Postgres" },
            db.GetProperty("answer").EnumerateArray().Select(value => value.GetString()).ToArray());

        // And the flatten agrees, because both read one merge.
        Assert.Contains(
            "Answered: Postgres",
            HandoffMarkdown.Emit(RichPlan, supplied.Values, supplied.Sha256),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnswerSource_SaysAnswersFile_WhenTheFileFILLEDAnUnansweredQuestion()
    {
        // The legal case, and the one #187's own repro inverted: since #186 an inline+file collision on one id
        // is REFUSED, so the only way `answers-file` is ever emitted is a question the plan left open.
        var manifest = Manifest(RichPlan, HandoffAnswers.Read(RichAnswersJson));

        Assert.Equal(
            HandoffGate.AnswersFileAnswerSource,
            Question(manifest, "queue").GetProperty("answerSource").GetString());
        Assert.Equal(HandoffGate.InlineAnswerSource, Question(manifest, "db").GetProperty("answerSource").GetString());
    }

    [Fact]
    public void AnswerSource_IsNullOnAnUnansweredQuestion_NotAThirdToken()
    {
        Assert.Equal(
            JsonValueKind.Null,
            Question(Manifest(RichPlan), "cache").GetProperty("answerSource").ValueKind);
    }

    [Fact]
    public void TheDoc_StatesWhatAnswerSourceCannotSay()
    {
        // The withdrawn reading -- that the field distinguishes a human's decision from the automation's -- is
        // the one a reader will re-invent, because it is what the field LOOKS like it says.
        var doc = ReadDoc();

        Assert.Contains("`inline`", doc, StringComparison.Ordinal);
        Assert.Contains("`answers-file`", doc, StringComparison.Ordinal);
        Assert.Contains("never reads the review log", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("where a decision lived", doc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HandoffSha256_IsOverTheBytesWRITTEN_StampsINCLUDED()
    {
        // Silently changeable, and it breaks every reproduction check: hashing the text BEFORE the provenance
        // stamps are appended produces a value no consumer can reproduce from the file on disk, while every
        // assertion about the field's presence stays green.
        var handoff = HandoffMarkdown.Emit(RichPlan);
        var hash = Manifest(RichPlan).RootElement.GetProperty("handoffSha256").GetString();

        Assert.Contains(HandoffMarkdown.StampPrefix, handoff, StringComparison.Ordinal);
        Assert.Contains(HandoffMarkdown.AnswersStampPrefix, handoff, StringComparison.Ordinal);
        Assert.Equal(PlanHash.Sha256Hex(handoff), hash);

        // The half that makes it a MEANING and not a name: it is NOT the hash of the stamp-free text.
        Assert.NotEqual(PlanHash.Sha256Hex(HandoffOutput.WithoutStamp(handoff)), hash);

        Assert.Contains("stamps included", ReadDoc(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnswersSha256_CoversTheFilesOWNTEXT_NotACanonicalizedDictionary()
    {
        // Two files that parse to the SAME answers hash differently, deliberately -- the value identifies a
        // revision of a file, which is what "reproducing this decision needs answersSha256" means. Canonicalizing
        // would make it identify a resolution instead, and two runs from different files would then be
        // indistinguishable in the one field that exists to tell them apart.
        var compact = HandoffAnswers.Read("{\"queue\":[\"SQS\"]}");
        var pretty = HandoffAnswers.Read("{\n  \"queue\": [\n    \"SQS\"\n  ]\n}\n");

        Assert.Equal(compact.Values["queue"], pretty.Values["queue"]);
        Assert.NotEqual(compact.Sha256, pretty.Sha256);

        Assert.NotEqual(
            Manifest(RichPlan, compact).RootElement.GetProperty("answersSha256").GetString(),
            Manifest(RichPlan, pretty).RootElement.GetProperty("answersSha256").GetString());

        // ...while the RESOLUTION they describe is identical, so nothing else in the manifest moved.
        Assert.Equal(
            Question(Manifest(RichPlan, compact), "queue").GetProperty("answer").ToString(),
            Question(Manifest(RichPlan, pretty), "queue").GetProperty("answer").ToString());

        Assert.Contains("own text", ReadDoc(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Answered_HereIsNarrowerThanInTheHeadlessRecord_AndTheDocSaysWhichIsWhich()
    {
        // Same word, two artifacts, two scopes -- which is precisely the shape of defect #188 was, so it is
        // ASSERTED rather than assumed. The record cannot see an --answers file by contract; the manifest is
        // about the merge.
        var answers = HandoffAnswers.Read(RichAnswersJson);

        Assert.False(
            HeadlessRecord.Build(RichPlan, "p.charter.md", "p.charter.html", "0.0.0-test")
                .Questions.Single(question => question.Id == "queue").Answered,
            "the headless record's `answered` must stay a pure function of the plan text.");

        Assert.True(
            Question(Manifest(RichPlan, answers), "queue").GetProperty("answered").GetBoolean(),
            "the manifest's `answered` must see the merged answer -- that is the whole point of #187.");

        // And a blank value is still not a decision, in either artifact (#188).
        var blank =
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
            + "{\"id\":\"db\",\"title\":\"Which database?\",\"mode\":\"single\",\"target\":\"human\","
            + "\"options\":[\"Postgres\",\"MySQL\"],\"answer\":[\"\"]}\n:::\n";

        Assert.False(Question(Manifest(blank), "db").GetProperty("answered").GetBoolean());

        Assert.Contains("NOT `answered` in the headless record", ReadDoc(), StringComparison.Ordinal);
    }

    // ---- absence semantics ----------------------------------------------------------------------------------

    [Fact]
    public void NoAnswersFile_IsNullPlusNull_AndAnEmptyOneIsNot()
    {
        // "Empty" and "not applicable" look identical on the wire unless something says otherwise. An EMPTY
        // answers file is a FILE: it was read, it has text, and that text has a hash.
        var none = Manifest(RichPlan).RootElement;
        Assert.Equal(JsonValueKind.Null, none.GetProperty("answers").ValueKind);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("answersSha256").ValueKind);

        var empty = Manifest(RichPlan, HandoffAnswers.Read("{}")).RootElement;
        Assert.Equal("a.json", empty.GetProperty("answers").GetString());
        Assert.Matches("^[0-9a-f]{64}$", empty.GetProperty("answersSha256").GetString());
    }

    [Fact]
    public void AMalformedQuestion_IsAbsentFromQuestions_AndVisibleInONEField()
    {
        // Without malformedQuestions, this plan's questions[] looks complete and entirely answered while the
        // flatten has DELETED the broken question's id, title and target from the document being vouched for.
        const string plan =
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
            + "{\"id\":\"db\",\"title\":\"t\",\"mode\":\"single\",\"target\":\"human\","
            + "\"options\":[\"A\"],\"answer\":[\"A\"]}\n:::\n\n"
            + ":::question\n{\"id\": \"broken\", \"title\": \"t\",}\n:::\n";

        var root = Manifest(plan).RootElement;

        Assert.Equal(1, root.GetProperty("malformedQuestions").GetInt32());
        Assert.Equal(1, root.GetProperty("questions").GetArrayLength());
        Assert.True(root.GetProperty("questions")[0].GetProperty("answered").GetBoolean());

        // It is reachable only as a blocker with no id, because Charter has no id to give it.
        var blocker = root.GetProperty("gate").GetProperty("blockers").EnumerateArray()
            .Single(entry => entry.GetProperty("kind").GetString() == HandoffGate.MalformedQuestion);
        Assert.Equal(JsonValueKind.Null, blocker.GetProperty("id").ValueKind);
    }

    [Fact]
    public void QuestionsIsNotAMap_DuplicateIdsKeepBothEntries_ToldApartBySourceLine()
    {
        const string plan =
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
            + "{\"id\":\"db\",\"title\":\"first\",\"mode\":\"single\",\"target\":\"human\",\"options\":[\"A\"]}\n:::\n\n"
            + ":::question\n"
            + "{\"id\":\"db\",\"title\":\"second\",\"mode\":\"single\",\"target\":\"human\",\"options\":[\"A\"]}\n:::\n";

        var entries = Manifest(plan).RootElement.GetProperty("questions").EnumerateArray().ToList();

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal("db", entry.GetProperty("id").GetString()));
        Assert.NotEqual(
            entries[0].GetProperty("sourceLine").GetInt32(),
            entries[1].GetProperty("sourceLine").GetInt32());
    }

    [Fact]
    public void NoBlockers_GenuinelyMeansNothingBlocks()
    {
        // Unlike the forensic record's old `notes: []`, nothing here is computed conditionally.
        const string clean = "---\ncharter-format-version: 1\n---\n\n# Plan\n\nJust prose.\n";
        var gate = Manifest(clean).RootElement.GetProperty("gate");

        Assert.Equal(0, gate.GetProperty("blockers").GetArrayLength());
        Assert.False(gate.GetProperty("needsHuman").GetBoolean());
    }

    [Fact]
    public void UnmatchedAnswerIds_AreRecorded_AndAreNeverAVeto()
    {
        var root = Manifest(RichPlan, HandoffAnswers.Read(RichAnswersJson), flagPassed: true).RootElement;

        Assert.Equal(
            new[] { "gone" },
            root.GetProperty("gate").GetProperty("unmatchedAnswerIds").EnumerateArray()
                .Select(value => value.GetString()).ToArray());

        // `cache` is still open, so this run DOES need a human -- but not because of the unmatched id.
        Assert.True(root.GetProperty("gate").GetProperty("needsHuman").GetBoolean());
        Assert.DoesNotContain(
            "gone",
            root.GetProperty("gate").GetProperty("blockers").ToString(),
            StringComparison.Ordinal);
    }

    // ---- the gate's verdict ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, true, 2)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 0)]
    public void ExitCode_IsDerivedFromFlagPassedAndNeedsHuman(bool flagPassed, bool needsHuman, int expected)
    {
        // Pinned as a DERIVATION rather than a recorded number, so the file and $? cannot disagree -- the same
        // discipline that keeps the forensic record's needsHuman equal to `charter headless`'s exit code.
        const string clean = "---\ncharter-format-version: 1\n---\n\n# Plan\n\nJust prose.\n";
        var plan = needsHuman ? RichPlan : clean;
        var gate = Manifest(plan, flagPassed: flagPassed).RootElement.GetProperty("gate");

        Assert.Equal(needsHuman, gate.GetProperty("needsHuman").GetBoolean());
        Assert.Equal(flagPassed, gate.GetProperty("flagPassed").GetBoolean());
        Assert.Equal(expected, gate.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void TheGateIsEvaluatedEvenWithoutTheFlag_AndTheManifestSaysTheVerdictWentToTheFileNotTheExitCode()
    {
        // The reading a consumer must not get wrong: exitCode 0 with needsHuman true is not "nothing blocks",
        // it is "the caller did not ask for the gate to change $?".
        var gate = Manifest(RichPlan, flagPassed: false).RootElement.GetProperty("gate");

        Assert.False(gate.GetProperty("flagPassed").GetBoolean());
        Assert.True(gate.GetProperty("needsHuman").GetBoolean());
        Assert.Equal(0, gate.GetProperty("exitCode").GetInt32());
        Assert.NotEmpty(gate.GetProperty("blockers").EnumerateArray());
    }

    [Fact]
    public void ABlockersDetail_IsNeverSerialized()
    {
        // HandoffGate documents `detail` as "Not a contract; do not parse it". Putting it in a versioned schema
        // makes it a de-facto contract the first time a harness greps it, at which point rewording a stderr
        // sentence becomes a breaking change.
        var blockers = Manifest(RichPlan).RootElement.GetProperty("gate").GetProperty("blockers");

        Assert.NotEmpty(blockers.EnumerateArray());
        foreach (var blocker in blockers.EnumerateArray())
        {
            Assert.False(
                blocker.TryGetProperty("detail", out _),
                "gate.blockers[].detail must never be serialized -- the gate declares it non-contract.");
        }
    }

    // ---- the absences that are load-bearing -----------------------------------------------------------------

    [Fact]
    public void NoArtifact_NoSourceMap_NoAnchorId_AnywhereInTheFile()
    {
        // The negative test the design asks for by name. Those three fields are exactly why emitting a
        // HeadlessRecord from `handoff` was rejected: `artifact` has no value on this path, and `sourceMap` /
        // `anchorId` map to the .charter.md, so a consumer joining either into plan.md joins against the WRONG
        // FILE. A future "helpful" addition reintroduces that, and it fails here.
        var names = new List<string>();
        Collect(Rich().RootElement, names);

        foreach (var forbidden in ForbiddenFields)
        {
            Assert.DoesNotContain(forbidden, names);
        }

        var doc = ReadDoc();
        Assert.Contains("No `artifact`", doc, StringComparison.Ordinal);
        Assert.Contains("no map into the handoff output", doc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryLineNumberInTheManifest_IsALineInThePlan()
    {
        // The governing rule, asserted rather than promised. Both `questions[].sourceLine` and
        // `gate.blockers[].sourceLine` must land on a line of the .charter.md -- and specifically on the
        // question's own ::: fence, which is what makes "a line in the plan" mean something stronger than "an
        // integer in range".
        var planLines = RichPlan.Split('\n');
        var root = Manifest(RichPlan).RootElement;

        foreach (var question in root.GetProperty("questions").EnumerateArray())
        {
            var line = question.GetProperty("sourceLine").GetInt32();
            Assert.InRange(line, 1, planLines.Length);
            Assert.StartsWith(":::question", planLines[line - 1], StringComparison.Ordinal);
        }

        foreach (var blocker in root.GetProperty("gate").GetProperty("blockers").EnumerateArray())
        {
            if (blocker.GetProperty("sourceLine").ValueKind == JsonValueKind.Number)
            {
                Assert.InRange(blocker.GetProperty("sourceLine").GetInt32(), 1, planLines.Length);
            }
        }
    }

    // ---- determinism ----------------------------------------------------------------------------------------

    [Fact]
    public void TwoRunsOverTheSameInputs_AreByteIdentical()
    {
        // No clock, no random, no local path -- so reproducibility is itself assertable: a harness diffs two
        // runs rather than trusting a sentence about determinism.
        var answers = HandoffAnswers.Read(RichAnswersJson);

        Assert.Equal(Json(RichPlan, answers, flagPassed: true), Json(RichPlan, answers, flagPassed: true));
    }

    [Fact]
    public void TheManifest_CarriesNoLocalPathAndNoClock()
    {
        var json = Json(RichPlan, HandoffAnswers.Read(RichAnswersJson), flagPassed: true);

        Assert.DoesNotContain("/", json.Replace("\\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("\\\\", json, StringComparison.Ordinal);
        Assert.DoesNotContain("timestamp", json, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public void APathWhereANameIsRequired_IsRefused()
    {
        // The same no-local-path guarantee the forensic record and the exported artifact keep, from the one
        // kernel both now use.
        var gate = HandoffGate.Evaluate(RichPlan, answers: null);

        Assert.Throws<ArgumentException>(() => HandoffManifest.Build(
            RichPlan,
            HandoffMarkdown.Emit(RichPlan),
            null,
            gate,
            false,
            new HandoffManifestFiles("dir/p.charter.md", null, "plan.md"),
            "0.0.0-test"));

        Assert.Throws<ArgumentException>(() => HandoffManifest.Build(
            RichPlan,
            HandoffMarkdown.Emit(RichPlan),
            null,
            gate,
            false,
            new HandoffManifestFiles("p.charter.md", null, @"dir\plan.md"),
            "0.0.0-test"));
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    /// <summary>The everything-at-once manifest: an answers file, a filled question, and a live blocker.</summary>
    private static JsonDocument Rich()
        => Manifest(RichPlan, HandoffAnswers.Read(RichAnswersJson), flagPassed: true);

    private static JsonDocument Manifest(
        string markdown, HandoffAnswers? answers = null, bool flagPassed = false)
        => JsonDocument.Parse(Json(markdown, answers, flagPassed));

    /// <summary>
    /// One run, exactly as the CLI performs it: emit the flatten, evaluate the gate ONCE, hand both to the
    /// manifest. Assembling it any other way here would let this file pass while the verb produced something
    /// else.
    /// </summary>
    private static string Json(string markdown, HandoffAnswers? answers, bool flagPassed)
        => HandoffManifest.Build(
            markdown,
            HandoffMarkdown.Emit(markdown, answers?.Values, answers?.Sha256),
            answers,
            HandoffGate.Evaluate(markdown, answers?.Values),
            flagPassed,
            new HandoffManifestFiles("p.charter.md", answers is null ? null : "a.json", "plan.md"),
            "0.0.0-test").ToJson();

    private static JsonElement Question(JsonDocument manifest, string id)
        => manifest.RootElement.GetProperty("questions").EnumerateArray()
            .Single(question => question.GetProperty("id").GetString() == id);

    /// <summary>Every property name anywhere in <paramref name="element"/>, at any depth.</summary>
    private static void Collect(JsonElement element, List<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    names.Add(property.Name);
                    Collect(property.Value, names);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, names);
                }

                break;

            default:
                break;
        }
    }

    private static void AssertDocumented(string doc, string token, string what)
        => Assert.True(
            doc.Contains("`" + token + "`", StringComparison.Ordinal),
            $"handoff.md does not document '{token}' ({what}). The manifest's shape is a CONTRACT a post-mortem "
                + "harness asserts against; an undocumented field is one it can only discover by reading "
                + "Charter's source, which is exactly how `recommended` reached the wild while the headless "
                + "record's published example still showed the shape before it.");

    /// <summary>Read handoff.md, located by walking up to the repo root (Charter.sln).</summary>
    private static string ReadDoc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Charter.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "could not locate the repo root (Charter.sln) from the test base directory.");

        var path = Path.Combine(dir!.FullName, "skills", "charter", "references", "handoff.md");
        Assert.True(File.Exists(path), $"handoff.md not found at {path}.");
        return File.ReadAllText(path);
    }
}
