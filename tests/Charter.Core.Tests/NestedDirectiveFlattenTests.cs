using System;
using System.Collections.Generic;
using Charter.Core;
using Markdig;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #203 step 2: what a nested <c>:::</c> directive's body actually BECOMES for the machine that
/// consumes <c>charter handoff</c>'s output — and therefore which tier <see cref="NestedDirectiveLint"/> puts
/// each kind in.
/// </summary>
/// <remarks>
/// <para>
/// <b>These tiers were asserted from mechanism before they were read.</b> This file reads them. The block model
/// is top-level-only, so a container nested inside a <c>::::note</c> is flattened as part of the note — which
/// means it arrives at the consumer as <b>blockquoted CommonMark prose</b>, its <c>:::</c> fence lines and all.
/// The one question that decides a kind's tier is therefore: <i>does this body survive being blockquoted as
/// CommonMark prose?</i>
/// </para>
/// <para>
/// <b>The consumer is a PLAIN CommonMark reader</b> — GitHub, or Guardrails' <c>plan-breakdown</c> — never
/// Charter's own pipeline. Re-reading the flatten with <see cref="CharterMarkdown"/>'s container-aware pipeline
/// would answer a different question entirely and answer it far too kindly: it re-parses the leaked
/// <c>:::question</c> back into a live <c>&lt;form&gt;</c>. Invariant 5 says the flattened path is plain
/// CommonMark, so that is what these tests parse it as.
/// </para>
/// </remarks>
public class NestedDirectiveFlattenTests
{
    // Plain CommonMark plus pipe tables: what a downstream reader has. NO custom containers, deliberately.
    private static readonly MarkdownPipeline PlainCommonMark =
        new MarkdownPipelineBuilder().UsePipeTables().Build();

    private static string PlanNesting(string inner) =>
        "# A plan\n\nProse before.\n\n::::note\nA callout that contains it.\n\n" + inner + "\n::::\n\nProse after.\n";

    private static string FlattenedAsHtml(string inner)
        => Markdown.ToHtml(HandoffMarkdown.Emit(PlanNesting(inner)), PlainCommonMark);

