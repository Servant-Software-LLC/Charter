using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #48 / C3 + C4 — the flattened handoff must PRESERVE the question metadata Guardrails routes on.
///
/// Measured on real output before the fix: an ANSWERED question lost its <c>options</c> while an OPEN one kept
/// them (asymmetric), and <c>id</c>, <c>target</c> and <c>mode</c> were dropped entirely. Two consequences,
/// both verified end to end:
/// <list type="bullet">
///   <item><description><c>charter-format</c> tells the interpreter to fold a resolved answer in "keeping the
///     <c>options</c> as rationale" — the REJECTED option is what lets the breakdown author a guardrail that
///     FAILS if the implementation reaches for it. Dropping options on answered destroyed exactly that.</description></item>
///   <item><description><c>target</c> is the routing signal the headless breakdown branches on, so the
///     flattened path structurally could not honour <c>target: agent</c> — the flattened DAG halted for a human
///     on a decision the author had explicitly delegated to the agent.</description></item>
/// </list>
///
/// The emitted shape is one compact, plain-CommonMark line under the Answered/Open line, identical either way:
/// <c>_Question — id: `q`; mode: `single`; target: `human`; options: `A`, `B`_</c>.
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","HandoffQuestionMetadata")].
/// </summary>
[Trait("Category", "HandoffQuestionMetadata")]
public class HandoffQuestionMetadataTests
{
    private const string AnsweredDoc =
        ":::question\n" +
        "{ \"id\": \"db-choice\", \"title\": \"Which datastore for the read path?\", \"mode\": \"single\", " +
        "\"options\": [\"Postgres\", \"DynamoDB\"], \"target\": \"human\", \"answer\": [\"Postgres\"] }\n" +
        ":::";

    private const string OpenDoc =
        ":::question\n" +
        "{ \"id\": \"db-choice\", \"title\": \"Which datastore for the read path?\", \"mode\": \"single\", " +
        "\"options\": [\"Postgres\", \"DynamoDB\"], \"target\": \"human\" }\n" +
        ":::";

    private const string AgentTargetedDoc =
        ":::question\n" +
        "{ \"id\": \"retry-policy\", \"title\": \"Which retry policy?\", \"mode\": \"single\", " +
        "\"options\": [\"exponential\", \"fixed\"], \"target\": \"agent\" }\n" +
        ":::";

    [Fact]
    public void Emit_AnsweredQuestion_KeepsAnswerAndOptionsAndIdTargetMode()
    {
        var output = HandoffMarkdown.Emit(AnsweredDoc);

        // The decision itself...
        Assert.Contains("Answered: Postgres", output, StringComparison.Ordinal);

        // ...its rationale (the REJECTED option is the point — it is what a guardrail can be written against)...
        Assert.Contains("options: `Postgres`, `DynamoDB`", output, StringComparison.Ordinal);

        // ...and the routing metadata that used to be dropped outright (0 occurrences of each).
        Assert.Contains("id: `db-choice`", output, StringComparison.Ordinal);
        Assert.Contains("mode: `single`", output, StringComparison.Ordinal);
        Assert.Contains("target: `human`", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Emit_OpenQuestion_CarriesTheSameMetadata_Symmetrically()
    {
        var open = HandoffMarkdown.Emit(OpenDoc);
        var answered = HandoffMarkdown.Emit(AnsweredDoc);

        Assert.Contains("Open question (unresolved)", open, StringComparison.Ordinal);

        // The metadata line is IDENTICAL in both flavours — the asymmetry (options only on open) is gone, so a
        // consumer parses one shape regardless of whether the question was settled.
        const string metadata =
            "_Question — id: `db-choice`; mode: `single`; target: `human`; options: `Postgres`, `DynamoDB`_";
        Assert.Contains(metadata, open, StringComparison.Ordinal);
        Assert.Contains(metadata, answered, StringComparison.Ordinal);
    }

    [Fact]
    public void Emit_AgentTargetedQuestion_IsDistinguishableFromHumanTargeted_AfterFlattening()
    {
        // The C4 consequence, stated as its own fact: after flattening, a `target: agent` question must still
        // be tellable from a `target: human` one. Without it the headless path cannot honour the delegation and
        // halts for a human on a decision the author explicitly handed to the agent.
        var agent = HandoffMarkdown.Emit(AgentTargetedDoc);
        var human = HandoffMarkdown.Emit(OpenDoc);

        Assert.Contains("target: `agent`", agent, StringComparison.Ordinal);
        Assert.DoesNotContain("target: `human`", agent, StringComparison.Ordinal);

        Assert.Contains("target: `human`", human, StringComparison.Ordinal);
        Assert.DoesNotContain("target: `agent`", human, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("free-text", "free-text")]
    [InlineData("bool", "bool")]
    [InlineData("number", "number")]
    [InlineData("multi", "multi")]
    public void Emit_EveryMode_EmitsItsAuthoringToken_NotTheEnumName(string mode, string expectedToken)
    {
        // The mode token is the AUTHORING token (single/multi/free-text/bool/number), single-sourced from
        // QuestionSpec.Token — never the C# enum name (SingleSelect/FreeText/…), which no consumer knows.
        var options = mode == "multi" ? ", \"options\": [\"A\", \"B\"]" : string.Empty;
        var doc =
            ":::question\n"
            + $"{{ \"id\": \"q\", \"title\": \"T\", \"mode\": \"{mode}\", \"target\": \"agent\"{options} }}\n"
            + ":::";

        var output = HandoffMarkdown.Emit(doc);

        Assert.Contains($"mode: `{expectedToken}`", output, StringComparison.Ordinal);
        Assert.DoesNotContain("SingleSelect", output, StringComparison.Ordinal);
        Assert.DoesNotContain("FreeText", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Emit_QuestionMetadata_StaysPlainCommonMark_NoDirectiveLeak_AndSelfParses()
    {
        // The headless contract: the flattened plan is PLAIN CommonMark. The metadata line must not reopen a
        // ::: directive, must not leak the raw JSON body, and the whole output must still self-parse.
        foreach (var doc in new[] { AnsweredDoc, OpenDoc, AgentTargetedDoc })
        {
            var output = HandoffMarkdown.Emit(doc);

            Assert.DoesNotMatch(@"(?m)^:::", output);
            Assert.DoesNotContain("\"mode\"", output, StringComparison.Ordinal);
            Assert.DoesNotContain("\"target\"", output, StringComparison.Ordinal);
            Assert.Null(Record.Exception(() => CharterRenderer.Render(output)));
            Assert.NotEmpty(BlockDocument.Parse(output).Blocks);
        }
    }

    [Fact]
    public void Emit_OptionFreeMode_OmitsTheOptionsSegment_ButKeepsIdModeTarget()
    {
        // free-text/bool/number declare no options, so the segment is simply absent — the line stays honest
        // rather than emitting an empty "options:".
        var output = HandoffMarkdown.Emit(
            ":::question\n{ \"id\": \"why\", \"title\": \"Why?\", \"mode\": \"free-text\", \"target\": \"human\" }\n:::");

        Assert.Contains("_Question — id: `why`; mode: `free-text`; target: `human`_", output, StringComparison.Ordinal);
        Assert.DoesNotContain("options:", output, StringComparison.Ordinal);
    }
}
