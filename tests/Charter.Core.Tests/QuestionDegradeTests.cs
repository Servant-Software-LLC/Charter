using System.IO;
using System.Linq;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// A malformed or empty <c>:::question</c> must DEGRADE to a visible placeholder rather than throwing and
/// aborting the whole render / handoff / export (which would take down the served review page and every other
/// block with it). These pin: <see cref="CharterRenderer.Render(string)"/> emits a <c>question-error</c> div
/// carrying the block's stable id (so it stays annotatable); <see cref="HandoffMarkdown.Emit"/> emits a
/// clearly-flagged line; <see cref="ArtifactExporter.Export"/> (which renders underneath) carries the same
/// placeholder — and in every case every OTHER block still renders. A pathologically deep-nested input
/// (which Markdig aborts with an <see cref="System.ArgumentException"/> from parse) also degrades rather than
/// throwing.
/// </summary>
[Trait("Category", "QuestionDegrade")]
public class QuestionDegradeTests
{
    // A valid paragraph followed by a malformed :::question — parameterized over bad JSON, an unknown mode,
    // a missing id, and an empty body. The paragraph must survive in every case; the question must degrade.
    public static IEnumerable<object[]> MalformedDocs()
    {
        yield return new object[] { "A valid paragraph.\n\n:::question\n{ not valid json\n:::" };
        yield return new object[] { "A valid paragraph.\n\n:::question\n{\"id\":\"q1\",\"title\":\"T\",\"mode\":\"dropdown\",\"target\":\"human\",\"options\":[\"A\"]}\n:::" };
        yield return new object[] { "A valid paragraph.\n\n:::question\n{\"title\":\"T\",\"mode\":\"single\",\"target\":\"human\",\"options\":[\"A\"]}\n:::" };
        yield return new object[] { "A valid paragraph.\n\n:::question\n:::" };
    }

    [Theory]
    [MemberData(nameof(MalformedDocs))]
    public void Render_MalformedQuestion_DegradesToErrorPlaceholderCarryingBlockId(string doc)
    {
        var questionBlock = BlockDocument.Parse(doc).Blocks.Single(b => b.Kind == BlockKind.Question);

        var ex = Record.Exception(() => CharterRenderer.Render(doc));
        Assert.Null(ex); // does NOT throw

        var html = CharterRenderer.Render(doc);

        // Every other block still renders.
        Assert.Contains("A valid paragraph.", html);

        // The question region is an error placeholder that KEEPS the block's stable id (still annotatable),
        // and no <form> is emitted for the broken question.
        Assert.Contains($"<div class=\"question-error\" id=\"{questionBlock.Id}\"", html);
        Assert.DoesNotContain("<form", html);
    }

    [Theory]
    [MemberData(nameof(MalformedDocs))]
    public void Emit_MalformedQuestion_DegradesToFlaggedLine_OtherBlocksSurvive(string doc)
    {
        var ex = Record.Exception(() => HandoffMarkdown.Emit(doc));
        Assert.Null(ex);

        var output = HandoffMarkdown.Emit(doc);

        Assert.Contains("A valid paragraph.", output);
        Assert.Contains("Malformed question", output);

        // The flag is a single blockquote line — no line starts with ::: (invariant 5), even though the parse
        // reason may MENTION ":::question" mid-line.
        Assert.DoesNotMatch(@"(?m)^:::", output);
    }

    [Theory]
    [MemberData(nameof(MalformedDocs))]
    public void Export_MalformedQuestion_DegradesToPlaceholder(string doc)
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-degrade-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ex = Record.Exception(() => ArtifactExporter.Export(doc, dir));
            Assert.Null(ex);

            var html = ArtifactExporter.Export(doc, dir);
            Assert.Contains("A valid paragraph.", html);
            Assert.Contains("question-error", html);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- Charter #203: a LIVE-NESTED question degrades too, for a different reason -------------------------

    private const string NestedQuestion =
        "A valid paragraph.\n\n::::note\nA callout.\n\n:::question\n"
        + "{\"id\":\"q-nested\",\"title\":\"Which store?\",\"mode\":\"single\",\"target\":\"human\","
        + "\"options\":[\"Postgres\",\"DynamoDB\"]}\n:::\n::::\n";

    /// <summary>
    /// The body here is perfectly VALID — it degrades because of where it sits, not what it says. The renderer
    /// would otherwise draw a real form whose every outcome is a lie: no anchor, absent from the handoff and
    /// the record, and an answer that can never be folded back.
    /// </summary>
    [Fact]
    public void Render_LiveNestedQuestion_DegradesToANonAnswerablePlaceholder()
    {
        var html = CharterRenderer.Render(NestedQuestion);

        Assert.Contains("A valid paragraph.", html);
        Assert.Contains("question-error", html);

        // Non-answerable, structurally: nothing for the SDK to bind a submit to.
        Assert.DoesNotContain("<form", html);
        Assert.DoesNotContain("data-question-id", html);

        // It names the defect and the title, so a reviewer is told rather than left to discover it.
        Assert.Contains("cannot be answered here", html);
        Assert.Contains("Which store?", html);
        Assert.Contains("must be a top-level block", html);
    }

    /// <summary>
    /// It carries NO id, because a nested block has none — <c>RenderBody</c>'s anchor pass is top-level-only
    /// and #166 settled that a note on a nested block resolves OUTWARD, to the enclosing callout. Emitting an
    /// id here would invent an anchor <see cref="SourceMap"/> cannot map to a line.
    /// </summary>
    [Fact]
    public void TheNestedPlaceholderCarriesNoAnchorId()
    {
        Assert.Contains("<div class=\"question-error\">", CharterRenderer.Render(NestedQuestion));
    }

    /// <summary>
    /// <b>Invariant 1.</b> The degrade ships IN THE ARTIFACT, deliberately — a standalone artifact carrying a
    /// dead form is a lie standalone, so this can never be a serve-time affordance.
    /// </summary>
    [Fact]
    public void Export_LiveNestedQuestion_CarriesThePlaceholder_NotAForm()
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-nested-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var html = ArtifactExporter.Export(NestedQuestion, dir);

            Assert.Contains("cannot be answered here", html);
            Assert.DoesNotContain("<form", html);
            Assert.DoesNotContain("data-question-id", html);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A TOP-LEVEL question is untouched by any of this — the positive control that stops the degrade being
    /// green because questions stopped rendering at all.
    /// </summary>
    [Fact]
    public void ATopLevelQuestionStillRendersItsForm()
    {
        var html = CharterRenderer.Render(
            "A valid paragraph.\n\n:::question\n"
            + "{\"id\":\"q-top\",\"title\":\"Which store?\",\"mode\":\"single\",\"target\":\"human\","
            + "\"options\":[\"Postgres\",\"DynamoDB\"]}\n:::\n");

        Assert.Contains("data-question-id=\"q-top\"", html);

        // Scoped to the placeholder's own wording, NOT to "question-error": that class name is DECLARED in the
        // bundled stylesheet the shell inlines, so a whole-document scan for it always finds the CSS rule.
        Assert.DoesNotContain("cannot be answered here", html);
    }

    [Fact]
    public void Render_PathologicallyDeepNesting_DoesNotThrow()
    {
        // ~500 nested blockquotes exceed Markdig's nesting-depth limit; ParseDocument aborts with an
        // ArgumentException. Render must degrade to a placeholder rather than propagating the throw.
        var input = string.Concat(Enumerable.Repeat("> ", 500)) + "x";

        var ex = Record.Exception(() => CharterRenderer.Render(input));

        Assert.Null(ex);
    }
}
