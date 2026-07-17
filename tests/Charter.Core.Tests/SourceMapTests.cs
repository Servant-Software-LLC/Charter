using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Source-map tests: <see cref="SourceMap.Build(string)"/> maps a block's stable id to its markdown
/// line, and — the load-bearing proof — that anchor survives an edit to an unrelated block, which a
/// positional (line-number) selector never could.
/// </summary>
[Trait("Category", "CoreRenderer")]
public class SourceMapTests
{
    [Fact]
    public void SourceMap_MapsKnownBlockToItsMarkdownLine()
    {
        const string markdown =
            "# Title\n" +
            "\n" +
            "First paragraph.\n" +
            "\n" +
            "Second paragraph.";

        var blocks = BlockDocument.Parse(markdown).Blocks;
        var map = SourceMap.Build(markdown);

        // The last block ("Second paragraph.") must resolve to its actual source line.
        var secondParagraphId = blocks[^1].Id;
        Assert.Equal(LineOf(markdown, "Second paragraph."), map.LineForAnchor(secondParagraphId));
    }

    [Fact]
    public void Anchor_SurvivesUnrelatedBlockEdit()
    {
        const string before =
            "# Heading one\n" +
            "\n" +
            "Alpha paragraph.\n" +
            "\n" +
            "Gamma paragraph.";

        // Resolve Gamma's content-derived anchor from the original document.
        var gammaId = BlockDocument.Parse(before).Blocks
            .Single(b => b.RawContent.Contains("Gamma", StringComparison.Ordinal)).Id;
        var mapBefore = SourceMap.Build(before);
        Assert.Equal(LineOf(before, "Gamma paragraph."), mapBefore.LineForAnchor(gammaId));

        // Edit an UNRELATED block above Gamma: expand Alpha across two lines. Gamma's text is untouched,
        // so its content-derived id is unchanged — but its source line shifts down.
        const string after =
            "# Heading one\n" +
            "\n" +
            "Alpha paragraph, now expanded.\n" +
            "It now spans a second line.\n" +
            "\n" +
            "Gamma paragraph.";
        var mapAfter = SourceMap.Build(after);

        // The ORIGINAL anchor still resolves — and to the RIGHT block: Gamma at its new, shifted line. A
        // positional selector ("line 5") would now point at the blank line or the wrong block; the
        // content-derived anchor tracks Gamma through the unrelated edit above it.
        Assert.Equal(LineOf(after, "Gamma paragraph."), mapAfter.LineForAnchor(gammaId));

        // And it genuinely moved, proving the map re-resolved rather than returning a stale line.
        Assert.NotEqual(mapBefore.LineForAnchor(gammaId), mapAfter.LineForAnchor(gammaId));
    }

    /// <summary>The 1-based markdown line number of the first line containing <paramref name="needle"/>.</summary>
    private static int LineOf(string markdown, string needle)
        => Array.FindIndex(markdown.Split('\n'), line => line.Contains(needle, StringComparison.Ordinal)) + 1;
}
