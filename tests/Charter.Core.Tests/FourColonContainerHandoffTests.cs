using System.Text.RegularExpressions;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #190 — a container opened with FOUR colons must flatten exactly as its three-colon twin does.
///
/// <para><c>HandoffMarkdown.InnerLines</c> recognised the fence pair with two regexes, <c>^:::\w+</c> and
/// <c>^:::\s*$</c>. Neither matches a <c>::::</c> line, so BOTH fence lines survived into the flattened body.
/// A four-colon fence is legal CommonMark-directive syntax and is exactly what <c>charter-format</c> tells an
/// author to reach for when a body line would otherwise open a nested directive — so the defect fires on the
/// NESTING case, which is when it is least likely to be noticed.</para>
///
/// <para><b>"Invariant 5 held anyway" is only true for two of the six containers.</b> A note/warn flattens to
/// a blockquote, so its leaked <c>::::</c> rides behind a <c>&gt;</c> and cannot be read as a directive
/// downstream — luck, not design, and #190 says not to rely on it. A <c>::::comparison</c> and a
/// <c>::::custom-html</c> emit their inner lines VERBATIM at column zero, so the leak was a live directive
/// line in the plain-CommonMark handoff; and a <c>::::diff</c>'s leaked opener made
/// <c>TryUnwrapOwnFence</c> fail, so the documented <c>::::diff</c> + <c>```diff</c> nesting form came out
/// double-fenced with its own container fence as diff content.</para>
///
/// <para>This is the same widening #172 already made for <c>::::question</c> (where the unreadable body cost
/// the handoff the question's id, title and target). Both readers now share ONE fence vocabulary —
/// <see cref="BlockDocument"/>'s <c>DirectiveFence</c> — so they cannot drift again.</para>
/// </summary>
[Trait("Category", "HandoffFourColonFence")]
public class FourColonContainerHandoffTests
{
    /// <summary>
    /// Invariant 5's line-anchored proxy, the same one <c>HandoffMarkdownTests</c> uses: no line of a
    /// flattened plan may (re)open a directive. Deliberately NOT <c>Contains(":::")</c>, which a plan
    /// TALKING about directive syntax makes a false positive.
    /// </summary>
    private const string LineStartDirective = @"(?m)^:::";

    private const string FourColonNote =
        "::::note\n" +
        "An important note.\n" +
        "::::";

    private const string FourColonWarn =
        "::::warn\n" +
        "A serious warning.\n" +
        "::::";

    private const string FourColonComparison =
        "::::comparison\n" +
        "- **Postgres** — the option we know\n" +
        "- **DynamoDB** — the option we do not\n" +
        "::::";

    private const string FourColonCustomHtml =
        "::::custom-html\n" +
        "<p id=\"verbatim\">markup the author opted into</p>\n" +
        "::::";

    /// <summary>
    /// The form <c>charter-format</c> documents for a diff OF a Charter plan: the container is widened to
    /// <c>::::</c> precisely because a body line reads <c>:::note</c> and would otherwise open a nested
    /// directive. It is the highest-value instance of #190 — the one an author is TOLD to write.
    /// </summary>
    private const string FourColonDiff =
        "::::diff\n" +
        "```diff\n" +
        " :::note\n" +
        "-  a line inside a .charter.md being diffed\n" +
        "+  the block survives because the container is four colons\n" +
        "```\n" +
        "::::";

    /// <summary>A genuinely NESTED container — the case the four-colon fence exists for.</summary>
    private const string NestedContainer =
        "::::note\n" +
        "A callout that carries a diagram.\n" +
        "\n" +
        ":::diagram\n" +
        "graph TD; Alpha-->Beta;\n" +
        ":::\n" +
        "::::";

    public static TheoryData<string> EveryFourColonContainer() => new()
    {
        FourColonNote,
        FourColonWarn,
        FourColonComparison,
        FourColonCustomHtml,
        FourColonDiff,
        NestedContainer,
    };

