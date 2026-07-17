using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using DefaultCodeBlockRenderer = Markdig.Renderers.Html.CodeBlockRenderer;

namespace Charter.Core;

/// <summary>
/// Renders Charter markdown to one portable HTML artifact, wrapping each block's element with its
/// content-derived stable <see cref="Block.Id"/> so a human annotation on the rendered HTML can be
/// round-tripped back to the markdown source via the <see cref="SourceMap"/>.
/// </summary>
public static class CharterRenderer
{
    /// <summary>
    /// Render <paramref name="markdown"/> to portable HTML. Each top-level block's root element carries an
    /// <c>id</c> attribute equal to that block's stable <see cref="Block.Id"/> — derived from the same raw
    /// content the block model uses, so the rendered id and <see cref="BlockDocument"/> always agree.
    /// Note/warn containers additionally carry a matching CSS class.
    /// </summary>
    public static string Render(string markdown)
    {
        markdown ??= string.Empty;
        var document = CharterMarkdown.ParseDocument(markdown);

        foreach (var node in document)
        {
            var (kind, rawContent) = CharterMarkdown.Describe(node, markdown);

            var attributes = node.GetAttributes();
            attributes.Id = Block.StableId(rawContent);
            if (kind == BlockKind.Note)
            {
                attributes.AddClass("note");
            }
            else if (kind == BlockKind.Warn)
            {
                attributes.AddClass("warn");
            }
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        CharterMarkdown.Pipeline.Setup(renderer);

        // The default renderer hangs the block attributes off <code>; Charter anchors the whole block, so
        // the stable id must land on the <pre> root while the language class stays on <code>.
        renderer.ObjectRenderers.Replace<DefaultCodeBlockRenderer>(new CharterCodeBlockRenderer());

        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }
}

/// <summary>
/// Renders a fenced/indented code block as <c>&lt;pre id="..."&gt;&lt;code class="language-..."&gt;</c> —
/// the block's stable id on the <c>&lt;pre&gt;</c> root (where every other block carries its anchor),
/// with the fence's info string as the <c>language-</c> class on <c>&lt;code&gt;</c>.
/// </summary>
internal sealed class CharterCodeBlockRenderer : HtmlObjectRenderer<CodeBlock>
{
    protected override void Write(HtmlRenderer renderer, CodeBlock obj)
    {
        renderer.EnsureLine();

        if (renderer.EnableHtmlForBlock)
        {
            // Only the stable id belongs on <pre>. Write it explicitly rather than via WriteAttributes,
            // because Markdig's code path also stashes the language as a class on the block's attributes —
            // and that class must stay on <code>, not leak onto the anchored <pre> root.
            renderer.Write("<pre");
            var id = obj.TryGetAttributes()?.Id;
            if (!string.IsNullOrEmpty(id))
            {
                renderer.Write(" id=\"");
                renderer.WriteEscape(id);
                renderer.Write('"');
            }

            renderer.Write("><code");
            if (obj is FencedCodeBlock fenced && !string.IsNullOrEmpty(fenced.Info))
            {
                renderer.Write(" class=\"language-");
                renderer.WriteEscape(fenced.Info);
                renderer.Write('"');
            }

            renderer.Write('>');
        }

        renderer.WriteLeafRawLines(obj, writeEndOfLines: true, escape: true);

        if (renderer.EnableHtmlForBlock)
        {
            renderer.WriteLine("</code></pre>");
        }
    }
}
