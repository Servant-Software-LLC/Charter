using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Tests the YAML front-matter prerequisite slice (Architecture B §2.4). Charter's Markdig pipeline now runs
/// <c>UseYamlFrontMatter()</c> and strips the <c>YamlFrontMatterBlock</c> in the ONE shared parse
/// (<c>CharterMarkdown.ParseDocument</c>), so every seam that traverses the document — render, the
/// anchor/source-map pass, the handoff flattener, and export — skips the marker uniformly: it is never a
/// content block, never anchored, never rendered as prose, and never flattened into the handoff. The
/// resolver, by contrast, splices on the raw source string, so <see cref="QuestionResolution.Apply"/> must
/// PRESERVE the front matter untouched.
///
/// The strongest proof of transparent stripping is EQUIVALENCE: the same document with and without a leading
/// front-matter header must produce an identical block set and identical rendered HTML for its content — the
/// marker changes nothing downstream except its own physical line offset (which the source map correctly
/// accounts for so an anchor still resolves to the real source line).
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","FrontMatter")].
/// </summary>
[Trait("Category", "FrontMatter")]
public class FrontMatterTests
{
    // The content, authored WITHOUT a marker — the baseline every "with front matter" case is compared to.
    private const string Body =
        "# Living plan\n\n" +
        "Some prose introducing the plan.\n\n" +
        ":::diff\n+added line\n-removed line\n unchanged line\n:::\n\n" +
        ":::question\n" +
        "{ \"id\": \"q\", \"title\": \"Which datastore?\", \"mode\": \"single\", " +
        "\"options\": [\"Postgres\", \"DynamoDB\"], \"target\": \"human\" }\n" +
        ":::";

    // The same content with a plain-YAML format-version marker on top — the only difference.
    private const string WithFrontMatter =
        "---\ncharter-format-version: 1\n---\n" + Body;

    private const string Marker = "charter-format-version";

    [Fact]
    public void Parse_FrontMatter_IsNotAContentBlock_AndDoesNotShiftTheBlockSet()
    {
        var withBlocks = BlockDocument.Parse(WithFrontMatter).Blocks;
        var withoutBlocks = BlockDocument.Parse(Body).Blocks;

        // Same number of blocks, same kinds, same content-derived ids — the marker adds nothing and corrupts
        // nothing (without UseYamlFrontMatter a raw --- would parse as a thematic break and mangle block 1).
        Assert.Equal(withoutBlocks.Count, withBlocks.Count);
        Assert.Equal(withoutBlocks.Select(b => b.Kind), withBlocks.Select(b => b.Kind));
        Assert.Equal(withoutBlocks.Select(b => b.Id), withBlocks.Select(b => b.Id));

        // No block carries the marker text.
        Assert.DoesNotContain(withBlocks, b => b.RawContent.Contains(Marker, StringComparison.Ordinal));
    }

    [Fact]
    public void Render_FrontMatter_IsStripped_AndContentRendersIdentically()
    {
        var withHtml = CharterRenderer.Render(WithFrontMatter);
        var withoutHtml = CharterRenderer.Render(Body);

        // Transparent stripping: the rendered content is byte-identical, and the marker never reaches the HTML.
        Assert.Equal(withoutHtml, withHtml);
        Assert.DoesNotContain(Marker, withHtml);
    }

    [Fact]
    public void SourceMap_FrontMatter_SameAnchors_ButLinesShiftedByTheMarkerOffset()
    {
        var withMap = SourceMap.Build(WithFrontMatter);
        var withoutMap = SourceMap.Build(Body);

        // The anchor SET is identical (anchors are content-derived, so the marker adds none)...
        Assert.Equal(withoutMap.Anchors.OrderBy(a => a, StringComparer.Ordinal),
                     withMap.Anchors.OrderBy(a => a, StringComparer.Ordinal));

        // ...but each anchor resolves to its REAL source line, three lines lower with the marker present
        // (---, charter-format-version: 1, ---). This proves the map accounts for the marker's physical lines
        // while still not treating it as an anchor slot.
        var headingId = BlockDocument.Parse(Body).Blocks[0].Id;
        Assert.Equal(1, withoutMap.LineForAnchor(headingId));
        Assert.Equal(4, withMap.LineForAnchor(headingId));
    }

    [Fact]
    public void Handoff_FrontMatter_IsStripped_NotFlattenedIntoTheOutput()
    {
        var output = HandoffMarkdown.Emit(WithFrontMatter);

        Assert.DoesNotContain(Marker, output);
        // The content still flattens as usual.
        Assert.Contains("Living plan", output);
        Assert.Contains("```diff", output);
    }

    [Fact]
    public void Export_FrontMatter_IsStripped_FromTheExportedArtifact()
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-fm-export-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var artifact = ArtifactExporter.Export(WithFrontMatter, dir);

            Assert.DoesNotContain(Marker, artifact);
            Assert.Contains("Living plan", artifact);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Apply_PreservesFrontMatterUntouched_WhileResolvingTheQuestion()
    {
        var answers = new Dictionary<string, IReadOnlyList<string>> { ["q"] = new[] { "Postgres" } };

        var updated = QuestionResolution.Apply(WithFrontMatter, answers);

        // The marker block is preserved verbatim at the top (the resolver never strips it)...
        Assert.StartsWith("---\ncharter-format-version: 1\n---\n", updated);
        // ...and the question is resolved.
        Assert.Contains("\"answer\"", updated);
    }
}
