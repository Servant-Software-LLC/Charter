using System.Text.RegularExpressions;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Duplicate-content anchor disambiguation (hardening #3). Block/sub-element ids are content-derived, so
/// identical content recurring in one document used to ALIAS to a single id — an annotation on the 2nd
/// occurrence resolved (via <see cref="SourceMap"/>) to the 1st occurrence's source line, silently
/// misattributing feedback. The shared <c>AnchorAssignment</c> pass — consumed by BOTH the renderer and the
/// source map — discriminates the 2nd+ occurrence (<c>-2</c>, <c>-3</c>, …) while the FIRST keeps the pure
/// id (so unique-content documents are byte-identical to before).
///
/// The load-bearing test here is <see cref="RenderedAnchors_AndSourceMapKeys_AreIdentical_ForDuplicateDoc"/>:
/// it guards the exact risk the shared pass exists to prevent — the render and source-map paths deriving
/// discriminated ids INDEPENDENTLY and drifting, which would make anchors stop resolving entirely (worse
/// than the original aliasing).
/// </summary>
[Trait("Category", "CoreRenderer")]
public class DuplicateContentAnchorTests
{
    [Fact]
    public void TwoIdenticalProseBlocks_GetDistinctIds_AndSecondResolvesToSecondLine()
    {
        // line 1: Same paragraph.   (1st occurrence)
        // line 2: (blank)
        // line 3: Same paragraph.   (2nd occurrence)
        const string markdown =
            "Same paragraph.\n" +
            "\n" +
            "Same paragraph.";

        // The FIRST occurrence keeps the pure content-derived id; the SECOND is discriminated with -2.
        var firstId = Block.StableId("Same paragraph.");
        var secondId = firstId + "-2";
        Assert.NotEqual(firstId, secondId);

        var html = CharterRenderer.Render(markdown);
        Assert.Contains($"<p id=\"{firstId}\">Same paragraph.</p>", html);
        Assert.Contains($"<p id=\"{secondId}\">Same paragraph.</p>", html);

        // The fix: the 2nd occurrence's id resolves to the 2nd occurrence's SOURCE LINE (3), not the 1st's (1).
        var map = SourceMap.Build(markdown);
        Assert.Equal(1, map.LineForAnchor(firstId));
        Assert.Equal(3, map.LineForAnchor(secondId));
    }

    [Fact]
    public void DiffWithTwoIdenticalAddedLines_GetsDistinctSubAnchors_EachResolvingToItsOwnLine()
    {
        // line 1: :::diff
        // line 2: +same added line   (1st occurrence)
        // line 3: -a removed line
        // line 4: +same added line   (2nd occurrence)
        // line 5: :::
        const string markdown =
            ":::diff\n" +
            "+same added line\n" +
            "-a removed line\n" +
            "+same added line\n" +
            ":::";

        var firstAdd = Block.StableId("+same added line");
        var secondAdd = firstAdd + "-2";
        Assert.NotEqual(firstAdd, secondAdd);

        var html = CharterRenderer.Render(markdown);

        // Each diff line stamps its sub-anchor as both data-anchor and id; the two identical added lines are
        // now distinct anchors rather than one aliased anchor.
        Assert.Contains($"data-anchor=\"{firstAdd}\"", html);
        Assert.Contains($"data-anchor=\"{secondAdd}\"", html);
        Assert.Contains($"id=\"{firstAdd}\"", html);
        Assert.Contains($"id=\"{secondAdd}\"", html);

        var map = SourceMap.Build(markdown);
        Assert.Equal(2, map.LineForAnchor(firstAdd));
        Assert.Equal(4, map.LineForAnchor(secondAdd));
    }

    [Fact]
    public void ComparisonWithTwoIdenticalRows_GetsDistinctSubAnchors_EachResolvingToItsOwnLine()
    {
        // line 1: :::comparison
        // line 2: - Repeated row     (1st occurrence)
        // line 3: - A different row
        // line 4: - Repeated row     (2nd occurrence)
        // line 5: :::
        const string markdown =
            ":::comparison\n" +
            "- Repeated row\n" +
            "- A different row\n" +
            "- Repeated row\n" +
            ":::";

        // A row's sub-anchor derives from that row's own trimmed source line (marker included).
        var firstRow = Block.StableId("- Repeated row");
        var secondRow = firstRow + "-2";
        Assert.NotEqual(firstRow, secondRow);

        var html = CharterRenderer.Render(markdown);
        Assert.Contains($"data-anchor=\"{firstRow}\"", html);
        Assert.Contains($"data-anchor=\"{secondRow}\"", html);

        var map = SourceMap.Build(markdown);
        Assert.Equal(2, map.LineForAnchor(firstRow));
        Assert.Equal(4, map.LineForAnchor(secondRow));
    }

    /// <summary>
    /// ANTI-DRIFT (the regression guard for this change's risk): for a document full of duplicates, the SET
    /// of every id/data-anchor the renderer emits must EQUAL the set of anchors the source map resolves. If
    /// the two paths ever derived discrimination independently and drifted, a rendered anchor would have no
    /// source-map key (or vice versa) and this set equality would break — catching the failure that would
    /// make anchors stop resolving entirely.
    /// </summary>
    [Fact]
    public void RenderedAnchors_AndSourceMapKeys_AreIdentical_ForDuplicateDoc()
    {
        const string markdown =
            "Duplicated prose.\n" +
            "\n" +
            "Duplicated prose.\n" +
            "\n" +
            ":::comparison\n" +
            "- Repeated row\n" +
            "- Repeated row\n" +
            ":::\n" +
            "\n" +
            ":::diff\n" +
            "+repeated add\n" +
            "+repeated add\n" +
            ":::";

        var html = CharterRenderer.Render(markdown);
        var renderedAnchors = RenderedAnchors(html);
        var sourceMapKeys = SourceMap.Build(markdown).Anchors;

        // Sanity: discrimination actually happened, so this test exercises the risky path (not two empty sets).
        Assert.Contains(renderedAnchors, a => a.EndsWith("-2", StringComparison.Ordinal));

        // The two paths cannot silently diverge: every rendered anchor is resolvable, and every resolvable
        // anchor is rendered.
        Assert.True(renderedAnchors.SetEquals(sourceMapKeys),
            "Rendered anchors and source-map keys diverged.\n" +
            "rendered-only: " + string.Join(", ", renderedAnchors.Except(sourceMapKeys)) + "\n" +
            "sourcemap-only: " + string.Join(", ", sourceMapKeys.Except(renderedAnchors)));
    }

    [Fact]
    public void UniqueContentDocument_KeepsPureIds_NoDiscriminator()
    {
        // A document with NO duplicate content must be byte-identical to the pre-change output: every block
        // keeps the pure content-derived id (anchor-survival preserved), and nothing carries a -N suffix.
        const string markdown =
            "# A unique heading\n" +
            "\n" +
            "A unique paragraph.\n" +
            "\n" +
            "Another distinct paragraph.";

        var blocks = BlockDocument.Parse(markdown).Blocks;
        var html = CharterRenderer.Render(markdown);
        var map = SourceMap.Build(markdown);

        foreach (var block in blocks)
        {
            // Rendered id equals the pure StableId (no discriminator), and it resolves in the source map.
            Assert.Contains($"id=\"{block.Id}\"", html);
            Assert.NotNull(map.LineForAnchor(block.Id));
        }

        // No anchor in a duplicate-free document carries a discrimination suffix.
        Assert.DoesNotContain(RenderedAnchors(html), a => a.Contains("-2", StringComparison.Ordinal));
    }

    /// <summary>Every id/data-anchor value the rendered HTML carries, as a set. The negative lookbehind keeps
    /// <c>id="</c> from matching the tail of a longer attribute name (e.g. <c>data-question-id="</c>).</summary>
    private static HashSet<string> RenderedAnchors(string html)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(html, "(?<![-\\w])id=\"([^\"]+)\""))
        {
            anchors.Add(m.Groups[1].Value);
        }

        foreach (Match m in Regex.Matches(html, "data-anchor=\"([^\"]+)\""))
        {
            anchors.Add(m.Groups[1].Value);
        }

        return anchors;
    }
}
