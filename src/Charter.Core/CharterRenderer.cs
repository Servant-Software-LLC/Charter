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
            else if (kind == BlockKind.Comparison)
            {
                attributes.AddClass("comparison");

                // Sub-block anchor model: a :::comparison stamps each row with its OWN content-derived
                // sub-anchor, so the default list renderer emits
                // <li data-anchor="{Block.StableId(row source line)}">. Distinct rows get distinct
                // sub-anchors, each derived from that row's own content — so one row's annotation survives
                // edits to the others (invariant 2). A :::diff carries its per-line sub-anchors through its
                // own custom renderer (CharterContainerRenderer) rather than this list-item stamping.
                foreach (var (row, subAnchor, _) in CharterMarkdown.SubAnchors(node, markdown))
                {
                    row.GetAttributes().AddProperty("data-anchor", subAnchor);
                }
            }
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        CharterMarkdown.Pipeline.Setup(renderer);

        // The default renderer hangs the block attributes off <code>; Charter anchors the whole block, so
        // the stable id must land on the <pre> root while the language class stays on <code>.
        renderer.ObjectRenderers.Replace<DefaultCodeBlockRenderer>(new CharterCodeBlockRenderer());

        // The containers whose markup diverges from the default callout <div> get a custom renderer: a
        // :::diagram renders as <pre class="mermaid" id="..."> (the Mermaid source / diagram-node anchor),
        // and a :::diff as a <div class="diff"> of per-line <div>s each carrying its own sub-anchor and
        // add/del class. Every other container (:::note, :::warn, :::comparison) falls through to the
        // default rendering this subclass delegates to.
        renderer.ObjectRenderers.Replace<HtmlCustomContainerRenderer>(new CharterContainerRenderer(markdown));

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
/// Renders the Charter custom containers whose markup diverges from the default callout <c>&lt;div&gt;</c>:
/// a <c>:::diagram</c> as <c>&lt;pre class="mermaid" id="..."&gt;…mermaid source…&lt;/pre&gt;</c> (the block's
/// stable id on the <c>&lt;pre&gt;</c> root where the diagram-node annotation binds, with the raw Mermaid
/// source preserved as element text for the client library), a <c>:::diff</c> as a
/// <c>&lt;div class="diff" id="..."&gt;</c> whose every diff LINE is its own
/// <c>&lt;div class="diff-line diff-add|diff-del|diff-context" data-anchor="..." id="..."&gt;</c> (the
/// per-line sub-anchor a reviewer's note binds to), and a <c>:::question</c> as a native HTML
/// <c>&lt;form id="..." data-question-id="..."&gt;</c> whose controls match the parsed
/// <see cref="QuestionSpec"/>'s mode (radios for single, checkboxes for multi, a textarea for free-text, a
/// number input for number, a checkbox for bool) — plain native HTML that needs no Charter JS to display.
/// Every other container (<c>:::note</c>, <c>:::warn</c>, <c>:::comparison</c>) falls through to the default
/// <see cref="HtmlCustomContainerRenderer"/>.
/// </summary>
internal sealed class CharterContainerRenderer : HtmlCustomContainerRenderer
{
    private readonly string _markdown;

    public CharterContainerRenderer(string markdown) => _markdown = markdown ?? string.Empty;

    protected override void Write(HtmlRenderer renderer, CustomContainer obj)
    {
        var info = obj.Info?.Trim();
        if (string.Equals(info, "diagram", StringComparison.OrdinalIgnoreCase))
        {
            WriteDiagram(renderer, obj);
        }
        else if (string.Equals(info, "diff", StringComparison.OrdinalIgnoreCase))
        {
            WriteDiff(renderer, obj);
        }
        else if (string.Equals(info, "question", StringComparison.OrdinalIgnoreCase))
        {
            WriteQuestion(renderer, obj);
        }
        else
        {
            base.Write(renderer, obj);
        }
    }

    private void WriteDiagram(HtmlRenderer renderer, CustomContainer obj)
    {
        renderer.EnsureLine();

        if (renderer.EnableHtmlForBlock)
        {
            renderer.Write("<pre class=\"mermaid\"");
            WriteId(renderer, obj.TryGetAttributes()?.Id);
            renderer.Write('>');
        }

        // Preserve the Mermaid source EXACTLY as authored rather than markdown-rendering it: the client library
        // reads the element's textContent, so any inline formatting would corrupt the graph. Slice the raw
        // source the inner blocks span straight from the markdown, HTML-escaped so it survives into the element.
        renderer.WriteEscape(ContainerBody(obj));

        if (renderer.EnableHtmlForBlock)
        {
            renderer.WriteLine("</pre>");
        }
    }

