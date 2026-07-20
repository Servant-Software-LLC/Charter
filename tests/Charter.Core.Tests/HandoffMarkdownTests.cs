using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// TDD "red" for <see cref="HandoffMarkdown"/>, the seam that converts a reviewed Charter deliverable into
/// canonical plain CommonMark for the Guardrails handoff (invariant 5 — no MDX crosses the handoff). Every
/// <c>:::</c> directive FENCE LINE must be rewritten to plain markdown Guardrails can parse; prose, headings,
/// lists, tables, and fenced code pass through VERBATIM.
///
/// These compile against the existing surface (<see cref="BlockDocument.Parse(string)"/>,
/// <see cref="CharterRenderer.Render(string)"/>) plus the new <see cref="HandoffMarkdown.Emit"/> stub, and
/// FAIL at runtime because the stub throws <see cref="System.NotImplementedException"/>. Task
/// <c>05-implement-handoff-markdown</c> makes them pass.
///
/// LOAD-BEARING DISTINCTION: "no directive leaks" means no LINE begins with <c>:::</c> — NOT that the
/// substring <c>:::</c> never appears. Ordinary prose is free to <em>mention</em> directive syntax mid-line
/// (e.g. documenting Charter's own block catalog), and that mention correctly survives into the handoff.
/// So every "no directive leaks" assertion below is LINE-ANCHORED (<see cref="LineStartDirective"/>, a
/// <c>(?m)^:::</c> regex), never a bare <c>Contains(":::")</c> — test 12 proves the bare form is a false
/// positive, which is exactly why test 9 uses the line-anchored form.
/// </summary>
[Trait("Category", "HandoffMarkdown")]
public class HandoffMarkdownTests
{
    // The one "no directive leaks" check, shared by every relevant fact: a line-anchored (multiline) regex
    // that matches only a line whose FIRST characters are ":::". Asserted via Assert.DoesNotMatch, this is the
    // honest proxy for invariant 5 — and, unlike a bare Contains(":::"), it does not false-positive on prose
    // that merely mentions directive syntax mid-line (see FalsePositive_ProseMentioningDirectiveSyntax).
    private const string LineStartDirective = @"(?m)^:::";

    // A document with NO directives: a heading, a paragraph, a list, a table, and a fenced code block. There
    // is nothing to convert, so every piece must survive VERBATIM in source order.
    private const string PassThroughDoc =
        "# Title heading\n\n" +
        "A plain prose paragraph.\n\n" +
        "- first item\n" +
        "- second item\n\n" +
        "| Name | Role |\n" +
        "| --- | --- |\n" +
        "| Ada | Author |\n\n" +
        "```csharp\n" +
        "var answer = 42;\n" +
        "```";

    // A :::note callout — becomes a labeled blockquote, fence gone.
    private const string NoteDoc = ":::note\nAn important note.\n:::";

    // A :::warn callout — the same shape with the Warning label.
    private const string WarnDoc = ":::warn\nA serious warning.\n:::";

    // A :::comparison wrapping an already-plain markdown list — only the fence lines are stripped.
    private const string ComparisonDoc =
        ":::comparison\n" +
        "- Option A: fast but risky\n" +
        "- Option B: slow but safe\n" +
        ":::";

    // A :::diagram wrapping raw Mermaid source — becomes a fenced ```mermaid code block.
    private const string DiagramDoc = ":::diagram\ngraph TD; A-->B;\n:::";

    // A :::diff wrapping unified-diff lines — becomes a fenced ```diff code block.
    private const string DiffDoc =
        ":::diff\n" +
        "+new feature added\n" +
        "-old behavior removed\n" +
        " unchanged context\n" +
        ":::";

    // A single-select :::question with the exact schema the ANSWERED / UNANSWERED facts pin.
    private const string SingleQuestionDoc =
        ":::question\n" +
        "{\"id\":\"q1\",\"title\":\"Pick one\",\"mode\":\"single\",\"options\":[\"A\",\"B\"],\"target\":\"human\"}\n" +
        ":::";

    // One of EVERY directive kind plus surrounding prose — the global "no fence lines leak" fixture, also
    // reused by the self-parse round-trip and the no-annotation-artifacts facts.
    private const string MixedDirectiveDoc =
        "# Handoff heading\n\n" +
        "Intro paragraph.\n\n" +
        ":::note\nAn important note.\n:::\n\n" +
        ":::warn\nA serious warning.\n:::\n\n" +
        ":::comparison\n- Option A: fast but risky\n- Option B: slow but safe\n:::\n\n" +
        ":::diagram\ngraph TD; A-->B;\n:::\n\n" +
        ":::diff\n+new feature added\n-old behavior removed\n unchanged context\n:::\n\n" +
        ":::question\n{\"id\":\"q9\",\"title\":\"Which approach?\",\"mode\":\"single\",\"options\":[\"X\",\"Y\"],\"target\":\"human\"}\n:::\n\n" +
        "Closing paragraph.";

