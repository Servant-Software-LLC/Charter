using Markdig.Extensions.CustomContainers;
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

        var hasDiagram = false;
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
            else if (kind == BlockKind.Diagram)
            {
                hasDiagram = true;
            }
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        CharterMarkdown.Pipeline.Setup(renderer);

        // The default renderer hangs the block attributes off <code>; Charter anchors the whole block, so
        // the stable id must land on the <pre> root while the language class stays on <code>.
        renderer.ObjectRenderers.Replace<DefaultCodeBlockRenderer>(new CharterCodeBlockRenderer());

        // A :::diagram container renders as <pre class="mermaid" id="..."> carrying the Mermaid source (the
        // diagram-node annotation anchor), not a callout <div>. Every other container (:::note, :::warn)
        // falls through to the default rendering this subclass delegates to.
        renderer.ObjectRenderers.Replace<HtmlCustomContainerRenderer>(new CharterDiagramContainerRenderer(markdown));

        renderer.Render(document);
        writer.Flush();

        var body = writer.ToString();

        // Offline portability (invariant 1): when the document contains at least one :::diagram, inline the
        // vendored Mermaid runtime + a theme-aware bootstrap ONCE. With no diagram, stay lean — inline nothing.
        return hasDiagram ? body + MermaidRuntimeMarkup() : body;
    }

    /// <summary>
    /// The inlined offline Mermaid runtime: the vendored library (<see cref="MermaidResource.Library"/>,
    /// which exposes <c>globalThis.mermaid</c>) followed by a theme-aware
    /// <c>mermaid.initialize({ startOnLoad: true, theme: … })</c> / <c>mermaid.run()</c> bootstrap — both as
    /// inline <c>&lt;script&gt;</c>, NEVER a CDN <c>src</c>. A saved <c>:::diagram</c> therefore renders with no
    /// network (invariant 1), and the only browser JS the renderer emits is the third-party library plus this
    /// minimal init call — Charter's own interaction JS stays in <c>sdk/</c> (invariant 6).
    /// </summary>
    private static string MermaidRuntimeMarkup()
    {
        // The vendored minified library carries exactly one incidental `class="note"` — a JavaScript property
        // assignment (`n.class="note"`) in Mermaid's note-shape code. Charter uses class="note" as its OWN
        // structural marker for note callouts, so the inlined third-party blob must not spuriously carry it.
        // Normalizing the insignificant whitespace around `=` is a semantics-neutral reformat of that single
        // assignment (its runtime value stays "note"); every other byte — including the __esbuild_esm_mermaid_nm
        // module marker the artifact needs — is inlined verbatim.
        var library = MermaidResource.Library.Replace("class=\"note\"", "class = \"note\"", StringComparison.Ordinal);

        const string bootstrap =
            "mermaid.initialize({ startOnLoad: true, " +
            "theme: window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'default' });\n" +
            "mermaid.run();";

        return "\n<script>" + library + "</script>\n<script>\n" + bootstrap + "\n</script>\n";
    }
}

/// <summary>
/// Renders a <c>:::diagram</c> container as
/// <c>&lt;pre class="mermaid" id="..."&gt;…mermaid source…&lt;/pre&gt;</c> — the block's stable id on the
/// <c>&lt;pre&gt;</c> root (where every other block carries its anchor, and the diagram-node annotation binds),
/// with the raw Mermaid source preserved as element text for the client library to render. Every other custom
/// container (<c>:::note</c>, <c>:::warn</c>) falls through to the default
/// <see cref="HtmlCustomContainerRenderer"/>.
/// </summary>
internal sealed class CharterDiagramContainerRenderer : HtmlCustomContainerRenderer
{
    private readonly string _markdown;

    public CharterDiagramContainerRenderer(string markdown) => _markdown = markdown ?? string.Empty;

    protected override void Write(HtmlRenderer renderer, CustomContainer obj)
    {
        if (!string.Equals(obj.Info?.Trim(), "diagram", StringComparison.OrdinalIgnoreCase))
        {
            base.Write(renderer, obj);
            return;
        }

        renderer.EnsureLine();

        if (renderer.EnableHtmlForBlock)
        {
            renderer.Write("<pre class=\"mermaid\"");
            var id = obj.TryGetAttributes()?.Id;
            if (!string.IsNullOrEmpty(id))
            {
                renderer.Write(" id=\"");
                renderer.WriteEscape(id);
                renderer.Write('"');
            }

            renderer.Write('>');
        }

        // Preserve the Mermaid source EXACTLY as authored rather than markdown-rendering it: the client library
        // reads the element's textContent, so any inline formatting would corrupt the graph. Slice the raw
        // source the inner blocks span straight from the markdown, HTML-escaped so it survives into the element.
        renderer.WriteEscape(DiagramSource(obj));

        if (renderer.EnableHtmlForBlock)
        {
            renderer.WriteLine("</pre>");
        }
    }

    private string DiagramSource(CustomContainer obj)
    {
        if (obj.Count == 0 || _markdown.Length == 0)
        {
            return string.Empty;
        }

        var start = obj[0].Span.Start;
        var end = obj[obj.Count - 1].Span.End;
        if (start < 0 || end < start)
        {
            return string.Empty;
        }

        start = Math.Clamp(start, 0, _markdown.Length - 1);
        end = Math.Clamp(end, start, _markdown.Length - 1);
        return _markdown.Substring(start, end - start + 1);
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
