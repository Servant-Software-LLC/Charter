using Markdig.Extensions.CustomContainers;
using Markdig.Extensions.Tables;
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
    /// Render <paramref name="markdown"/> to a COMPLETE, STYLED, portable HTML document. Each top-level block's
    /// root element carries an <c>id</c> attribute equal to that block's stable <see cref="Block.Id"/> — derived
    /// from the same raw content the block model uses, so the rendered id and <see cref="BlockDocument"/> always
    /// agree. Note/warn containers additionally carry a matching CSS class. The rendered block body is wrapped in
    /// the shared <see cref="CharterDocument"/> shell (doctype/html/head/body + the bundled stylesheet), the same
    /// shell <c>review</c> and <c>export</c> use — so all three surfaces are complete and styled, never a bare
    /// fragment (Charter #38). No CSP meta is emitted here: a plain <c>render</c> file is a local trusted
    /// artifact, and the review server supplies the served-page CSP as an HTTP header; only <c>export</c> stamps
    /// a CSP (its strict offline policy) into the shell.
    /// </summary>
    public static string Render(string markdown) => Render(markdown, pendingAnswers: null);

    /// <summary>
    /// <see cref="Render(string)"/> with an overlay of answers that are SAVED but not yet folded into the
    /// markdown — the review server's pending queue (Charter #120).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, a reviewer who answered five questions and then restarted the server saw five BLANK forms.
    /// The answers were never lost — the sidecar had every one — but nothing put them back on the page, so a
    /// working safety net was indistinguishable from data loss.
    /// </para>
    /// <para>
    /// It is an OVERLAY, not a write: the plan file is untouched, so the single-writer rule is intact (the
    /// server still never writes the plan). It exists here rather than in the SDK because the renderer already
    /// implements pre-selection for every mode, the write-in hatch, and escaping — reimplementing that in
    /// JavaScript would be a second copy free to drift from this one, and would leave a JS-free page blank.
    /// </para>
    /// <para>
    /// A pending answer WINS over an inline one. Both can exist — the plan records what was folded in, the
    /// queue holds what the reviewer has since chosen — and the queued one is by definition the later decision.
    /// </para>
    /// </remarks>
    public static string Render(string markdown, IReadOnlyDictionary<string, IReadOnlyList<string>>? pendingAnswers)
        => CharterDocument.Wrap(RenderBody(markdown, pendingAnswers), cspMeta: null);

    /// <summary>
    /// Render <paramref name="markdown"/> to the block-body HTML only — the rendered blocks plus, when a
    /// <c>:::diagram</c> is present, the inlined offline Mermaid runtime — WITHOUT the document shell. This is
    /// the shared core of <see cref="Render(string)"/> (which wraps it in the styled shell) and of
    /// <see cref="ArtifactExporter"/> (which runs its offline asset transforms over this body before wrapping it
    /// in the shell with the export CSP). Splitting body from shell keeps the shell a single source of truth
    /// while letting each surface choose its CSP.
    /// </summary>
    internal static string RenderBody(string markdown) => RenderBody(markdown, pendingAnswers: null);

    /// <inheritdoc cref="RenderBody(string)"/>
    internal static string RenderBody(
        string markdown, IReadOnlyDictionary<string, IReadOnlyList<string>>? pendingAnswers)
    {
        markdown ??= string.Empty;

        MarkdownDocument document;
        try
        {
            document = CharterMarkdown.ParseDocument(markdown);
        }
        catch (ArgumentException)
        {
            // Markdig aborts pathologically deep nesting (e.g. hundreds of nested blockquotes) with an
            // ArgumentException from ParseDocument. Degrade to a visible, escaped placeholder rather than
            // letting the whole render — and thus the served review page, export, and handoff — throw.
            using var errorWriter = new StringWriter();
            var errorRenderer = new HtmlRenderer(errorWriter);
            errorRenderer.Write("<div class=\"charter-parse-error\">");
            errorRenderer.WriteEscape("The plan could not be parsed (input too deeply nested).");
            errorRenderer.Write("</div>");
            errorWriter.Flush();
            return errorWriter.ToString();
        }

        // One shared id-assignment pass over this document; the source map builds the SAME assignment over
        // its own parse of the same markdown, so discriminated ids are identical on both paths (they cannot
        // drift). Every id/data-anchor the renderer emits below is read from THIS assignment.
        var assignment = AnchorAssignment.Build(document, markdown);

        var hasDiagram = false;
        foreach (var node in document)
        {
            var (kind, _) = CharterMarkdown.Describe(node, markdown);

            var attributes = node.GetAttributes();
            attributes.Id = assignment.IdForLine(CharterMarkdown.StartLine(node));
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
            }

            // Sub-block anchor model: a plain top-level list and a :::comparison both stamp each of their
            // items with the shared assignment's SUB-anchor id for that item's line, so the default list
            // renderer emits <li data-anchor="…">. Distinct items get distinct sub-anchors, each derived from
            // that item's own content — so one item's annotation survives edits to the others (invariant 2) —
            // and two identical items are discriminated (-2) by the shared pass. Sub-anchor ids are read from
            // SubIdForLine, not IdForLine: a plain list starts on its first item's line, so the two slots
            // share a key and only the namespace tells them apart. A :::diff carries its per-line sub-anchors
            // through its own custom renderer (CharterContainerRenderer) rather than this item stamping.
            //
            // The <ul>/<ol> keeps its OWN block anchor: a click on the list's padding rather than on a bullet
            // still resolves to it, and a note the reviewer means about the list as a whole still has a home.
            if (kind is BlockKind.Comparison or BlockKind.List)
            {
                foreach (var (row, _, subLine) in CharterMarkdown.SubAnchors(node, markdown))
                {
                    row.GetAttributes().AddProperty("data-anchor", assignment.SubIdForLine(subLine));
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
        renderer.ObjectRenderers.Replace<HtmlCustomContainerRenderer>(
            new CharterContainerRenderer(markdown, assignment, pendingAnswers));

        // A wide table must stay REACHABLE, so every table is emitted inside its own scroll container
        // (Charter #68). The wrapper is anchor-invisible by construction — see CharterTableRenderer.
        renderer.ObjectRenderers.Replace<HtmlTableRenderer>(new CharterTableRenderer());

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
    /// <c>mermaid.initialize({ startOnLoad: false, theme: … })</c> / <c>mermaid.run({ nodes })</c> bootstrap — both as
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

        // Make the inlined library SAFE to sit inside <script>…</script>. The browser's HTML tokenizer leaves
        // the raw "script data" state on `<!--`, `<script`, or `</script` — EVEN when those appear inside a JS
        // string/regex literal (this build has `<!--` inside a regex and inside strings). Left un-escaped, that
        // tore the minified library apart at parse time: its tail dumped into the page as visible text, its
        // sandbox-<iframe> template literal (`<iframe … sandbox="${…}">`) materialized as a real element the
        // strict CSP then blocked, and the truncated script threw so `mermaid` never defined (Charter #37). The
        // WHATWG-recommended escape of these three sequences is semantics-neutral here: each occurs only inside a
        // string/regex literal in this vendored build, where `<\!--` / `<\/script` / `<\script` denote the
        // identical characters. (The browser acceptance test proves Mermaid still defines and renders to <svg>.)
        library = library
            .Replace("<!--", "<\\!--", StringComparison.Ordinal)
            .Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase)
            .Replace("<script", "<\\script", StringComparison.OrdinalIgnoreCase);

        // Render diagrams as INLINE SVG under the strict CSP: securityLevel 'antiscript' keeps Mermaid on its
        // inline-<svg> render path (never the sandboxed-<iframe>/data: path that `default-src 'none'` blocks and
        // that needs a `frame-src` we deliberately do not grant) WHILE still stripping <script> from diagram
        // labels — so no CSP relaxation is required and the security posture is not weakened. The theme tracks
        // prefers-color-scheme, the same signal the bundled stylesheet uses.
        //
        // Render ONLY the blocks Charter authored. Mermaid's default selector is `.mermaid`, document-wide,
        // and `startOnLoad` re-applies it on window load — so a :::custom-html body containing a
        // <pre class="mermaid"> (a plan that DOCUMENTS Mermaid is the obvious case) had that element replaced
        // by a rendered SVG, in the served page and in the saved artifact alike. The escape hatch's one
        // promise is that the author's markup is rendered as written, and invariant 1 says the artifact is
        // what the author wrote (Charter #177). It looked intermittent because it fired only when some
        // unrelated block elsewhere in the plan happened to be a diagram, which is what inlines this at all.
        //
        // The node list is narrowed the same way the SDK's anchor walk is: a MONOTONE containment test.
        // `.custom-html-scroll` wraps the whole of an author's body, so an author forging that class inside
        // their own markup only ever ADDS an ancestor match — forgery can shrink this set, never grow it, and
        // shrinking it costs the forger their own rendering and nothing else. A nested :::diagram (inside a
        // ::::note, say) still matches, so it renders exactly as before whenever the runtime is present.
        const string bootstrap =
            "mermaid.initialize({ startOnLoad: false, securityLevel: 'antiscript', " +
            "theme: window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'default' });\n" +
            // Wrapped in an IIFE so the artifact's global scope stays as bare as it was: a :::custom-html
            // body may carry a <script> of the author's own, and a plan is not the place to discover that
            // Charter took a name.
            "(function () {\n" +
            "  var nodes = [];\n" +
            "  var candidates = document.querySelectorAll('pre.mermaid');\n" +
            "  for (var i = 0; i < candidates.length; i++) {\n" +
            "    var parent = candidates[i].parentElement;\n" +
            "    if (!parent || !parent.closest('.custom-html-scroll')) nodes.push(candidates[i]);\n" +
            "  }\n" +
            "  mermaid.run({ nodes: nodes });\n" +
            "})();";

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
/// number input for number, two mutually-exclusive Yes/No radios for bool) — plain native HTML that needs no
/// Charter JS to display.
/// Every other container (<c>:::note</c>, <c>:::warn</c>, <c>:::comparison</c>) falls through to the default
/// <see cref="HtmlCustomContainerRenderer"/>.
/// </summary>
internal sealed class CharterContainerRenderer : HtmlCustomContainerRenderer
{
    /// <summary>The two declared <c>bool</c> values — the "known options" a bool answer is matched against.</summary>
    private static readonly string[] BoolValues = { "true", "false" };

    private readonly string _markdown;
    private readonly AnchorAssignment _assignment;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>>? _pendingAnswers;

    public CharterContainerRenderer(string markdown, AnchorAssignment assignment)
        : this(markdown, assignment, pendingAnswers: null)
    {
    }

    public CharterContainerRenderer(
        string markdown,
        AnchorAssignment assignment,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? pendingAnswers)
    {
        _markdown = markdown ?? string.Empty;
        _assignment = assignment;
        _pendingAnswers = pendingAnswers;
    }

    protected override void Write(HtmlRenderer renderer, CustomContainer obj)
    {
        // Dispatch on the SAME classification the block model uses (CharterMarkdown.ClassifyContainer), so the
        // renderer and the catalog can never disagree on what a :::directive is. The containers whose markup
        // diverges from the default callout get a bespoke writer; :::note / :::warn / :::comparison fall through
        // to the default; and an unrecognized :::foo (BlockKind.Unknown) renders as a visible unknown-directive
        // element rather than silently masquerading as a note (Charter #22).
        switch (CharterMarkdown.ClassifyContainer(obj))
        {
            case BlockKind.Diagram:
                WriteDiagram(renderer, obj);
                break;
            case BlockKind.Diff:
                WriteDiff(renderer, obj);
                break;
            case BlockKind.Question:
                WriteQuestion(renderer, obj);
                break;
            case BlockKind.CustomHtml:
                WriteCustomHtml(renderer, obj);
                break;
            case BlockKind.Unknown:
                WriteUnknown(renderer, obj);
                break;
            default:
                base.Write(renderer, obj);
                break;
        }
    }

    /// <summary>
    /// Render an unrecognized <c>:::foo</c> container as a VISIBLE, clearly-marked "unknown directive" element
    /// that names the offending directive, rather than letting a typo/unlisted container flatten into a silent
    /// note (Charter #22). The block's stable id rides the wrapping <c>&lt;div&gt;</c> so the unknown block is
    /// still annotatable in the review loop.
    /// Its BODY is preserved (escaped, in a preformatted element) beneath the marker: the interop contract
    /// requires an unknown directive's body to survive as prose context and to NEVER be silently dropped —
    /// discarding it lost real plan content behind a directive typo (Charter #48/C5).
    /// </summary>
    private void WriteUnknown(HtmlRenderer renderer, CustomContainer obj)
    {
        renderer.EnsureLine();
        if (!renderer.EnableHtmlForBlock)
        {
            return;
        }

        var directive = obj.Info?.Trim() ?? string.Empty;

        renderer.Write("<div class=\"unknown-directive\"");
        WriteId(renderer, obj.TryGetAttributes()?.Id);
        renderer.Write("><strong>Unknown directive:</strong> <code>:::");
        renderer.WriteEscape(directive);
        renderer.Write("</code>");

        // Escaped, never raw: an unknown directive's body is untrusted plan content, and :::custom-html is the
        // only sanctioned raw-HTML surface. <pre> keeps whatever the author laid out (a tree, a table sketch)
        // legible instead of collapsing its whitespace.
        var body = ContainerBody(obj);
        if (body.Trim().Length > 0)
        {
            // The preserved body is a scroll region (Charter #87). It exists so a directive typo cannot lose
            // real plan content, and a preserved body a reviewer cannot read to the end undercuts exactly
            // that. It carries no id — the block's anchor is on the enclosing <div> — so it is anchor-invisible
            // even though it is a tab stop.
            renderer.Write("<pre class=\"unknown-directive-body\"");
            renderer.Write(ScrollRegion.Attributes("Unknown directive body"));
            renderer.Write('>');
            renderer.WriteEscape(body.Trim('\n', '\r'));
            renderer.Write("</pre>");
        }

        renderer.WriteLine("</div>");
    }

    /// <summary>
    /// Render a <c>:::custom-html</c> container as the sanctioned raw-HTML escape hatch: emit its inner source
    /// VERBATIM (unescaped) so the author's explicitly opted-in HTML stays live. This is the ONE block where
    /// raw HTML is intentional — every other surface escapes it (the pipeline's <c>DisableHtml</c>), so bare
    /// prose HTML can never phone home or run, but a deliberate <c>:::custom-html</c> block still can. The
    /// block's stable id rides the wrapping <c>&lt;div&gt;</c> so the escape-hatch content stays annotatable.
    /// <para>
    /// The body is emitted inside an anchor-invisible <see cref="ScrollRegion"/> (Charter #87). The escape
    /// hatch's containment used to sit on the author's own <c>&lt;table&gt;</c> via a stylesheet rule, which
    /// could carry neither a visible scrollbar nor a tab stop — and the renderer cannot put an attribute on
    /// markup it emits verbatim, so the region has to be a box Charter owns. Wrapping the body rather than
    /// scrolling the anchored <c>div.custom-html</c> itself also keeps that <c>&lt;div&gt;</c> a plain
    /// non-scrolling frame, so the SDK's absolutely-positioned whole-block badge stays pinned to its corner
    /// instead of riding away with the horizontal scroll (the Charter #51 lesson).
    /// </para>
    /// </summary>
    private void WriteCustomHtml(HtmlRenderer renderer, CustomContainer obj)
    {
        renderer.EnsureLine();
        if (!renderer.EnableHtmlForBlock)
        {
            return;
        }

        renderer.Write("<div class=\"custom-html\"");
        WriteId(renderer, obj.TryGetAttributes()?.Id);
        renderer.Write('>');
        renderer.Write(ScrollRegion.Open("custom-html-scroll", "Custom HTML"));
        renderer.Write(ContainerBody(obj)); // RAW, not escaped — the escape hatch
        renderer.Write("</div>");
        renderer.WriteLine("</div>");
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

        // Emit the Mermaid source EXACTLY as authored (the client library reads the element's textContent, so
        // any inline formatting would corrupt the graph) but NEVER the fence markers. The documented authoring
        // form wraps the source in a fenced ```mermaid code block, which Markdig models as a FencedCodeBlock
        // child; its ``` opener/closer and its `mermaid` info string are Markdig syntax, NOT diagram source, so
        // emitting them handed Mermaid "```mermaid" as the first line and broke every diagram (Charter #40).
        // When the body IS such a fenced block, write only its CONTENT lines; a fence-less :::diagram body (raw
        // Mermaid) is emitted verbatim as before. Both forms are accepted so the documented examples work.
        if (TryGetFencedMermaidSource(obj, out var fenced))
        {
            renderer.WriteLeafRawLines(fenced, writeEndOfLines: false, escape: true);
        }
        else
        {
            renderer.WriteEscape(ContainerBody(obj));
        }

        if (renderer.EnableHtmlForBlock)
        {
            renderer.WriteLine("</pre>");
        }
    }

    /// <summary>
    /// True when the <c>:::diagram</c> container's body is a single fenced code block — the documented
    /// <c>```mermaid</c> (or bare <c>```</c>) authoring form. In that case the block's
    /// <see cref="LeafBlock.Lines"/> are the Mermaid source, and the caller emits ONLY those content lines,
    /// never the container's ``` fence markers or its <c>mermaid</c> info string. Any other body — raw
    /// fence-less Mermaid, or mixed content — returns <c>false</c> so the caller emits the verbatim container
    /// source instead.
    /// </summary>
    private static bool TryGetFencedMermaidSource(CustomContainer obj, out FencedCodeBlock fenced)
    {
        if (obj.Count == 1 && obj[0] is FencedCodeBlock only)
        {
            fenced = only;
            return true;
        }

        fenced = null!;
        return false;
    }

    /// <summary>
    /// Render a <c>:::diff</c> as an outer <c>&lt;div class="diff" id="…"&gt;</c> card holding an inner
    /// <see cref="ScrollRegion"/>, one <c>&lt;div class="diff-line …"&gt;</c> per line (Charter #87).
    /// <para>
    /// WHY two boxes. <c>.diff { overflow: hidden }</c> is what clips the per-line FULL-BLEED add/del
    /// backgrounds to the card's rounded corners, and that clip must hold unconditionally — including while
    /// the content is scrolled — so it cannot simply become <c>auto</c>: one box cannot both hard-clip its own
    /// painted corners and be the user-scrollable box whose scrollbar is laid out inside them. Keeping the
    /// anchored card a NON-scrolling, positioned frame has a second payoff: the SDK's whole-block annotation
    /// badge is absolutely positioned inside it, and an absolutely-positioned child of a scroll container
    /// rides away with the content (the Charter #51 lesson) — here it stays pinned by construction.
    /// </para>
    /// <para>
    /// The inner region is ANCHOR-INVISIBLE, which matters more here than anywhere else in the catalog: a
    /// <c>:::diff</c> carries per-LINE sub-anchors, so <c>closestAnchored</c> must walk from a clicked line
    /// straight past the region to that line's own <c>id</c>/<c>data-anchor</c>. The region therefore carries
    /// neither, and <see cref="SourceMap"/> — built from the markdown — never sees it at all.
    /// </para>
    /// </summary>
    private void WriteDiff(HtmlRenderer renderer, CustomContainer obj)
    {
        renderer.EnsureLine();

        if (renderer.EnableHtmlForBlock)
        {
            renderer.Write("<div class=\"diff\"");
            WriteId(renderer, obj.TryGetAttributes()?.Id);
            renderer.WriteLine(">");
            renderer.WriteLine(ScrollRegion.Open("diff-scroll", "Diff"));
        }

        // Each diff LINE carries its OWN sub-anchor, read from the shared assignment by that line's markdown
        // line — the same anchor SourceMap.Build registers via the same assignment, so a note on one line
        // round-trips to that line and survives edits to the others (invariant 2), and two identical diff
        // lines are discriminated (-2) rather than aliased. The add/del/context class makes added vs. removed
        // lines distinguishable in the markup.
        foreach (var (raw, _, line, cssClass) in CharterMarkdown.DiffLines(obj, _markdown))
        {
            var anchor = _assignment.SubIdForLine(line);
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
            renderer.WriteLine("</div>"); // the scroll region
            renderer.WriteLine("</div>"); // the diff card
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
        renderer.EnsureLine();
        if (!renderer.EnableHtmlForBlock)
        {
            return;
        }

        var id = obj.TryGetAttributes()?.Id;

        // Degrade a malformed/empty :::question to a visible placeholder rather than throwing (which would
        // abort the whole render — and thus the served page / export). The placeholder KEEPS the block's
        // stable id so a reviewer can still annotate it, and every other block still renders.
        if (!QuestionSpec.TryParse(ContainerBody(obj), out var spec, out var error) || spec is null)
        {
            renderer.Write("<div class=\"question-error\"");
            WriteId(renderer, id);
            renderer.Write('>');
            renderer.WriteEscape(error ?? "The :::question block could not be parsed.");
            renderer.WriteLine("</div>");
            return;
        }

        // A non-empty inline `answer` is the on-disk RESOLVED marker (charter-format). Before Charter #48 the
        // renderer ignored it entirely, so a settled decision rendered byte-identically to an open one and the
        // review artifact could not show the human which decisions were already made — a second review round
        // re-asked every answered question. The resolved state is now marked BOTH for a human (the `answered`
        // class + an "Answered" status in the legend, styled by charter.css) and for a machine
        // (`data-answered="true"`), and each mode pre-selects its chosen value(s) below. An OPEN question's
        // markup is unchanged, byte for byte.
        // An answer the reviewer has SAVED but that no drain has folded into the plan yet wins over the inline
        // one: both can legitimately exist, and the queued one is by definition the later decision (#120). It
        // is rendered through exactly the same path as an inline answer — same pre-selection, same write-in
        // handling, same escaping — and additionally marked as pending, because "saved" and "delivered to the
        // agent" are different facts and a reviewer must not be told the second when only the first is true.
        // "Answered" is AnswerRules.IsDecision, not a element count: a blank value is not a decision, and a
        // page that says "Answered" over an empty choice tells a reviewer something false (Charter #188). The
        // predicate is shared with the forensic record and the flatten so the three cannot disagree.
        var pending = PendingAnswerFor(spec.Id);
        var effectiveAnswer = pending ?? spec.Answer;
        var answered = AnswerRules.IsDecision(effectiveAnswer);

        renderer.Write("<form class=\"question");
        if (answered)
        {
            renderer.Write(" answered");
        }

        if (pending is not null)
        {
            renderer.Write(" answer-pending");
        }

        renderer.Write('"');
        WriteId(renderer, id);
        renderer.Write(" data-question-id=\"");
        renderer.WriteEscape(spec.Id);
        renderer.Write('"');

        // Declare the MODE to the SDK. The SDK has always preferred an explicit data-question-mode over
        // inferring the mode from the controls present — but the renderer never stamped it, so inference was
        // the only path, and inference reads a `bool` (two Yes/No radios since Charter #43) as a `single`. That
        // left the SDK's bool branch dead and reported every bool answer to the agent as mode "single". The
        // token comes from QuestionSpec, the single source of truth for the mode vocabulary.
        renderer.Write(" data-question-mode=\"");
        renderer.WriteEscape(QuestionSpec.Token(spec.Mode));
        renderer.Write('"');
        if (answered)
        {
            renderer.Write(" data-answered=\"true\"");
        }

        if (pending is not null)
        {
            renderer.Write(" data-answer-pending=\"true\"");
        }

        renderer.WriteLine(">");

        // The question id also rides a hidden field, so a native (JS-free) submit still posts which question
        // the answer belongs to — data attributes alone are not submitted with a plain <form>.
        renderer.Write("<input type=\"hidden\" name=\"question-id\" value=\"");
        renderer.WriteEscape(spec.Id);
        renderer.WriteLine("\" />");

        renderer.Write("<fieldset><legend>");
        renderer.WriteEscape(spec.Title);
        if (answered)
        {
            // The wording is the whole point for a pending answer. "Answered" alone would let a reviewer infer
            // the agent has it; it does not until a drain. Saying so is what makes a restarted server legible
            // rather than alarming.
            renderer.Write(pending is not null
                ? " <span class=\"question-status\">Answered — saved, not yet sent</span>"
                : " <span class=\"question-status\">Answered</span>");
        }

        renderer.WriteLine("</legend>");

        // The agent's reasoning, INSIDE the box (Charter #132). Written as prose beside the block it was an
        // ordinary CommonMark paragraph with nothing binding it to the question — and a reviewer who met one
        // sitting between two questions read it as the introduction to the one BELOW, answering one question
        // while reading the argument for another. Rendered here, the binding is visible rather than inferred.
        //
        // Between the title and the controls on purpose: the reviewer reads what is being asked, then why,
        // then chooses. Escaped like every other echoed value, and plain text by design — see QuestionSpec.
        if (spec.Rationale is { Length: > 0 })
        {
            renderer.Write("<p class=\"question-rationale\">");
            renderer.WriteEscape(spec.Rationale);
            renderer.WriteLine("</p>");
        }

        WriteQuestionControls(renderer, spec, effectiveAnswer);
        WriteSubmitControl(renderer);

        renderer.WriteLine("</fieldset>");
        renderer.WriteLine("</form>");
    }

    /// <summary>
    /// Emit the question form's SUBMIT control (Charter #56). Without it a reviewer could not answer AT ALL:
    /// the SDK listens for the <c>submit</c> event, and a form carrying no submit control produces none — not
    /// from clicking a radio, not from Enter, not from Ctrl+Enter. Only a scripted <c>requestSubmit()</c> did,
    /// which is what the browser test used, so the suite stayed green over an unusable form.
    ///
    /// It ships <c>disabled</c>, which is the correct initial state twice over. Semantically: nothing has
    /// changed yet, so there is nothing to submit — true of an OPEN question (nothing chosen) and of a RESOLVED
    /// one (what is selected IS the recorded answer). Structurally: the saved artifact carries no SDK
    /// (invariant 1), so an enabled button there would fire a NATIVE submit and navigate the page to
    /// <c>?answer=…</c>. The SDK owns the enabled state from load onward — it enables the moment the reviewer's
    /// answer differs from the recorded one, which is also what lets a settled decision be revised and
    /// re-submitted. A disabled default button additionally suppresses the form's implicit Enter submission,
    /// so the keyboard path follows the same rule for free.
    /// </summary>
    private static void WriteSubmitControl(HtmlRenderer renderer)
        => renderer.WriteLine(
            "<div class=\"question-actions\">"
            + "<button type=\"submit\" class=\"question-submit\" disabled>Save answer</button></div>");

    /// <summary>
    /// Emit the native control(s) for the question's <see cref="QuestionSpec.Mode"/>: <c>single</c> → one
    /// radio per option, <c>multi</c> → one checkbox per option, <c>free-text</c> → a <c>&lt;textarea&gt;</c>,
    /// <c>number</c> → a number input, <c>bool</c> → two mutually-exclusive Yes/No radios (values
    /// <c>true</c>/<c>false</c>). Every option label appears alongside its control.
    /// When the question is RESOLVED (a non-empty <see cref="QuestionSpec.Answer"/>) the chosen value(s) are
    /// PRE-SELECTED — <c>checked</c> on the matching radio/checkbox, the value on the textarea/number input —
    /// so the artifact shows what has already been settled. An OPEN question emits exactly the markup it
    /// always did (nothing is pre-selected, so Yes / No / unanswered stay distinguishable — Charter #43).
    /// Every echoed answer value is HTML-escaped: reviewer-supplied answer text is untrusted input.
    /// </summary>
    /// <summary>
    /// The queued answer for <paramref name="questionId"/>, or <see langword="null"/> when none is pending.
    /// </summary>
    /// <remarks>
    /// An EMPTY queued answer is not "no answer pending" — it is a pending RETRACTION (Charter #63: submitting
    /// an emptied form clears a recorded answer). It is returned as an empty list so it overrides the inline
    /// answer and the question renders open again, which is what the reviewer asked for. Only the absence of a
    /// queue entry yields null.
    /// </remarks>
    private IReadOnlyList<string>? PendingAnswerFor(string questionId)
        => _pendingAnswers is not null && _pendingAnswers.TryGetValue(questionId, out var values)
            ? values ?? Array.Empty<string>()
            : null;

    private static void WriteQuestionControls(
        HtmlRenderer renderer, QuestionSpec spec, IReadOnlyList<string> answer)
    {

        switch (spec.Mode)
        {
            case QuestionMode.SingleSelect:
                WriteOptionControls(renderer, spec.Options, "radio", answer, spec.Recommended);
                break;
            case QuestionMode.MultiSelect:
                WriteOptionControls(renderer, spec.Options, "checkbox", answer, spec.Recommended);
                break;
            case QuestionMode.FreeText:
                // The answer rides as the textarea's TEXT content; escaped, so an answer carrying markup or a
                // </textarea> sequence can never break out of the element.
                renderer.Write("<textarea name=\"answer\">");
                renderer.WriteEscape(JoinAnswer(answer, "\n"));
                renderer.WriteLine("</textarea>");
                break;
            case QuestionMode.Number:
                renderer.Write("<input type=\"number\" name=\"answer\"");
                WriteValueAttribute(renderer, JoinAnswer(answer, ", "));
                renderer.WriteLine(" />");
                break;
            case QuestionMode.Bool:
                // Two mutually-exclusive radios (name="answer") so Yes / No / unanswered are all
                // distinguishable — a lone checkbox left Yes and unanswered indistinguishable (Charter #43).
                // The selected radio's value ("true"/"false") is collected, stored, and folded inline like any
                // other single-value answer; a resolved question pre-checks the one it chose.
                WriteChoice(renderer, "radio", "true", "Yes", IsChosen(answer, "true"), cssClass: null);
                WriteChoice(renderer, "radio", "false", "No", IsChosen(answer, "false"), cssClass: null);
                WriteWriteIns(renderer, "radio", answer, BoolValues);
                break;
        }
    }

    /// <summary>
    /// Write one <c>&lt;label&gt;&lt;input type="{inputType}" …/&gt; {option}&lt;/label&gt;</c> per option —
    /// a radio (single-select) or checkbox (multi-select) carrying the option as both its submitted value and
    /// its visible label, <c>checked</c> when <paramref name="answer"/> chose it. Any answer value that
    /// matches NO declared option is then surfaced as a checked write-in rather than silently vanishing.
    /// </summary>
    private static void WriteOptionControls(
        HtmlRenderer renderer,
        IReadOnlyList<string> options,
        string inputType,
        IReadOnlyList<string> answer,
        string? recommended)
    {
        WriteOptionControlsCore(renderer, options, inputType, answer, recommended);
    }

    /// <summary>
    /// The escape hatch every select needs (#109): a final "Something else" control paired with a text input,
    /// so a reviewer can answer in their own words.
    /// <para>
    /// The agent authoring the options is the party <b>least</b> qualified to know they are exhaustive — it is
    /// asking precisely because it does not know the answer. Without this, a reviewer who disagrees with the
    /// framing must pick a wrong option or abandon the form, and either way the real decision is lost.
    /// </para>
    /// <para>
    /// Emitted by the RENDERER, deliberately not authored into <see cref="QuestionSpec.Options"/> by the agent.
    /// <see cref="HandoffMarkdown"/> emits the option list verbatim into the CommonMark Guardrails consumes, so
    /// an authored "Other" would become a real option in the data model — handed downstream as though the agent
    /// had proposed it. Doing it here keeps <c>options</c> meaning exactly what it claims, cannot be forgotten,
    /// and retrofits every chart that already exists.
    /// </para>
    /// <para>
    /// No schema change is required: nothing validates that an answer is one of the options, <c>answer</c> is
    /// already a string array, and <see cref="WriteWriteIns"/> already renders a value matching no declared
    /// option as a checked write-in. This simply gives the reviewer a way to CREATE one.
    /// </para>
    /// </summary>
    private static void WriteOtherControl(HtmlRenderer renderer, string inputType)
    {
        renderer.Write("<label class=\"charter-answer-other\"><input type=\"");
        renderer.WriteEscape(inputType);
        renderer.Write("\" name=\"answer\" value=\"\" data-answer-other=\"1\" /> Something else: ");
        renderer.Write("<input type=\"text\" class=\"charter-answer-other-text\" data-answer-other-text=\"1\" ");
        renderer.WriteLine("placeholder=\"your answer\" /></label>");
    }

    private static void WriteOptionControlsCore(
        HtmlRenderer renderer,
        IReadOnlyList<string> options,
        string inputType,
        IReadOnlyList<string> answer,
        string? recommended = null)
    {
        foreach (var option in options)
        {
            // The agent's lean rides the LABEL only; the submitted value stays the bare option (Charter #125).
            // "(Recommended)" is the convention reviewers already know from Claude Code's own AskUserQuestion,
            // so it is reproduced verbatim rather than invented — but it must never become part of the recorded
            // decision, which is what authoring it into the option text would have done.
            var recommend = recommended is not null && string.Equals(option, recommended, StringComparison.Ordinal);
            var label = recommend ? option + " (Recommended)" : option;
            WriteChoice(
                renderer,
                inputType,
                option,
                label,
                IsChosen(answer, option),
                cssClass: recommend ? "charter-answer-recommended" : null);
        }

        WriteWriteIns(renderer, inputType, answer, options);
        WriteOtherControl(renderer, inputType);
    }

    /// <summary>
    /// Surface every answer value that matches NO declared option as its own CHECKED "write-in" control. An
    /// answer that no longer lines up with the options (the option was renamed or removed after the question
    /// was resolved) is a real decision: dropping it would silently lose the human's answer, so it is rendered
    /// instead — clearly marked, and escaped like every other echoed value.
    /// </summary>
    private static void WriteWriteIns(
        HtmlRenderer renderer, string inputType, IReadOnlyList<string> answer, IReadOnlyList<string> known)
    {
        foreach (var value in answer)
        {
            if (!known.Contains(value, StringComparer.Ordinal))
            {
                WriteChoice(renderer, inputType, value, value + " (write-in)", checkedState: true, cssClass: "write-in");
            }
        }
    }

    /// <summary>
    /// Write one labeled native control: <c>&lt;label&gt;&lt;input type="…" name="answer" value="…"
    /// [checked] /&gt; {label}&lt;/label&gt;</c>. Both the submitted value and the visible label are escaped.
    /// An UNCHECKED control with no css class emits exactly the markup the renderer emitted before answered
    /// questions were rendered, so open-question output is unchanged.
    /// </summary>
    private static void WriteChoice(
        HtmlRenderer renderer, string inputType, string value, string label, bool checkedState, string? cssClass)
    {
        renderer.Write("<label");
        if (cssClass is not null)
        {
            renderer.Write(" class=\"");
            renderer.Write(cssClass);
            renderer.Write('"');
        }

        renderer.Write("><input type=\"");
        renderer.Write(inputType);
        renderer.Write("\" name=\"answer\" value=\"");
        renderer.WriteEscape(value);
        renderer.Write('"');
        if (checkedState)
        {
            renderer.Write(" checked");
        }

        renderer.Write(" /> ");
        renderer.WriteEscape(label);
        renderer.WriteLine("</label>");
    }

    /// <summary>Write a <c>value="…"</c> attribute (escaped) when <paramref name="value"/> is non-empty.</summary>
    private static void WriteValueAttribute(HtmlRenderer renderer, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        renderer.Write(" value=\"");
        renderer.WriteEscape(value);
        renderer.Write('"');
    }

    /// <summary>True when the resolved <paramref name="answer"/> selected <paramref name="value"/>.</summary>
    private static bool IsChosen(IReadOnlyList<string> answer, string value)
        => answer.Contains(value, StringComparer.Ordinal);

    /// <summary>
    /// The answer's value(s) as one string for a single-valued control. The schema says a
    /// <c>free-text</c>/<c>number</c> answer is ONE element, but a malformed multi-element answer is joined
    /// rather than truncated so no value is silently dropped.
    /// </summary>
    private static string JoinAnswer(IReadOnlyList<string> answer, string separator)
        => answer.Count == 0 ? string.Empty : string.Join(separator, answer);

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
/// The ONE definition of the chrome that makes a clipping block reachable — a <c>tabindex="0"</c> tab stop,
/// a <c>role="region"</c> and an accessible name — shared by every scroll region the renderer emits
/// (Charter #68, #87). Single-sourced so a table, a diff, a code block, an unknown directive's preserved
/// body and the <c>:::custom-html</c> escape hatch cannot drift into carrying different affordances.
/// </summary>
/// <remarks>
/// <para>
/// <c>tabindex="0"</c> plus <c>role="region"</c> and a name is the established pattern for a scrollable
/// region: without the tab stop a keyboard-only reviewer cannot scroll it (Chromium's focusable-scrollers
/// behaviour is version-dependent and leaves <c>tabIndex</c> at -1), and a focusable element with no role or
/// name announces as nothing. The cost is a tab stop and a landmark even on a block that FITS, which is the
/// accepted trade — CSS cannot make either conditional on overflow, and doing it in script would break the
/// SDK-free saved artifact (invariant 1). What a block that fits does NOT gain is any visible decoration:
/// <c>overflow-x: auto</c> draws no bar and takes no gutter when there is nothing to scroll.
/// </para>
/// <para>
/// A region that is a WRAPPER (rather than the clipping element itself) must be ANCHOR-INVISIBLE, so
/// <see cref="Open"/> deliberately emits no <c>id</c>, no <c>data-anchor</c> and no
/// <c>data-charter-anchor</c>: the SDK walks up from the clicked node to the nearest ancestor carrying one
/// of those, so a wrapper holding an anchor of its own would silently re-target every note on the block —
/// and, in a <c>:::diff</c>, would shadow the individual LINE the reviewer meant.
/// </para>
/// </remarks>
internal static class ScrollRegion
{
    /// <summary>
    /// The affordance attributes, ready to append to an opening tag (leading space included) — for a
    /// clipping element the renderer already owns, such as a code block's <c>&lt;pre&gt;</c>.
    /// </summary>
    internal static string Attributes(string label)
        => " tabindex=\"0\" role=\"region\" aria-label=\"" + label + "\"";

    /// <summary>
    /// An anchor-invisible <c>&lt;div&gt;</c> scroll-region opening tag — for a clipping element the renderer
    /// cannot attribute directly (a <c>&lt;table&gt;</c>, a diff's per-line rows, an author's verbatim HTML).
    /// </summary>
    internal static string Open(string cssClass, string label)
        => "<div class=\"" + cssClass + "\"" + Attributes(label) + ">";
}

/// <summary>
/// Renders a GFM pipe table INSIDE a horizontal scroll container:
/// <c>&lt;div class="table-scroll" tabindex="0" role="region" aria-label="Table"&gt;&lt;table id="..."&gt;…</c>.
/// The table markup itself is the stock Markdig rendering, delegated to unchanged.
/// </summary>
/// <remarks>
/// <para>
/// WHY a wrapper (Charter #68). A plan's tables carry exactly the content that is wide and must not be
/// truncated — file paths, method signatures — so they overflow the review column, and the review-notes panel
/// narrows that column further. The stylesheet used to put <c>overflow-x</c> on the <c>&lt;table&gt;</c>
/// element itself; that shape gave the reviewer nothing to act on (Chromium draws its scrollbar as a fading
/// OVERLAY, so the measured gutter is 0px — text visibly cut off, no signal it can move) and no keyboard way
/// in at all. Only a real element can be given a persistent scrollbar, a focus ring and a tab stop, so the
/// renderer emits one.
/// </para>
/// <para>
/// The wrapper is ANCHOR-INVISIBLE, which is the load-bearing constraint. The annotation SDK walks up from
/// the clicked node to the nearest ancestor carrying an <c>id</c>/<c>data-anchor</c>
/// (<c>closestAnchored</c>), so a wrapper holding an id of its own would silently re-target every note on a
/// table — and, for a table nested in a <c>:::comparison</c>, would shadow the card that is the real anchor.
/// It therefore carries NO <c>id</c> and NO anchor attribute: the block's stable id stays on the
/// <c>&lt;table&gt;</c> (written by the delegated base renderer from the block's attributes), the
/// <see cref="SourceMap"/> never sees the wrapper at all, and sub-anchors are untouched.
/// </para>
/// <para>
/// <c>tabindex="0"</c> plus <c>role="region"</c> and an accessible name is the established pattern for a
/// scrollable region: without the tab stop a keyboard-only reviewer cannot scroll it (Chromium's
/// focusable-scrollers behaviour is version-dependent and leaves <c>tabIndex</c> at -1), and a focusable
/// <c>&lt;div&gt;</c> with no role or name announces as nothing. The cost is a tab stop and a landmark even
/// on a table that fits, which is the accepted trade — CSS cannot make either conditional on overflow, and
/// doing it in script would break the SDK-free saved artifact (invariant 1).
/// </para>
/// </remarks>
internal sealed class CharterTableRenderer : HtmlTableRenderer
{
    private static readonly string Open = ScrollRegion.Open("table-scroll", "Table");

    protected override void Write(HtmlRenderer renderer, Table table)
    {
        if (!renderer.EnableHtmlForBlock)
        {
            base.Write(renderer, table);
            return;
        }

        renderer.EnsureLine();
        renderer.WriteLine(Open);
        base.Write(renderer, table);
        renderer.WriteLine("</div>");
    }
}

/// <summary>
/// Renders a fenced/indented code block as
/// <c>&lt;pre id="..." tabindex="0" role="region" aria-label="Code block"&gt;&lt;code
/// class="language-..."&gt;</c> — the block's stable id on the <c>&lt;pre&gt;</c> root (where every other
/// block carries its anchor), with the fence's info string as the <c>language-</c> class on
/// <c>&lt;code&gt;</c>.
/// </summary>
/// <remarks>
/// Charter #87. A code block clips: <c>pre { overflow: auto }</c>, over content whose whole point is that it
/// has no line-break opportunity. Unlike a <c>&lt;table&gt;</c> (#68) a <c>&lt;pre&gt;</c> IS already the
/// scroll container and IS a real element that can take a tab stop, so the affordance goes straight onto it
/// and no wrapper is emitted — which keeps the block's DOM shape, and its anchor, exactly where they were.
/// The stylesheet supplies the matching persistent scrollbar gutter and focus ring.
/// </remarks>
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

            renderer.Write(ScrollRegion.Attributes("Code block"));
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