    [Fact]
    public void Emit_ProseHeadingListTableCode_PassThroughVerbatim()
    {
        var output = HandoffMarkdown.Emit(PassThroughDoc);

        // Nothing to convert (no directive involved) — every non-directive block survives unchanged.
        Assert.Contains("# Title heading", output);
        Assert.Contains("A plain prose paragraph.", output);
        Assert.Contains("- first item", output);
        Assert.Contains("- second item", output);
        Assert.Contains("| Name | Role |", output);
        Assert.Contains("```csharp", output);
        Assert.Contains("var answer = 42;", output);
    }

    [Fact]
    public void Emit_Note_BecomesLabeledBlockquote_FenceGone()
    {
        var output = HandoffMarkdown.Emit(NoteDoc);

        // No line begins with ::: (line-anchored — the proxy for invariant 5, never a bare substring search).
        Assert.DoesNotMatch(LineStartDirective, output);

        // A blockquote (">" at line start), the Note label, and the note's own text.
        Assert.Matches(@"(?m)^>", output);
        Assert.Contains("**Note:**", output);
        Assert.Contains("An important note.", output);
    }

    [Fact]
    public void Emit_Warn_BecomesLabeledBlockquote_FenceGone()
    {
        var output = HandoffMarkdown.Emit(WarnDoc);

        Assert.DoesNotMatch(LineStartDirective, output);
        Assert.Matches(@"(?m)^>", output);
        Assert.Contains("**Warning:**", output);
        Assert.Contains("A serious warning.", output);
    }

    [Fact]
    public void Emit_Comparison_BecomesInnerList_FenceGone()
    {
        var output = HandoffMarkdown.Emit(ComparisonDoc);

        // The inner content is already plain CommonMark: only the fence lines are stripped, so every option's
        // own text survives verbatim.
        Assert.DoesNotMatch(LineStartDirective, output);
        Assert.Contains("Option A: fast but risky", output);
        Assert.Contains("Option B: slow but safe", output);
    }

    [Fact]
    public void Emit_Diagram_BecomesFencedMermaidCodeBlock_FenceGone()
    {
        var output = HandoffMarkdown.Emit(DiagramDoc);

        Assert.DoesNotMatch(LineStartDirective, output);

        // A fenced ```mermaid opener, with the raw Mermaid source preserved verbatim as the block's text.
        Assert.Contains("```mermaid", output);
        Assert.Contains("graph TD", output);
    }

    [Fact]
    public void Emit_Diff_BecomesFencedDiffCodeBlock_FenceGone()
    {
        var output = HandoffMarkdown.Emit(DiffDoc);

        Assert.DoesNotMatch(LineStartDirective, output);

        // A fenced ```diff opener, with at least one diff line's text preserved verbatim.
        Assert.Contains("```diff", output);
        Assert.Contains("+new feature added", output);
    }

    [Fact]
    public void Emit_Question_Answered_ResolvesToSelectedAnswer()
    {
        // The resolved-answer lookup: question id -> selected value(s), the same shape as Answer.Values.
        var answers = new Dictionary<string, IReadOnlyList<string>>
        {
            ["q1"] = new[] { "A" },
        };

        var output = HandoffMarkdown.Emit(SingleQuestionDoc, answers);

        Assert.DoesNotMatch(LineStartDirective, output);

        // The title, the resolved answer, and the ANSWERED-format marker (a distinct token from test 8's
        // "Open question", so no single weak assertion can satisfy both the answered and unanswered scenarios).
        Assert.Contains("Pick one", output);
        Assert.Contains("A", output);
        Assert.Contains("Answered:", output);

        // Not flagged open, and the raw JSON body is gone (the "mode" token must not survive).
        Assert.DoesNotContain("Open question", output);
        Assert.DoesNotContain("\"mode\"", output);
    }

    [Fact]
    public void Emit_Question_Unanswered_FlagsOpenQuestion()
    {
        // No answers supplied (null) — the question was never answered.
        var output = HandoffMarkdown.Emit(SingleQuestionDoc, answers: null);

        Assert.DoesNotMatch(LineStartDirective, output);

        // The title survives, the question is clearly flagged open, and the raw JSON body is gone.
        Assert.Contains("Pick one", output);
        Assert.Contains("Open question", output);
        Assert.DoesNotContain("\"mode\"", output);
    }

    [Fact]
    public void Emit_MixedDirectives_NoFenceLineLeaksGlobally()
    {
        var output = HandoffMarkdown.Emit(MixedDirectiveDoc);

        // The whole-output proxy for invariant 5: NO line in the entire handoff begins with :::. A single
        // line-anchored assertion — deliberately NOT Contains(":::"), which test 12 proves is a false positive.
        Assert.DoesNotMatch(LineStartDirective, output);
    }

    [Fact]
    public void Emit_Output_SelfParsesThroughCharterOwnPipeline()
    {
        var output = HandoffMarkdown.Emit(MixedDirectiveDoc);

        // The handoff's own self-consistency: its emitted markdown is itself well-formed input to Charter's
        // Markdig pipeline — the renderer parses it without throwing, and it parses into at least one block.
        // (This is NOT identity with the original block set: invariant 5 requires converting the directives
        // away, so the original :::containers cannot survive unchanged.)
        var renderException = Record.Exception(() => CharterRenderer.Render(output));
        Assert.Null(renderException);
        Assert.NotEmpty(BlockDocument.Parse(output).Blocks);
    }

