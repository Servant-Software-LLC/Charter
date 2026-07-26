using System.Text.RegularExpressions;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #48 / C2 — <c>charter handoff</c> must NOT double-fence a <c>:::diagram</c> or <c>:::diff</c>.
///
/// The documented authoring form wraps the body in its own <c>```mermaid</c> / <c>```diff</c> fence (the
/// renderer accepts that AND a raw body — Charter #40). The handoff, however, wrapped unconditionally, so an
/// already-fenced body came out as <c>````mermaid</c> around <c>```mermaid</c>: the outer fence turns the
/// inner one into literal content, and the flattened plan's diagram renders on GitHub as a code listing
/// rather than a diagram — which breaks the flattened plan as a PR review artifact.
///
/// These pin: BOTH authoring forms flatten to EXACTLY ONE fence of the right language, the content survives,
/// no fence marker leaks into the code block's text, and the self-parse round-trip (invariant 5) still holds.
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","HandoffFenceFidelity")].
/// </summary>
[Trait("Category", "HandoffFenceFidelity")]
public class HandoffFenceFidelityTests
{
    // The documented authoring form: the container's body IS a fenced code block.
    private const string FencedDiagramDoc =
        ":::diagram\n" +
        "```mermaid\n" +
        "graph TD; A-->B;\n" +
        "```\n" +
        ":::";

    // The other accepted form: a raw, fence-less Mermaid body.
    private const string RawDiagramDoc = ":::diagram\ngraph TD; A-->B;\n:::";

    private const string FencedDiffDoc =
        ":::diff\n" +
        "```diff\n" +
        "+new feature added\n" +
        "-old behavior removed\n" +
        "```\n" +
        ":::";

    private const string RawDiffDoc =
        ":::diff\n" +
        "+new feature added\n" +
        "-old behavior removed\n" +
        ":::";

    [Theory]
    [InlineData(FencedDiagramDoc)]
    [InlineData(RawDiagramDoc)]
    public void Emit_Diagram_EmitsExactlyOneMermaidFence_WhicheverAuthoringFormWasUsed(string doc)
    {
        var output = HandoffMarkdown.Emit(doc);

        // Exactly ONE ```mermaid opener — the double-fenced bug emitted ````mermaid AROUND ```mermaid, so the
        // inner opener survived as literal text and this count was 2.
        Assert.Single(Regex.Matches(output, "(?m)^`+mermaid$"));

        // ...and it is a plain three-backtick fence: nothing forced an escalation, so GitHub renders it.
        Assert.StartsWith("```mermaid\n", output.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("graph TD; A-->B;", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FencedDiffDoc)]
    [InlineData(RawDiffDoc)]
    public void Emit_Diff_EmitsExactlyOneDiffFence_WhicheverAuthoringFormWasUsed(string doc)
    {
        var output = HandoffMarkdown.Emit(doc);

        Assert.Single(Regex.Matches(output, "(?m)^`+diff$"));
        Assert.StartsWith("```diff\n", output.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("+new feature added", output, StringComparison.Ordinal);
        Assert.Contains("-old behavior removed", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Emit_FencedDiagram_ParsesAsOneRenderableMermaidCodeBlock()
    {
        // The real acceptance criterion: the flattened file's mermaid block is a code block whose LANGUAGE is
        // mermaid and whose CONTENT is the diagram source — no stray fence marker inside it. Under the
        // double-fence bug the content began with the literal line "```mermaid", which is what stopped GitHub
        // rendering it as a diagram.
        var output = HandoffMarkdown.Emit(FencedDiagramDoc);

        var code = Assert.Single(BlockDocument.Parse(output).Blocks);
        Assert.Equal(BlockKind.Code, code.Kind);

        var inner = InnerFenceContent(output);
        Assert.Equal("graph TD; A-->B;", inner);
    }

    [Fact]
    public void Emit_FencedDiff_ParsesAsOneDiffCodeBlockWithNoFenceLeak()
    {
        var output = HandoffMarkdown.Emit(FencedDiffDoc);

        var code = Assert.Single(BlockDocument.Parse(output).Blocks);
        Assert.Equal(BlockKind.Code, code.Kind);
        Assert.Equal("+new feature added\n-old behavior removed", InnerFenceContent(output));
    }

    [Fact]
    public void Emit_FencedForms_StillSelfParseWithNoDirectiveLeak()
    {
        // Invariant 5 is unaffected by the unwrap: no line begins with ::: and the output is valid input to
        // Charter's own pipeline.
        foreach (var doc in new[] { FencedDiagramDoc, FencedDiffDoc, RawDiagramDoc, RawDiffDoc })
        {
            var output = HandoffMarkdown.Emit(doc);

            Assert.DoesNotMatch(@"(?m)^:::", output);
            Assert.Null(Record.Exception(() => CharterRenderer.Render(output)));
        }
    }

    [Fact]
    public void Emit_DiffOfAMarkdownFile_StillEscalatesTheFence_TheIndentedFencesAreContent()
    {
        // The unwrap must NOT swallow a diff's own content. A :::diff of a markdown file carries its ``` lines
        // as diff context/add lines — INDENTED or marker-prefixed, never a column-zero wrapper — so they stay
        // inside the emitted block and the opener escalates past them (the Charter #48/C2 fix is scoped to a
        // column-zero fence pair whose info string matches the container).
        const string doc =
            ":::diff\n" +
            " ```\n" +
            " unchanged code line\n" +
            " ```\n" +
            ":::";

        var output = HandoffMarkdown.Emit(doc);

        Assert.StartsWith("````diff\n", output.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains(" unchanged code line", output, StringComparison.Ordinal);

        var code = Assert.Single(BlockDocument.Parse(output).Blocks);
        Assert.Equal(BlockKind.Code, code.Kind);
    }

    [Fact]
    public void Emit_DiagramWhoseBodyFenceTagsAnotherLanguage_IsWrappedNotUnwrapped()
    {
        // A body fenced as something OTHER than the container's language is not the container's own wrapper —
        // unwrapping it would silently retag (and lose) the author's info string, so it is preserved verbatim
        // inside an escalated mermaid fence instead.
        const string doc =
            ":::diagram\n" +
            "```text\n" +
            "graph TD; A-->B;\n" +
            "```\n" +
            ":::";

        var output = HandoffMarkdown.Emit(doc);

        Assert.Contains("```text", output, StringComparison.Ordinal);
        Assert.StartsWith("````mermaid\n", output.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>The text BETWEEN the first line (the opening fence) and the last line (the closing fence).</summary>
    private static string InnerFenceContent(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        Assert.True(lines.Length >= 2, "a fenced handoff block must have at least an opener and a closer.");
        return string.Join("\n", lines[1..^1]);
    }
}