    /// <summary>
    /// A nested <c>:::question</c>'s JSON body arrives as PROSE. Nothing downstream reads JSON out of a
    /// paragraph, so the decision is simply absent from the handoff — while <c>needsHuman</c> says nobody is
    /// needed. Hence the record's <c>nested-question</c> note AND the gate blocker.
    /// </summary>
    [Fact]
    public void ANestedQuestion_ArrivesAsRawJsonProse_NotADecision()
    {
        var html = FlattenedAsHtml(
            ":::question\n{\"id\":\"q-nested\",\"title\":\"Which store?\",\"mode\":\"single\","
            + "\"target\":\"human\",\"options\":[\"Postgres\",\"DynamoDB\"]}\n:::");

        // The literal JSON body is emitted as TEXT — the very thing HandoffMarkdown.EmitQuestion promises
        // NEVER happens, because EmitQuestion is not reached for a block the model cannot see. (It arrives
        // HTML-escaped, which is exactly the point: it is prose now, not a declaration.)
        Assert.Contains("&quot;id&quot;:&quot;q-nested&quot;", html, StringComparison.Ordinal);

        // And none of the markers a consumer would use to recognise a question is present.
        Assert.DoesNotContain("_Question — id", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Open question (unresolved)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Delegated decision", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A nested <c>:::diff</c> is the tier that had to be READ rather than assumed, and reading it confirms the
    /// worst reading: line-initial <c>+</c> and <c>-</c> inside a blockquote are CommonMark BULLET MARKERS, so
    /// an added line and a removed line become the same thing — two <c>&lt;li&gt;</c>s with the marker eaten.
    /// A reader is shown <c>REQUIRE_MFA = true</c> as an item of a list when the plan said DELETE it.
    /// </summary>
    [Fact]
    public void ANestedDiff_LosesItsAddDelMarkers_ToBulletParsing()
    {
        var html = FlattenedAsHtml(":::diff\n- REQUIRE_MFA = true\n+ REQUIRE_MFA = false\n  UNCHANGED = 1\n:::");

        // The removed line is parsed as a list item: its `-` marker is gone from the text entirely.
        Assert.Contains("<li>", html, StringComparison.Ordinal);
        Assert.Contains("REQUIRE_MFA = true", html, StringComparison.Ordinal);
        Assert.DoesNotContain("- REQUIRE_MFA = true", html, StringComparison.Ordinal);

        // Which is the whole defect: nothing in the output distinguishes the removed line from the added one.
        Assert.DoesNotContain("+ REQUIRE_MFA = false", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Read, not assumed:</b> a nested <c>:::comparison</c>'s body is a CommonMark list, and a list survives
    /// blockquoting intact — every option, its emphasis and its text. It loses the <c>comparison</c> framing
    /// (and its per-row anchors), which is a presentation loss, not a corruption. Warning tier confirmed.
    /// </summary>
    [Fact]
    public void ANestedComparison_SurvivesBlockquotingIntact_SoItIsAWarningOnly()
    {
        var html = FlattenedAsHtml(
            ":::comparison\n- **Postgres** — mature, boring, ours\n- **DynamoDB** — scales, costs\n:::");

        Assert.Contains("<strong>Postgres</strong>", html, StringComparison.Ordinal);
        Assert.Contains("mature, boring, ours", html, StringComparison.Ordinal);
        Assert.Contains("<strong>DynamoDB</strong>", html, StringComparison.Ordinal);
        Assert.Contains("scales, costs", html, StringComparison.Ordinal);

        // Two list items, both readable: no option is dropped, reordered or merged into another.
        Assert.Equal(2, CountOf(html, "<li>"));
    }

    /// <summary>
    /// <b>Read, not assumed:</b> a nested <c>:::diagram</c>'s Mermaid source is already inside a fenced code
    /// block, and a fence survives blockquoting — the source arrives verbatim, unrendered. A reader gets the
    /// graph as text instead of a picture, which is a presentation loss. Warning tier confirmed.
    /// </summary>
    [Fact]
    public void ANestedDiagram_KeepsItsMermaidSourceVerbatim_SoItIsAWarningOnly()
    {
        var html = FlattenedAsHtml(":::diagram\n```mermaid\ngraph TD;\n  A-->B;\n```\n:::");

        Assert.Contains("<code", html, StringComparison.Ordinal);
        Assert.Contains("graph TD;", html, StringComparison.Ordinal);
        Assert.Contains("A--&gt;B;", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Read, not assumed:</b> a nested <c>:::note</c>/<c>:::warn</c> is prose, and prose survives
    /// blockquoting with its inline formatting and links intact. Warning tier confirmed.
    /// </summary>
    [Theory]
    [InlineData("note")]
    [InlineData("warn")]
    public void ANestedCallout_KeepsItsProseAndInlineFormatting_SoItIsAWarningOnly(string kind)
    {
        var html = FlattenedAsHtml($":::{kind}\nAn inner callout with **emphasis** and a [link](http://x).\n:::");

        Assert.Contains("<strong>emphasis</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"http://x\">link</a>", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unknown nested directive is unknowable by definition — and the reason it BLOCKS rather than warns is
    /// that a misspelled <c>:::questoin</c> classifies as one, so a hidden <c>target: human</c> decision would
    /// otherwise exit 0. Same reasoning <see cref="HandoffGate"/> already applies at the top level.
    /// </summary>
    [Fact]
    public void ANestedUnknownDirective_CanHideAHumanDecision()
    {
        var html = FlattenedAsHtml(
            ":::questoin\n{\"id\":\"typo\",\"title\":\"Hidden?\",\"mode\":\"single\","
            + "\"target\":\"human\",\"options\":[\"A\",\"B\"]}\n:::");

        Assert.Contains("&quot;target&quot;:&quot;human&quot;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("_Question — id", html, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