    [Fact]
    public void Emit_Output_ContainsNoAnnotationLoopArtifacts()
    {
        var output = HandoffMarkdown.Emit(MixedDirectiveDoc);

        // The handoff is plain markdown TEXT derived from the source, never a leaked fragment of the
        // rendered/annotated HTML the review server produces.
        Assert.DoesNotContain("data-anchor", output);
        Assert.DoesNotContain("<script", output);
        Assert.DoesNotContain("data-charter-sdk", output);
    }

    [Fact]
    public void Emit_ProseMentioningDirectiveSyntax_IsNotTreatedAsALeak()
    {
        // Ordinary prose describing Charter's OWN syntax — no actual directive container is involved. This is
        // the exact pattern this plan-of-record's "Format & block catalog" section uses.
        const string proseSentence = "Use `:::note` for a callout and `:::diagram` for Mermaid.";

        var output = HandoffMarkdown.Emit(proseSentence);

        // The sentence passes through UNCHANGED, including both of its ::: mentions.
        Assert.Contains(proseSentence, output);

        // The substring ":::" IS present — so a bare Contains(":::") check (test 9's REJECTED alternative)
        // would WRONGLY flag this clean prose as a directive leak...
        Assert.Contains(":::", output);

        // ...while the real, line-anchored (?m)^::: check correctly PASSES: no LINE begins with :::. This is
        // precisely why invariant 5 is checked line-anchored, not by bare substring.
        Assert.DoesNotMatch(LineStartDirective, output);
    }

    [Fact]
    public void Emit_DiffBodyContainingBacktickFences_StaysOneCodeBlock()
    {
        // A :::diff of a markdown file whose body itself contains ``` fence lines. Diff context lines carry a
        // leading space, so " ``` " is a 3-backtick line indented one space — a VALID CommonMark closing
        // fence for a hard-coded 3-backtick opener. Emitting the body inside a 3-backtick fence would let the
        // inner ``` close the block early and split it into misattributed blocks; the emitted fence must be
        // longer than any inner backtick run so the whole body stays ONE code block (no breakout).
        const string diffDoc =
            ":::diff\n" +
            " ```\n" +
            " unchanged code line\n" +
            " ```\n" +
            ":::";

        var blocks = BlockDocument.Parse(HandoffMarkdown.Emit(diffDoc)).Blocks;

        var code = Assert.Single(blocks);
        Assert.Equal(BlockKind.Code, code.Kind);
        Assert.Contains("unchanged code line", code.RawContent);
        Assert.Contains("```", code.RawContent);
    }

    [Fact]
    public void Emit_DiagramBodyContainingBacktickFences_StaysOneCodeBlock()
    {
        // The same breakout risk for a :::diagram whose body carries bare ``` lines: a hard-coded 3-backtick
        // ```mermaid wrapper would be closed early by the inner ```; the fence must exceed the inner run so the
        // whole Mermaid source survives as ONE code block.
        const string diagramDoc =
            ":::diagram\n" +
            "```\n" +
            "graph TD; A-->B;\n" +
            "```\n" +
            ":::";

        var blocks = BlockDocument.Parse(HandoffMarkdown.Emit(diagramDoc)).Blocks;

        var code = Assert.Single(blocks);
        Assert.Equal(BlockKind.Code, code.Kind);
        Assert.Contains("graph TD; A-->B;", code.RawContent);
        Assert.Contains("```", code.RawContent);
    }

    [Fact]
    public void Emit_MixedDocument_PreservesCrossBlockSourceOrder()
    {
        // Heading, then :::note, then :::diagram, then a trailing paragraph.
        const string orderedDoc =
            "# Order heading\n\n" +
            ":::note\nA note in the middle.\n:::\n\n" +
            ":::diagram\ngraph TD; X-->Y;\n:::\n\n" +
            "A trailing paragraph.";

        var output = HandoffMarkdown.Emit(orderedDoc);

        // Every landmark is present...
        Assert.Contains("Order heading", output);
        Assert.Contains("A note in the middle.", output);
        Assert.Contains("```mermaid", output);
        Assert.Contains("A trailing paragraph.", output);

        // ...and in the SAME relative order as the source. An implementation that groups output by block kind,
        // or defers directive conversions to the end, passes every single-block-kind fact yet fails here.
        var headingIndex = output.IndexOf("Order heading", StringComparison.Ordinal);
        var noteIndex = output.IndexOf("A note in the middle.", StringComparison.Ordinal);
        var mermaidIndex = output.IndexOf("```mermaid", StringComparison.Ordinal);
        var trailingIndex = output.IndexOf("A trailing paragraph.", StringComparison.Ordinal);

        Assert.True(headingIndex < noteIndex, "heading must precede the note");
        Assert.True(noteIndex < mermaidIndex, "note must precede the diagram");
        Assert.True(mermaidIndex < trailingIndex, "diagram must precede the trailing paragraph");
    }
}
