using System.Text.RegularExpressions;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #208 — a <c>:::diff</c> that is not a DIRECT CHILD of the document must RENDER, without anchors.
///
/// <para><c>WriteDiff</c> read every line's sub-anchor out of <c>AnchorAssignment</c>, whose slot walk is
/// <c>foreach (var node in document)</c> — top-level only — but it is called wherever Markdig parsed the
/// container, including inside a <c>::::note</c>, a list item or a blockquote. A nested diff's lines were
/// never registered, so <c>SubIdForLine</c> threw and <c>charter render</c> exited 1 with
/// <i>"The given key '14' was not present in the dictionary"</i> — right after #203's warning had correctly
/// named the block, so the author was told the cause and then denied the render that would let them fix
/// it.</para>
///
/// <para><b>Why the anchors go and the render stays.</b> Dropping them is #166 applied one level down: a
/// nested block carries no anchor of its own, and a note on one resolves OUTWARD to the enclosing block. The
/// alternative — refusing the plan — was rejected because the shape is already refused where refusing helps
/// (#203's strict-handoff gate blocks a nested <c>:::diff</c>, its <c>+</c>/<c>-</c> lines being eaten as
/// bullet markers in the flatten), and <c>render</c>/<c>review</c> are how an author READS the plan they have
/// to fix.</para>
///
/// <para><b>Every positive here is PAIRED with the top-level control that shares its diff body.</b> The
/// anchor a line would carry is computed from the recipe (<c>Block.StableId</c> of the trimmed line) and
/// asserted PRESENT in the top-level render and ABSENT in the nested one — so neither half can pass
/// vacuously: a fix that emitted no diff at all would fail the readability assertions, and a test that merely
/// looked for "no id" would pass against a renderer that stopped anchoring diffs entirely.</para>
/// </summary>
[Trait("Category", "NestedDiffAnchor")]
public class NestedDiffAnchorTests
{
    /// <summary>The three diff lines every fixture shares, so one computed anchor answers for all of them.</summary>
    private const string RemovedLine = "-const timeout = 30;";
    private const string AddedLine = "+const timeout = 120;";
    private const string ContextLine = " const retries = 3;";

    /// <summary>
    /// The context line's rendered form, with its leading space OPTIONAL. Markdig dedents a fenced block inside
    /// a list item by the block's minimum indentation, which eats that space — identically for a TOP-LEVEL
    /// <c>:::diff</c> in a list item, so it is Markdig's behaviour and not this fix's. The <c>+</c>/<c>-</c>
    /// lines carry no leading space and are asserted exactly.
    /// </summary>
    private const string ContextLinePattern = "<div class=\"diff-line diff-context\"> ?const retries = 3;</div>";

    /// <summary>The control: the identical diff, at top level, where it is a Block and carries real anchors.</summary>
    private const string TopLevelDiff =
        "# A plan that shows a change\n" +
        "\n" +
        "Prose before the diff.\n" +
        "\n" +
        ":::diff\n" +
        "```diff\n" +
        RemovedLine + "\n" +
        AddedLine + "\n" +
        ContextLine + "\n" +
        "```\n" +
        ":::\n";

    /// <summary>The issue's own shape: the diff one level down, inside the four-colon callout.</summary>
    private const string DiffInsideNote =
        "# A note that shows a change\n" +
        "\n" +
        "Prose before the callout.\n" +
        "\n" +
        "::::note\n" +
        "The change we are proposing:\n" +
        "\n" +
        ":::diff\n" +
        "```diff\n" +
        RemovedLine + "\n" +
        AddedLine + "\n" +
        ContextLine + "\n" +
        "```\n" +
        ":::\n" +
        "::::\n";

    /// <summary>The second nesting the renderer descends through: indented under a list item.</summary>
    private const string DiffInsideListItem =
        "# A step that shows a change\n" +
        "\n" +
        "- a plain first step\n" +
        "- a step that shows a change\n" +
        "\n" +
        "  :::diff\n" +
        "  ```diff\n" +
        "  " + RemovedLine + "\n" +
        "  " + AddedLine + "\n" +
        "  " + ContextLine + "\n" +
        "  ```\n" +
        "  :::\n";

    /// <summary>The third: inside a blockquote, which #203 verified renders live and behaves identically.</summary>
    private const string DiffInsideBlockquote =
        "# A quote that shows a change\n" +
        "\n" +
        "> Quoted context for the change.\n" +
        ">\n" +
        "> :::diff\n" +
        "> ```diff\n" +
        "> " + RemovedLine + "\n" +
        "> " + AddedLine + "\n" +
        "> " + ContextLine + "\n" +
        "> ```\n" +
        "> :::\n";

    public static TheoryData<string> LiveNestings() => new()
    {
        DiffInsideNote,
        DiffInsideListItem,
        DiffInsideBlockquote,
    };

