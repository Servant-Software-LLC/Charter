using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Tests the migration-bridge faithfulness fix (Architecture B, DA blocker 1). While <c>charter handoff</c>
/// survives as the bridge to a not-yet-living-document Guardrails, a RESOLVED living-document
/// <c>:::question</c> carries its decision INLINE (<c>QuestionSpec.Answer</c>), not in an external answers
/// dict. Before the fix, <see cref="HandoffMarkdown.EmitQuestion"/> read only the external dict, so a
/// resolved <c>.charter.md</c> flattened as ALL-QUESTIONS-OPEN and every human decision was silently lost.
/// The fix: when the dict lacks the id, fall back to the inline <c>answer</c>. The external dict, when it
/// does carry the id, still takes precedence.
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","HandoffInlineAnswer")].
/// </summary>
[Trait("Category", "HandoffInlineAnswer")]
public class HandoffInlineAnswerTests
{
    // A RESOLVED question: the decision rides inline in the block body, no external answers involved.
    private const string ResolvedQuestionDoc =
        ":::question\n" +
        "{ \"id\": \"db\", \"title\": \"Which datastore?\", \"mode\": \"single\", " +
        "\"options\": [\"Postgres\", \"DynamoDB\"], \"target\": \"human\", \"answer\": [\"Postgres\"] }\n" +
        ":::";

    // An OPEN question: no inline answer, no external answer.
    private const string OpenQuestionDoc =
        ":::question\n" +
        "{ \"id\": \"db\", \"title\": \"Which datastore?\", \"mode\": \"single\", " +
        "\"options\": [\"Postgres\", \"DynamoDB\"], \"target\": \"human\" }\n" +
        ":::";

    [Fact]
    public void Emit_ResolvedInlineAnswer_NoExternalDict_FlattensAsAnswered()
    {
        // The DA-blocker-1 fix: with answers = null, the inline answer is honored, so the decision is NOT lost.
        var output = HandoffMarkdown.Emit(ResolvedQuestionDoc, answers: null);

        Assert.Contains("Answered:", output);
        Assert.Contains("Postgres", output);
        Assert.DoesNotContain("Open question", output);
        // The raw JSON body never leaks, and no ::: fence line survives.
        Assert.DoesNotContain("\"mode\"", output);
        Assert.DoesNotMatch(@"(?m)^:::", output);
    }

    [Fact]
    public void Emit_OpenQuestion_NoInlineAnswer_StillFlagsOpen()
    {
        // The complement: an open question (no inline answer) must still flatten as clearly open — the inline
        // fallback must not turn an open question into a spuriously-answered one.
        var output = HandoffMarkdown.Emit(OpenQuestionDoc, answers: null);

        Assert.Contains("Open question", output);
        Assert.DoesNotContain("Answered:", output);
    }

    [Fact]
    public void Emit_ExternalDict_TakesPrecedenceOverInlineAnswer()
    {
        // When BOTH are present, the external answers dict wins (the freshly-drained answer is authoritative
        // over whatever was previously written inline).
        var answers = new Dictionary<string, IReadOnlyList<string>> { ["db"] = new[] { "DynamoDB" } };

        var output = HandoffMarkdown.Emit(ResolvedQuestionDoc, answers);

        Assert.Contains("Answered:", output);
        Assert.Contains("DynamoDB", output);
        Assert.DoesNotContain("Postgres", output);
    }
}
