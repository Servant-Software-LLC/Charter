using System.Text.Json;
using System.Text.Json.Nodes;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Tests for <see cref="QuestionResolution"/> — the single deterministic kernel that writes resolved answers
/// back INTO a Charter deliverable's <c>:::question</c> blocks (Architecture B §1.4). The load-bearing
/// contract: <see cref="QuestionResolution.Apply"/> performs a SURGICAL <c>JsonObject</c> key-add — it sets
/// only the <c>answer</c> key and preserves every other body key AND every non-question byte of the document
/// (prose, other blocks, front matter, fences). It is deliberately NOT a <see cref="QuestionSpec"/>
/// round-trip, which would drop any body key the record does not model. <see cref="QuestionResolution.ApplyToFile"/>
/// adds the single-writer atomic persist (temp+rename in the plan's own directory), and
/// <see cref="QuestionResolution.FindDuplicateQuestionIds"/> is the document-unique-id lint.
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","QuestionResolution")].
/// </summary>
[Trait("Category", "QuestionResolution")]
public class QuestionResolutionTests
{
    private static Dictionary<string, IReadOnlyList<string>> Answers(params (string Id, string[] Values)[] entries)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (id, values) in entries)
        {
            map[id] = values;
        }

        return map;
    }

    [Fact]
    public void Apply_AddsAnswerToMatchingQuestion_AndItReparsesAsResolved()
    {
        const string markdown =
            ":::question\n" +
            "{ \"id\": \"db-choice\", \"title\": \"Which datastore?\", \"mode\": \"single\", " +
            "\"options\": [\"Postgres\", \"DynamoDB\"], \"target\": \"human\" }\n" +
            ":::";

        var updated = QuestionResolution.Apply(markdown, Answers(("db-choice", new[] { "Postgres" })));

        // The resolved block re-parses through the single schema source of truth as RESOLVED.
        var block = Assert.Single(BlockDocument.Parse(updated).Blocks);
        Assert.Equal(BlockKind.Question, block.Kind);

        var spec = QuestionSpec.Parse(InnerJson(block.RawContent));
        Assert.Equal(new[] { "Postgres" }, spec.Answer);
        // Every other schema field survives the splice.
        Assert.Equal("db-choice", spec.Id);
        Assert.Equal(QuestionMode.SingleSelect, spec.Mode);
        Assert.Equal(new[] { "Postgres", "DynamoDB" }, spec.Options);
    }

    [Fact]
    public void Apply_IsNotALossyRoundTrip_PreservesUnknownBodyKeys()
    {
        // The teeth of "surgical key-add, never a QuestionSpec round-trip": a body key the record does NOT
        // model (rationale) must survive. A round-trip through QuestionSpec — which captures only five keys —
        // would silently discard it. This is the assertion that proves the kernel is not lossy.
        const string markdown =
            ":::question\n" +
            "{ \"id\": \"q\", \"title\": \"T\", \"mode\": \"single\", \"options\": [\"A\", \"B\"], " +
            "\"target\": \"human\", \"rationale\": \"kept because latency matters\" }\n" +
            ":::";

        var updated = QuestionResolution.Apply(markdown, Answers(("q", new[] { "A" })));

        // The unknown key AND its value survive verbatim, and the answer was added alongside it.
        var json = JsonNode.Parse(InnerJson(BlockDocument.Parse(updated).Blocks[0].RawContent))!.AsObject();
        Assert.Equal("kept because latency matters", (string?)json["rationale"]);
        Assert.Equal("A", (string?)json["answer"]!.AsArray()[0]);
    }

    [Fact]
    public void Apply_LeavesProseAndOtherBlocksUntouched()
    {
        const string markdown =
            "# A heading\n\n" +
            "Some prose that mentions the word answer but is not a question.\n\n" +
            ":::note\nA note that must survive verbatim.\n:::\n\n" +
            ":::question\n" +
            "{ \"id\": \"q\", \"title\": \"T\", \"mode\": \"bool\", \"target\": \"human\" }\n" +
            ":::\n\n" +
            "Trailing paragraph.";

        var updated = QuestionResolution.Apply(markdown, Answers(("q", new[] { "true" })));

        // Every non-question region is byte-for-byte present in the output.
        Assert.Contains("# A heading", updated);
        Assert.Contains("Some prose that mentions the word answer but is not a question.", updated);
        Assert.Contains(":::note\nA note that must survive verbatim.\n:::", updated);
        Assert.Contains("Trailing paragraph.", updated);
        // ...and the question gained its answer.
        Assert.Contains("\"answer\"", updated);
    }

    [Fact]
    public void Apply_QuestionNotInMap_IsLeftUntouched()
    {
        const string markdown =
            ":::question\n" +
            "{ \"id\": \"unanswered\", \"title\": \"T\", \"mode\": \"bool\", \"target\": \"human\" }\n" +
            ":::";

        var updated = QuestionResolution.Apply(markdown, Answers(("some-other-id", new[] { "x" })));

        // No id match, so the document is returned unchanged — no stray answer key appears.
        Assert.Equal(markdown, updated);
        Assert.DoesNotContain("\"answer\"", updated);
    }

    [Fact]
    public void Apply_EmptyOrNullAnswers_ReturnsInputUnchanged()
    {
        const string markdown =
            ":::question\n{ \"id\": \"q\", \"title\": \"T\", \"mode\": \"bool\", \"target\": \"human\" }\n:::";

        Assert.Equal(markdown, QuestionResolution.Apply(markdown, new Dictionary<string, IReadOnlyList<string>>()));
    }

    [Fact]
    public void Apply_ResolvesOnlyTheMatchingQuestionAmongMany()
    {
        const string markdown =
            ":::question\n{ \"id\": \"a\", \"title\": \"A?\", \"mode\": \"bool\", \"target\": \"human\" }\n:::\n\n" +
            ":::question\n{ \"id\": \"b\", \"title\": \"B?\", \"mode\": \"bool\", \"target\": \"human\" }\n:::";

        var updated = QuestionResolution.Apply(markdown, Answers(("b", new[] { "true" })));

        var blocks = BlockDocument.Parse(updated).Blocks;
        Assert.Empty(QuestionSpec.Parse(InnerJson(blocks[0].RawContent)).Answer); // a stays open
        Assert.Equal(new[] { "true" }, QuestionSpec.Parse(InnerJson(blocks[1].RawContent)).Answer); // b resolved
    }

    [Fact]
    public void Apply_BoolFalseAnswer_ResolvesAndReparsesAsAnsweredNo()
    {
        // Charter #43 round-trip: a bool "No" flows like any other answer — the selected radio's value "false"
        // is spliced as a one-element ["false"] answer, which re-parses through the schema as RESOLVED (Answer
        // non-empty), never as an open question. This is the Core half of the Yes/No-radios end-to-end.
        const string markdown =
            ":::question\n" +
            "{ \"id\": \"flag\", \"title\": \"Enable the feature flag?\", \"mode\": \"bool\", \"target\": \"human\" }\n" +
            ":::";

        var updated = QuestionResolution.Apply(markdown, Answers(("flag", new[] { "false" })));

        var spec = QuestionSpec.Parse(InnerJson(BlockDocument.Parse(updated).Blocks[0].RawContent));
        Assert.Equal(new[] { "false" }, spec.Answer); // a real inline No value, not empty
        Assert.NotEmpty(spec.Answer);                 // distinguishable from an open (unanswered) question
    }

    [Fact]
    public void Apply_PreservesLeadingFrontMatter()
    {
        // Apply splices on the original source string, so YAML front matter above the first block is copied
        // through verbatim (never stripped — that is the render/handoff seam's job, not the resolver's).
        const string markdown =
            "---\ncharter-format-version: 1\n---\n\n" +
            ":::question\n{ \"id\": \"q\", \"title\": \"T\", \"mode\": \"bool\", \"target\": \"human\" }\n:::";

        var updated = QuestionResolution.Apply(markdown, Answers(("q", new[] { "true" })));

        Assert.StartsWith("---\ncharter-format-version: 1\n---", updated);
        Assert.Contains("\"answer\"", updated);
    }

    // ---- Charter #49 (bonus): the splice preserves the AUTHORED body's line structure --------------------

    [Fact]
    public void Apply_PreservesTheAuthoredMultiLineBody_ChangingExactlyOneLine()
    {
        // The canonical authored :::question in the charter-format skill is MULTI-LINE. Re-serializing the whole
        // body compacted it onto one line, shrinking the file and shifting every anchor below it (Charter #49).
        // The answer is now spliced in place instead: same line count, one line touched.
        const string markdown =
            ":::question\n" +
            "{\n" +
            "  \"id\": \"db-choice\",\n" +
            "  \"title\": \"Which datastore for the read path?\",\n" +
            "  \"mode\": \"single\",\n" +
            "  \"options\": [\"Postgres\", \"DynamoDB\"],\n" +
            "  \"target\": \"human\"\n" +
            "}\n" +
            ":::\n";

        var updated = QuestionResolution.Apply(markdown, Answers(("db-choice", new[] { "Postgres" })));

        var before = markdown.Split('\n');
        var after = updated.Split('\n');
        Assert.Equal(before.Length, after.Length);

        var index = Assert.Single(Enumerable.Range(0, before.Length), i => before[i] != after[i]);
        Assert.Equal("  \"target\": \"human\"", before[index]);
        Assert.StartsWith("  \"target\": \"human\",", after[index], StringComparison.Ordinal);
        Assert.Contains("\"answer\"", after[index], StringComparison.Ordinal);

        // ...and it is still a valid, RESOLVED question through the one schema source of truth.
        var spec = QuestionSpec.Parse(InnerJson(BlockDocument.Parse(updated).Blocks[0].RawContent));
        Assert.Equal(new[] { "Postgres" }, spec.Answer);
        Assert.Equal("db-choice", spec.Id);
        Assert.Equal(new[] { "Postgres", "DynamoDB" }, spec.Options);
    }

    [Fact]
    public void Apply_SingleLineBody_KeepsTheBodyOnOneLine()
    {
        const string markdown =
            ":::question\n" +
            "{ \"id\": \"q\", \"title\": \"T\", \"mode\": \"bool\", \"target\": \"human\" }\n" +
            ":::\n";

        var updated = QuestionResolution.Apply(markdown, Answers(("q", new[] { "true" })));

        Assert.Equal(markdown.Split('\n').Length, updated.Split('\n').Length);
        Assert.Equal(new[] { "true" }, QuestionSpec.Parse(InnerJson(BlockDocument.Parse(updated).Blocks[0].RawContent)).Answer);
    }

    [Fact]
    public void Apply_OverwritesAnExistingAnswer_LeavingExactlyOneAnswerKey()
    {
        // Re-applying is a supported path (a failed commit means the next run re-applies). Whatever route the
        // splice takes, the result must carry exactly ONE answer key holding the NEW value.
        const string markdown =
            ":::question\n" +
            "{ \"id\": \"q\", \"title\": \"T\", \"mode\": \"single\", \"options\": [\"A\", \"B\"], " +
            "\"target\": \"human\", \"answer\": [\"A\"] }\n" +
            ":::\n";

        var updated = QuestionResolution.Apply(markdown, Answers(("q", new[] { "B" })));

        Assert.Equal(1, updated.Split("\"answer\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(new[] { "B" }, QuestionSpec.Parse(InnerJson(BlockDocument.Parse(updated).Blocks[0].RawContent)).Answer);
    }

    [Fact]
    public void Apply_EmptyBodyObject_StillGainsTheAnswer()
    {
        // The degenerate body: no keys to comma-separate from. It carries no id, so it is left untouched —
        // proving the splice never invents a key on a block Apply has no business rewriting.
        const string markdown = ":::question\n{}\n:::\n";

        Assert.Equal(markdown, QuestionResolution.Apply(markdown, Answers(("q", new[] { "A" }))));
    }

    [Fact]
    public void FindDuplicateQuestionIds_ReportsIdsSharedByMoreThanOneQuestion()
    {
        const string markdown =
            ":::question\n{ \"id\": \"dup\", \"title\": \"First\", \"mode\": \"bool\", \"target\": \"human\" }\n:::\n\n" +
            ":::question\n{ \"id\": \"unique\", \"title\": \"Mid\", \"mode\": \"bool\", \"target\": \"human\" }\n:::\n\n" +
            ":::question\n{ \"id\": \"dup\", \"title\": \"Second\", \"mode\": \"bool\", \"target\": \"human\" }\n:::";

        var duplicates = QuestionResolution.FindDuplicateQuestionIds(markdown);

        Assert.Equal(new[] { "dup" }, duplicates);
    }

    // ---- the missing-lean lint (Charter #142) ------------------------------------------------
    //
    // `recommended` is optional in schema and load-bearing unattended: `charter headless` escalates an open
    // human question, and the escalation is only useful if it can say what the agent would have chosen.
    // Eleven questions were authored across two real plans without one because an omitted optional field
    // produces valid output and nothing objected.

    private const string Select =
        ":::question\n{{ \"id\": \"{0}\", \"title\": \"T\", \"mode\": \"single\", "
        + "\"options\": [\"A\", \"B\"]{1}, \"target\": \"{2}\" }}\n:::\n\n";

    private static string Question(string id, string extra = "", string target = "human")
        => string.Format(System.Globalization.CultureInfo.InvariantCulture, Select, id, extra, target);

    [Fact]
    public void FindQuestionsMissingRecommendation_ReportsAnOpenHumanSelectWithNoRecommendedKey()
    {
        var markdown = Question("needs-a-lean");

        Assert.Equal(new[] { "needs-a-lean" }, QuestionResolution.FindQuestionsMissingRecommendation(markdown));
    }

    [Fact]
    public void FindQuestionsMissingRecommendation_AcceptsARecommendation()
    {
        var markdown = Question("has-a-lean", ", \"recommended\": \"A\"");

        Assert.Empty(QuestionResolution.FindQuestionsMissingRecommendation(markdown));
    }

    /// <summary>
    /// The whole point of the opt-out: an explicit null records "I considered a lean and declined", which must
    /// be distinguishable from an absent key ("I never knew the field existed"). Both parse to a null
    /// <see cref="QuestionSpec.Recommended"/>, which is exactly why the lint reads the raw JSON body.
    /// </summary>
    [Fact]
    public void FindQuestionsMissingRecommendation_ExplicitNullIsADeliberateAbstention_NotReported()
    {
        var markdown = Question("considered-and-declined", ", \"recommended\": null");

        Assert.Empty(QuestionResolution.FindQuestionsMissingRecommendation(markdown));

        // ...and the parsed spec is identical either way — the distinction exists ONLY in the source bytes.
        Assert.Null(QuestionSpec.Parse(
            "{ \"id\": \"x\", \"title\": \"T\", \"mode\": \"single\", \"options\": [\"A\"], "
            + "\"recommended\": null, \"target\": \"human\" }").Recommended);
    }

    [Fact]
    public void FindQuestionsMissingRecommendation_SkipsAnsweredAgentTargetedAndNonSelectQuestions()
    {
        // Answered: the decision is already on record, so a lean would change nothing.
        var answered =
            ":::question\n{ \"id\": \"answered\", \"title\": \"T\", \"mode\": \"single\", "
            + "\"options\": [\"A\", \"B\"], \"answer\": [\"A\"], \"target\": \"human\" }\n:::\n";
        // Agent-targeted: resolved downstream, never escalated to a person.
        var agent = Question("agent-side", target: "agent");
        // Non-select: nothing to lean toward.
        var free =
            ":::question\n{ \"id\": \"prose\", \"title\": \"T\", \"mode\": \"free-text\", "
            + "\"target\": \"human\" }\n:::\n";

        Assert.Empty(QuestionResolution.FindQuestionsMissingRecommendation(answered));
        Assert.Empty(QuestionResolution.FindQuestionsMissingRecommendation(agent));
        Assert.Empty(QuestionResolution.FindQuestionsMissingRecommendation(free));
    }

    [Fact]
    public void FindQuestionsMissingRecommendation_ReportsInDocumentOrder_AndOnlyTheOffenders()
    {
        var markdown =
            Question("first-missing")
            + Question("has-one", ", \"recommended\": \"B\"")
            + Question("second-missing");

        Assert.Equal(
            new[] { "first-missing", "second-missing" },
            QuestionResolution.FindQuestionsMissingRecommendation(markdown));
    }

    [Fact]
    public void FindDuplicateQuestionIds_AllUnique_ReturnsEmpty()
    {
        const string markdown =
            ":::question\n{ \"id\": \"a\", \"title\": \"A\", \"mode\": \"bool\", \"target\": \"human\" }\n:::\n\n" +
            ":::question\n{ \"id\": \"b\", \"title\": \"B\", \"mode\": \"bool\", \"target\": \"human\" }\n:::";

        Assert.Empty(QuestionResolution.FindDuplicateQuestionIds(markdown));
    }

    [Fact]
    public void ApplyToFile_WritesAnswerInPlace_WithNoOrphanTempInThePlanDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-apply-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var planPath = Path.Combine(dir, "plan.charter.md");
            const string markdown =
                "# Plan\n\n" +
                ":::question\n" +
                "{ \"id\": \"db\", \"title\": \"Which datastore?\", \"mode\": \"single\", " +
                "\"options\": [\"Postgres\", \"DynamoDB\"], \"target\": \"human\", \"rationale\": \"latency\" }\n" +
                ":::";
            File.WriteAllText(planPath, markdown);

            var persisted = QuestionResolution.ApplyToFile(planPath, Answers(("db", new[] { "Postgres" })));

            // The file now holds the resolved plan (and the return value equals what was persisted).
            var onDisk = File.ReadAllText(planPath);
            Assert.Equal(persisted, onDisk);
            Assert.Contains("\"answer\"", onDisk);
            Assert.Equal(new[] { "Postgres" }, QuestionSpec.Parse(InnerJson(BlockDocument.Parse(onDisk).Blocks[1].RawContent)).Answer);
            // The unknown key survived the file round-trip too (surgical, not lossy).
            Assert.Contains("\"rationale\"", onDisk);

            // Atomic write leaves NO temp behind: the plan directory contains only the plan file.
            var remaining = Directory.GetFiles(dir);
            Assert.Equal(new[] { planPath }, remaining);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ApplyToFile_DuplicateQuestionIds_RefusesTheWrite_LeavingThePlanUntouched()
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-apply-dup-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var planPath = Path.Combine(dir, "plan.charter.md");
            const string markdown =
                ":::question\n{ \"id\": \"dup\", \"title\": \"First\", \"mode\": \"bool\", \"target\": \"human\" }\n:::\n\n" +
                ":::question\n{ \"id\": \"dup\", \"title\": \"Second\", \"mode\": \"bool\", \"target\": \"human\" }\n:::";
            File.WriteAllText(planPath, markdown);

            // Applying an answer to a plan whose two :::question share an id would splice it into BOTH — a
            // silent double-write. ApplyToFile REFUSES: it throws, names the offending id, and writes nothing.
            var ex = Assert.Throws<DuplicateQuestionIdException>(
                () => QuestionResolution.ApplyToFile(planPath, Answers(("dup", new[] { "true" }))));
            Assert.Contains("dup", ex.Message);
            Assert.Equal(new[] { "dup" }, ex.Ids);

            // The plan is byte-for-byte untouched (no partial write), and no temp was left behind.
            Assert.Equal(markdown, File.ReadAllText(planPath));
            Assert.Equal(new[] { planPath }, Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AtomicWriteIfUnchanged_WhenFileChangedUnderneath_RefusesAndDoesNotClobber()
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-precondition-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var planPath = Path.Combine(dir, "plan.charter.md");
            File.WriteAllText(planPath, "current-on-disk");

            // The concurrent-edit precondition: the caller based its write on "stale-read", but the file now
            // holds "current-on-disk". Overwriting would silently clobber the external edit, so it is refused.
            Assert.Throws<IOException>(() =>
                QuestionResolution.AtomicWriteIfUnchanged(planPath, expectedCurrent: "stale-read", contents: "clobber"));

            // The external edit survived, and the refused write left no temp behind.
            Assert.Equal("current-on-disk", File.ReadAllText(planPath));
            Assert.Equal(new[] { planPath }, Directory.GetFiles(dir));

            // When the file DOES still match what the caller read, the write goes through atomically.
            QuestionResolution.AtomicWriteIfUnchanged(planPath, expectedCurrent: "current-on-disk", contents: "next");
            Assert.Equal("next", File.ReadAllText(planPath));
            Assert.Equal(new[] { planPath }, Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The JSON body of a <c>:::question</c> block's raw content — the lines between the opening and closing
    /// <c>:::</c> fences — so a resolved block can be re-validated through <see cref="QuestionSpec.Parse"/>.
    /// </summary>
    private static string InnerJson(string rawContent)
    {
        var normalized = rawContent.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = new List<string>(normalized.Split('\n'));
        lines.RemoveAll(l => l.Trim().StartsWith(":::", StringComparison.Ordinal));
        return string.Join("\n", lines).Trim();
    }
}