    private void WriteDiff(HtmlRenderer renderer, CustomContainer obj)
    {
        renderer.EnsureLine();

        if (renderer.EnableHtmlForBlock)
        {
            renderer.Write("<div class=\"diff\"");
            WriteId(renderer, obj.TryGetAttributes()?.Id);
            renderer.WriteLine(">");
        }

        // Each diff LINE carries its OWN content-derived sub-anchor (Block.StableId of the line's trimmed
        // text, marker included) — the same anchor SourceMap.Build registers via CharterMarkdown.DiffLines,
        // so a note on one line round-trips to that line and survives edits to the others (invariant 2). The
        // add/del/context class makes added vs. removed lines distinguishable in the markup.
        foreach (var (raw, trimmed, _, cssClass) in CharterMarkdown.DiffLines(obj, _markdown))
        {
            var anchor = Block.StableId(trimmed);
            if (renderer.EnableHtmlForBlock)
            {
                renderer.Write("<div class=\"diff-line ");
                renderer.Write(cssClass);
                renderer.Write("\" data-anchor=\"");
                renderer.WriteEscape(anchor);
                renderer.Write("\" id=\"");
                renderer.WriteEscape(anchor);
                renderer.Write("\">");
            }

            renderer.WriteEscape(raw);

            if (renderer.EnableHtmlForBlock)
            {
                renderer.WriteLine("</div>");
            }
        }

        if (renderer.EnableHtmlForBlock)
        {
            renderer.WriteLine("</div>");
        }
    }

    /// <summary>
    /// Render a <c>:::question</c> as a native HTML <c>&lt;form&gt;</c>. The body is parsed via
    /// <see cref="QuestionSpec.Parse(string)"/> (the single source of truth for the question schema — the
    /// renderer never re-declares it), and the form root carries the block's stable id (the annotation
    /// anchor) plus the question id (<c>data-question-id</c> AND a hidden field) so a submitted answer
    /// correlates back to its question. Each answer mode maps to its native control; the markup is plain HTML
    /// that displays with NO Charter JS — the serve-time submit wiring is added later by the SDK (task 15),
    /// never embedded here.
    /// </summary>
    private void WriteQuestion(HtmlRenderer renderer, CustomContainer obj)
    {
        var spec = QuestionSpec.Parse(ContainerBody(obj));

        renderer.EnsureLine();
        if (!renderer.EnableHtmlForBlock)
        {
            return;
        }

        renderer.Write("<form class=\"question\"");
        WriteId(renderer, obj.TryGetAttributes()?.Id);
        renderer.Write(" data-question-id=\"");
        renderer.WriteEscape(spec.Id);
        renderer.WriteLine("\">");

        // The question id also rides a hidden field, so a native (JS-free) submit still posts which question
        // the answer belongs to — data attributes alone are not submitted with a plain <form>.
        renderer.Write("<input type=\"hidden\" name=\"question-id\" value=\"");
        renderer.WriteEscape(spec.Id);
        renderer.WriteLine("\" />");

        renderer.Write("<fieldset><legend>");
        renderer.WriteEscape(spec.Title);
        renderer.WriteLine("</legend>");

        WriteQuestionControls(renderer, spec);

        renderer.WriteLine("</fieldset>");
        renderer.WriteLine("</form>");
    }

    /// <summary>
    /// Emit the native control(s) for the question's <see cref="QuestionSpec.Mode"/>: <c>single</c> → one
    /// radio per option, <c>multi</c> → one checkbox per option, <c>free-text</c> → a <c>&lt;textarea&gt;</c>,
    /// <c>number</c> → a number input, <c>bool</c> → a single checkbox. Every option label appears alongside
    /// its control.
    /// </summary>
    private static void WriteQuestionControls(HtmlRenderer renderer, QuestionSpec spec)
    {
        switch (spec.Mode)
        {
            case QuestionMode.SingleSelect:
                WriteOptionControls(renderer, spec.Options, "radio");
                break;
            case QuestionMode.MultiSelect:
                WriteOptionControls(renderer, spec.Options, "checkbox");
                break;
            case QuestionMode.FreeText:
                renderer.WriteLine("<textarea name=\"answer\"></textarea>");
                break;
            case QuestionMode.Number:
                renderer.WriteLine("<input type=\"number\" name=\"answer\" />");
                break;
            case QuestionMode.Bool:
                renderer.WriteLine("<label><input type=\"checkbox\" name=\"answer\" value=\"true\" /> Yes</label>");
                break;
        }
    }

    /// <summary>
    /// Write one <c>&lt;label&gt;&lt;input type="{inputType}" …/&gt; {option}&lt;/label&gt;</c> per option —
    /// a radio (single-select) or checkbox (multi-select) carrying the option as both its submitted value and
    /// its visible label.
    /// </summary>
    private static void WriteOptionControls(HtmlRenderer renderer, IReadOnlyList<string> options, string inputType)
    {
        foreach (var option in options)
        {
            renderer.Write("<label><input type=\"");
            renderer.Write(inputType);
            renderer.Write("\" name=\"answer\" value=\"");
            renderer.WriteEscape(option);
            renderer.Write("\" /> ");
            renderer.WriteEscape(option);
            renderer.WriteLine("</label>");
        }
    }

    /// <summary>Write an <c>id="…"</c> attribute when the block carries a stable id.</summary>
    private static void WriteId(HtmlRenderer renderer, string? id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            renderer.Write(" id=\"");
            renderer.WriteEscape(id);
            renderer.Write('"');
        }
    }

    /// <summary>
    /// The exact raw source text a container's inner blocks span — the Mermaid source for a
    /// <c>:::diagram</c>, the JSON body for a <c>:::question</c>. Sliced straight from the markdown so it is
    /// preserved verbatim (never markdown-rendered).
    /// </summary>
    private string ContainerBody(CustomContainer obj)
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
