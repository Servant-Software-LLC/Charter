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