    /// <summary>
    /// The crash, stated as the renderer contract it broke: rendering is TOTAL. Before the fix every one of
    /// these threw <see cref="KeyNotFoundException"/> out of <c>AnchorAssignment.SubIdForLine</c>, which the
    /// CLI surfaced as exit 1.
    /// </summary>
    [Theory]
    [MemberData(nameof(LiveNestings))]
    public void Render_NestedDiff_DoesNotThrow(string plan)
    {
        var body = CharterRenderer.RenderBody(plan);

        Assert.Contains("<div class=\"diff\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// READABLE, which is the whole reason the render is kept rather than refused: every line is present, and
    /// the add/del/context class — the only thing that tells an added line from a removed one once the markers
    /// are just text — is written whether or not the block is anchored.
    /// </summary>
    [Theory]
    [MemberData(nameof(LiveNestings))]
    public void Render_NestedDiff_KeepsEveryLineAndItsAddDelClass(string plan)
    {
        var body = CharterRenderer.RenderBody(plan);

        Assert.Contains("<div class=\"diff-line diff-del\">" + RemovedLine, body, StringComparison.Ordinal);
        Assert.Contains("<div class=\"diff-line diff-add\">" + AddedLine, body, StringComparison.Ordinal);
        Assert.Matches(ContextLinePattern, body);

        // The scroll region survives too — a diff a reviewer cannot read to the end is the #87 defect, and it
        // is anchor-invisible by construction, so nothing about #166 argues for removing it.
        Assert.Contains("class=\"diff-scroll\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The anchor half, PAIRED with its control. The sub-anchor a diff line carries is
    /// <c>Block.StableId</c> of the line's trimmed text, so the id is computable here without reaching into
    /// the assignment — asserted present at top level and absent one level down.
    /// </summary>
    [Theory]
    [MemberData(nameof(LiveNestings))]
    public void Render_NestedDiff_CarriesNoSubAnchors_WhereTheTopLevelTwinCarriesThem(string plan)
    {
        var addedAnchor = Block.StableId(AddedLine);
        var removedAnchor = Block.StableId(RemovedLine);

        // The control first: this is what a diff line looks like when it IS a block's sub-element. Without it,
        // "no id in the nested render" would also pass against a renderer that had stopped anchoring diffs.
        var control = CharterRenderer.RenderBody(TopLevelDiff);
        Assert.Contains("id=\"" + addedAnchor + "\"", control, StringComparison.Ordinal);
        Assert.Contains("data-anchor=\"" + removedAnchor + "\"", control, StringComparison.Ordinal);

        var body = CharterRenderer.RenderBody(plan);
        Assert.DoesNotContain(addedAnchor, body, StringComparison.Ordinal);
        Assert.DoesNotContain(removedAnchor, body, StringComparison.Ordinal);

        // Stated structurally as well as by id, so a future change to the id recipe cannot quietly make the
        // assertion above vacuous: not one .diff-line opening tag in a nested diff carries an attribute.
        foreach (Match tag in Regex.Matches(body, "<div class=\"diff-line [^>]*>"))
        {
            Assert.Matches("^<div class=\"diff-line diff-(add|del|context)\">$", tag.Value);
        }
    }

    /// <summary>
    /// The anchor MODEL is undisturbed — the #184 pin. <c>SourceMap.Anchors</c> is exactly the top-level block
    /// ids: the nested diff contributes no block anchor and no line sub-anchor, so nothing new became
    /// resolvable and no existing anchor moved.
    /// </summary>
    [Fact]
    public void Render_NestedDiff_LeavesTheAnchorSetExactlyTheTopLevelBlocks()
    {
        var topLevelIds = BlockDocument.Parse(DiffInsideNote).Blocks.Select(block => block.Id).ToHashSet();

        Assert.True(
            SourceMap.Build(DiffInsideNote).Anchors.SetEquals(topLevelIds),
            "the anchor set must still be exactly the top-level blocks — the nested diff gains none.");

        // …and the enclosing callout keeps the anchor a note on the nested diff resolves outward TO (#166).
        var note = BlockDocument.Parse(DiffInsideNote).Blocks.Single(block => block.Kind == BlockKind.Note);
        Assert.Contains("id=\"" + note.Id + "\" class=\"note\"", CharterRenderer.RenderBody(DiffInsideNote), StringComparison.Ordinal);
    }

    /// <summary>
    /// The saved artifact carries the diff readably too — invariant 1. A nested diff that renders in the
    /// review page and crashes (or vanishes) on export would be the #184 shape all over again.
    /// </summary>
    [Theory]
    [MemberData(nameof(LiveNestings))]
    public void Export_NestedDiff_SavedArtifactCarriesTheDiffReadably(string plan)
    {
        var artifact = ArtifactExporter.Export(plan, Path.GetTempPath());

        Assert.Contains("<div class=\"diff-line diff-del\">" + RemovedLine, artifact, StringComparison.Ordinal);
        Assert.Contains("<div class=\"diff-line diff-add\">" + AddedLine, artifact, StringComparison.Ordinal);
        Assert.Matches(ContextLinePattern, artifact);

        // Still no anchors, and still no serve-time scaffolding rode in behind the fix.
        Assert.DoesNotContain(Block.StableId(AddedLine), artifact, StringComparison.Ordinal);
        Assert.DoesNotContain("data-charter-sdk", artifact, StringComparison.Ordinal);
    }

    /// <summary>
    /// The top-level path is UNCHANGED — the fix touches only the branch that had no assignment to read.
    /// </summary>
    [Fact]
    public void Render_TopLevelDiff_StillCarriesItsPerLineAnchorsAndSourceMapEntries()
    {
        var body = CharterRenderer.RenderBody(TopLevelDiff);
        var map = SourceMap.Build(TopLevelDiff);

        foreach (var line in new[] { RemovedLine, AddedLine, ContextLine })
        {
            var anchor = Block.StableId(line.Trim());
            Assert.Contains("data-anchor=\"" + anchor + "\" id=\"" + anchor + "\">", body, StringComparison.Ordinal);
            Assert.True(map.Anchors.Contains(anchor), $"the top-level diff line '{line}' must stay in the source map.");
        }
    }
}
