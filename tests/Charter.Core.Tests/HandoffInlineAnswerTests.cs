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
    public void Emit_ResolvedBoolFalse_FlattensAsAnsweredNotOpen()
    {
        // Charter #43: a resolved bool "No" (answer ["false"]) is a real decision, NOT an open question. Its
        // inline answer is non-empty, so it must flatten as Answered — a false/No is distinguishable from an
        // unanswered question, exactly as a resolved single-select is.
        const string resolvedBoolFalse =
            ":::question\n" +
            "{ \"id\": \"flag\", \"title\": \"Enable the feature flag?\", \"mode\": \"bool\", " +
            "\"target\": \"human\", \"answer\": [\"false\"] }\n" +
            ":::";

        var output = HandoffMarkdown.Emit(resolvedBoolFalse, answers: null);

        Assert.Contains("Answered:", output);
        Assert.Contains("false", output);
        Assert.DoesNotContain("Open question", output);
    }

    [Fact]
    public void Emit_ExternalDict_TakesPrecedenceOverInlineAnswer()
    {
        // When BOTH are present, the external answers dict wins (the freshly-drained answer is authoritative
        // over whatever was previously written inline).
        var answers = new Dictionary<string, IReadOnlyList<string>> { ["db"] = new[] { "DynamoDB" } };

        var output = HandoffMarkdown.Emit(ResolvedQuestionDoc, answers);

        Assert.Contains("Answered:", output);

        // Precedence is asserted on the ANSWERED LINE itself, not on the whole output: since Charter #48/C3 the
        // question's `options` are preserved beneath an answered question too (they are the rationale — the
        // REJECTED option is what lets the breakdown guard against reaching for it), so "Postgres" legitimately
        // still appears in the document as an OPTION. What must not happen is Postgres surviving as the ANSWER.
        var answered = AnsweredLine(output);
        Assert.Contains("DynamoDB", answered);
        Assert.DoesNotContain("Postgres", answered);

        // ...and the rejected option is still on the record, exactly once, as an option.
        Assert.Contains("options: `Postgres`, `DynamoDB`", output);
    }

    /// <summary>The single <c>**Q: … — Answered: …**</c> line of a flattened handoff.</summary>
    private static string AnsweredLine(string output)
    {
        foreach (var line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Contains("Answered:", StringComparison.Ordinal))
            {
                return line;
            }
        }

        Assert.Fail("the handoff carried no Answered line.");
        return string.Empty;
    }
}