    [Fact]
    public void Emit_FourColonNote_FlattensToTheSameLabeledBlockquoteAsItsThreeColonTwin()
    {
        var output = HandoffOutput.WithoutStamp(HandoffMarkdown.Emit(FourColonNote));

        Assert.Equal("> **Note:** An important note.", output);
        Assert.Equal(
            HandoffOutput.WithoutStamp(HandoffMarkdown.Emit(FourColonNote.Replace("::::", ":::",
                System.StringComparison.Ordinal))),
            output);
    }

    [Fact]
    public void Emit_FourColonWarn_FlattensToTheSameLabeledBlockquoteAsItsThreeColonTwin()
    {
        var output = HandoffOutput.WithoutStamp(HandoffMarkdown.Emit(FourColonWarn));

        Assert.Equal("> **Warning:** A serious warning.", output);
        Assert.Equal(
            HandoffOutput.WithoutStamp(HandoffMarkdown.Emit(FourColonWarn.Replace("::::", ":::",
                System.StringComparison.Ordinal))),
            output);
    }

    [Fact]
    public void Emit_FourColonComparison_LeavesNoDirectiveLineAtColumnZero()
    {
        // A comparison emits its inner lines VERBATIM, so the leaked fences were not behind a blockquote:
        // the flattened plan carried a live "::::comparison" directive line. Invariant 5, breached outright.
        var output = HandoffMarkdown.Emit(FourColonComparison);

        Assert.DoesNotMatch(LineStartDirective, output);
        Assert.Equal(
            "- **Postgres** — the option we know\n- **DynamoDB** — the option we do not",
            HandoffOutput.WithoutStamp(output));
    }

    [Fact]
    public void Emit_FourColonCustomHtml_PassesTheBodyThroughWithNoFenceLeak()
    {
        var output = HandoffMarkdown.Emit(FourColonCustomHtml);

        Assert.DoesNotMatch(LineStartDirective, output);
        Assert.Equal("<p id=\"verbatim\">markup the author opted into</p>", HandoffOutput.WithoutStamp(output));
    }

    [Fact]
    public void Emit_FourColonDiff_StillUnwrapsItsOwnFence_TheDocumentedNestingForm()
    {
        // The leaked "::::diff" opener sat where TryUnwrapOwnFence looks for the body's own ```diff fence, so
        // the unwrap failed and the whole container — its own fence lines included — was re-wrapped as diff
        // CONTENT inside an escalated ````diff block. Charter #48/C2's double-fence, via #190.
        var output = HandoffMarkdown.Emit(FourColonDiff);

        Assert.DoesNotMatch(LineStartDirective, output);
        Assert.Single(Regex.Matches(output, "(?m)^`+diff$"));
        Assert.StartsWith("```diff\n", HandoffOutput.WithoutStamp(output), System.StringComparison.Ordinal);
        Assert.DoesNotContain("::::", output, System.StringComparison.Ordinal);

        var code = Assert.Single(HandoffOutput.ContentBlocks(output));
        Assert.Equal(BlockKind.Code, code.Kind);
    }

    [Fact]
    public void Emit_NestedContainer_StripsTheOuterFencePair()
    {
        var output = HandoffOutput.WithoutStamp(HandoffMarkdown.Emit(NestedContainer));

        // The OUTER container is the one this flatten owns, and its fence pair is gone: the note's label
        // lands on the first body line and nothing carries a four-colon run any more.
        Assert.StartsWith("> **Note:** A callout that carries a diagram.", output, System.StringComparison.Ordinal);
        Assert.DoesNotContain("::::", output, System.StringComparison.Ordinal);
        Assert.DoesNotMatch(LineStartDirective, output);
    }

    [Theory]
    [MemberData(nameof(EveryFourColonContainer))]
    public void Emit_EveryFourColonContainer_LeaksNoFenceLineAndSelfParses(string doc)
    {
        var output = HandoffMarkdown.Emit(doc);

        // No line reopens a directive (invariant 5's proxy), no four-colon run survives anywhere, and the
        // emitted markdown is still well-formed input to Charter's own pipeline.
        Assert.DoesNotMatch(LineStartDirective, output);
        Assert.DoesNotContain("::::", output, System.StringComparison.Ordinal);
        Assert.Null(Record.Exception(() => CharterRenderer.Render(output)));
        Assert.NotEmpty(BlockDocument.Parse(output).Blocks);
    }
}
