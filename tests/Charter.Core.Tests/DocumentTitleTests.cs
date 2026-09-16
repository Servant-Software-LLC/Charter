using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #253 — the document shell gained an OPTIONAL title for the served review page, and it must reach nowhere
/// else. <c>render</c> and <c>export</c> produce the portable artifact, which is promised byte-identical; a title
/// leaking into it would change every exported file for a feature that is about browser tabs.
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","DocumentTitle")].
/// </summary>
[Trait("Category", "DocumentTitle")]
public class DocumentTitleTests
{
    private const string Plan = "---\ncharter-format-version: 1\n---\n\n# A plan\n\nA paragraph.\n";

    [Fact]
    public void Render_carries_no_title()
    {
        Assert.DoesNotContain("<title>", CharterRenderer.Render(Plan), StringComparison.Ordinal);
    }

    [Fact]
    public void Export_carries_no_title()
    {
        var directory = Path.Combine(Path.GetTempPath(), "charter-title-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.DoesNotContain("<title>", ArtifactExporter.Export(Plan, directory), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Wrap_without_a_title_is_unchanged_from_the_two_argument_form()
    {
        Assert.Equal(CharterDocument.Wrap("<p>x</p>", cspMeta: null), CharterDocument.Wrap("<p>x</p>", null, title: null));
    }

    [Fact]
    public void Wrap_with_a_title_emits_it_encoded_inside_the_head()
    {
        var html = CharterDocument.Wrap("<p>x</p>", cspMeta: null, title: "r&d <draft>");

        Assert.Contains("<title>r&amp;d &lt;draft&gt;</title>", html, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("<title>", StringComparison.Ordinal) < html.IndexOf("</head>", StringComparison.Ordinal),
            "the title must sit inside the head");
    }
}
