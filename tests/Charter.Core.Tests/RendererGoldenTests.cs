using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Golden-per-block renderer tests: for each block kind, <see cref="CharterRenderer.Render(string)"/>
/// must emit the expected HTML fragment, and that fragment must carry the block's content-derived
/// stable id (asserted against <see cref="Block.Id"/>, never a hard-coded hash, so the test survives
/// any hash choice while still proving the renderer and block model agree on the anchor).
/// </summary>
[Trait("Category", "CoreRenderer")]
public class RendererGoldenTests
{
    [Fact]
    public void Render_Prose_WrapsParagraphWithStableId()
    {
        const string markdown = "Plain prose paragraph.";

        var block = BlockDocument.Parse(markdown).Blocks[0];
        var html = CharterRenderer.Render(markdown);

        Assert.Equal(BlockKind.Prose, block.Kind);
        Assert.Contains($"<p id=\"{block.Id}\">Plain prose paragraph.</p>", html);
    }

    [Fact]
    public void Render_Heading_WrapsHeadingWithStableId()
    {
        const string markdown = "## A section heading";

        var block = BlockDocument.Parse(markdown).Blocks[0];
        var html = CharterRenderer.Render(markdown);

        Assert.Equal(BlockKind.Heading, block.Kind);
        Assert.Contains($"<h2 id=\"{block.Id}\">A section heading</h2>", html);
    }

    [Fact]
    public void Render_List_WrapsListWithStableId()
    {
        const string markdown = "- first item\n- second item";

        var block = BlockDocument.Parse(markdown).Blocks[0];
        var html = CharterRenderer.Render(markdown);

        Assert.Equal(BlockKind.List, block.Kind);
        Assert.Contains($"<ul id=\"{block.Id}\">", html);
        Assert.Contains("<li>first item</li>", html);
        Assert.Contains("<li>second item</li>", html);
    }

    [Fact]
    public void Render_NoteCallout_WrapsDirectiveWithStableId()
    {
        const string markdown = ":::note\nThis is an important note.\n:::";

        var block = BlockDocument.Parse(markdown).Blocks[0];
        var html = CharterRenderer.Render(markdown);

        Assert.Equal(BlockKind.Note, block.Kind);
        Assert.Contains($"<div id=\"{block.Id}\" class=\"note\">", html);
        Assert.Contains("<p>This is an important note.</p>", html);
    }

    [Fact]
    public void Render_Table_WrapsTableWithStableId()
    {
        const string markdown =
            "| Name | Role |\n" +
            "| --- | --- |\n" +
            "| Ada | Author |";

        var block = BlockDocument.Parse(markdown).Blocks[0];
        var html = CharterRenderer.Render(markdown);

        Assert.Equal(BlockKind.Table, block.Kind);
        Assert.Contains($"<table id=\"{block.Id}\">", html);
        Assert.Contains("<th>Name</th>", html);
        Assert.Contains("<td>Ada</td>", html);
    }

    [Fact]
    public void Render_FencedCode_WrapsCodeWithStableId()
    {
        const string markdown =
            "```csharp\n" +
            "var answer = 42;\n" +
            "```";

        var block = BlockDocument.Parse(markdown).Blocks[0];
        var html = CharterRenderer.Render(markdown);

        Assert.Equal(BlockKind.Code, block.Kind);
        Assert.Contains($"<pre id=\"{block.Id}\">", html);
        Assert.Contains("<code class=\"language-csharp\">", html);
        Assert.Contains("var answer = 42;", html);
    }
}
