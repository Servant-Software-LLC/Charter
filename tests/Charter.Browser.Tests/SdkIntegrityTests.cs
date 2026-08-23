using System.Net;
using System.Text.Json;
using Charter.Core;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #176 / #177 / #178 / #179 — one rule, four places it was broken.
///
/// <para><b>The rule.</b> <c>:::custom-html</c> is the escape hatch, and its one promise is that the author's
/// markup is rendered AS WRITTEN. So the SDK — and the Mermaid bootstrap the renderer inlines — may never
/// reach into the document by a NAME an author controls and change what it finds there. Charter owns its own
/// chrome by CONSTRUCTION (it made the element, so it holds the reference and the private property), and
/// where a name really is the only handle available, the selection is narrowed by the same monotone
/// containment predicate #166 introduced: inside an opaque region, an author's markup is out of reach.</para>
///
/// <para><b>Why this is a different category from #166.</b> That fix is a read-only containment TEST, and
/// forging a region class can only ever make more things unanchorable — never fewer — so forgery buys
/// nothing. These four couplings were DESTRUCTIVE: an author's element deleted on every SSE frame (#176), an
/// author's markup rewritten into an SVG in the exported artifact (#177), a reviewer's typed note dropped with
/// no message (#178), an author's stylesheet counted into the offset frame a note is recorded against (#179).
/// A monotone read cannot be weaponised; a destructive write can.</para>
///
/// <para>Every assertion here is on behaviour a reviewer or an agent can observe — the markup that survives in
/// the page and in the ARTIFACT, the payload that reaches the drain, the sentence the reviewer is shown —
/// never on the presence of a guard.</para>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    /// <summary>
    /// An escape hatch whose body carries, deliberately, every name the SDK used to sweep the document for:
    /// the badge class, the rail class, the "this block is annotated" class, the count attribute, and the
    /// <c>data-charter-ui</c> chrome marker. Plus a <c>&lt;style&gt;</c> and a <c>&lt;script&gt;</c> ahead of
    /// the prose, so the offset frame a note is recorded against has something to be shifted BY (#179).
    ///
    /// <para>None of it is exotic: a plan that documents Charter, or pastes in a fragment of a page that
    /// happens to use the same words, produces exactly this.</para>
    /// </summary>
    private const string EscapeHatchIntegrityPlan =
        "# The escape hatch keeps what the author wrote\n\n" +
        "An ordinary paragraph, which is the block these tests take their notes on.\n\n" +
        ":::custom-html\n" +
        "<style>.author-styled { color: rgb(11, 22, 33); }</style>\n" +
        "<script>window.__charterAuthorScriptRan = true;</script>\n" +
        "<div class=\"charter-annotation-badge\">a badge the author wrote</div>\n" +
        "<div class=\"charter-badge-rail\">a rail the author wrote</div>\n" +
        "<p class=\"author-styled charter-has-annotations\">a paragraph the author marked</p>\n" +
        "<p data-charter-annotation-count=\"9\">a paragraph the author counted</p>\n" +
        "<span data-charter-ui=\"composer\">chrome the author named</span>\n" +
        "<form data-question-id=\"author-form\"><button type=\"submit\">Do the thing</button></form>\n" +
        "<p class=\"author-prose\">Vermilion telemetry pipelines answer to nobody.</p>\n" +
        ":::\n";

    /// <summary>The one sentence a reviewer selects in <see cref="EscapeHatchIntegrityPlan"/>.</summary>
    private const string AuthorProse = "Vermilion telemetry pipelines answer to nobody.";

    /// <summary>
    /// A real <c>:::diagram</c> — which is what makes the Mermaid runtime get inlined at all — beside an
    /// escape hatch holding markup that merely LOOKS like one. The conditionality is the reason #177 reads as
    /// intermittent: the author's block is rewritten only when some unrelated block elsewhere in the plan
    /// happens to be a diagram.
    /// </summary>
    private const string ForgedDiagramPlan =
        "# A real diagram, and one the author only wrote about\n\n" +
        ":::custom-html\n" +
        "<pre class=\"mermaid\" id=\"forged-pre\">graph TD; Forged--&gt;Node;</pre>\n" +
        "<div class=\"mermaid\">graph TD; AlsoForged--&gt;Node;</div>\n" +
        "<pre class=\"mermaid\" id=\"forged-svg\"><svg viewBox=\"0 0 2000 200\" width=\"120\" height=\"12\">" +
        "<rect width=\"2000\" height=\"200\"></rect></svg></pre>\n" +
        ":::\n\n" +
        ":::diagram\n" +
        "graph TD; Real-->Rendered;\n" +
        ":::\n";

    /// <summary>The literal text the author wrote inside the forged <c>&lt;pre&gt;</c>, after HTML parsing.</summary>
    private const string ForgedDiagramSource = "graph TD; Forged-->Node;";

    /// <summary>The same, for the <c>&lt;div class="mermaid"&gt;</c> — Mermaid's default selector is the
    /// CLASS, so the tag it sits on is not what saves it.</summary>
    private const string ForgedDiagramDivSource = "graph TD; AlsoForged-->Node;";

    /// <summary>
    /// A heading and one ordinary paragraph. Deliberately NOT the escape-hatch fixture: that body forges
    /// <c>data-charter-ui="composer"</c>, and this suite's own <c>Ui()</c> helper is an ATTRIBUTE selector, so
    /// <c>AssertNoComposerForAsync</c> would find the author's span and report a composer that was never
    /// opened. The same defect these tests are about, one layer up — worth knowing before adding a fixture
    /// that forges a name the harness reads.
    /// </summary>
    private const string PlainNotePlan =
        "# A note with nowhere to go\n\n" +
        "An ordinary paragraph, which is the only annotatable block in this plan.\n";

    /// <summary>One question and one ordinary paragraph — the two ends of the Alt+click gesture (#178).</summary>
    private const string UnanchorableGesturePlan =
        "# A block that cannot take a note\n\n" +
        "An ordinary paragraph, which can.\n\n" +
        ":::question\n" +
        "{\"id\":\"q-store\",\"title\":\"Which store?\",\"mode\":\"single\",\"target\":\"human\"," +
        "\"options\":[\"Postgres\",\"DynamoDB\"]}\n" +
        ":::\n";

    // ---- #176: the SDK undoes what it did, not what merely LOOKS like what it did -------------------------

    /// <summary>
    /// Charter #176. <c>clearMarkers</c> swept the whole document for <c>.charter-annotation-badge</c>,
    /// <c>.charter-badge-rail</c>, <c>.charter-has-annotations</c> and <c>[data-charter-annotation-count]</c>
    /// and destroyed every match — so an escape hatch carrying any of those names had its element DELETED, or
    /// its class and attribute stripped, on every single marker pass. <c>render()</c> runs on every SSE frame,
    /// so a teammate's note arriving by pull was enough to do it.
    ///
    /// <para><b>The control is the EXPORTED artifact, not a second served page.</b> The sweep ran
    /// unconditionally — <c>renderMarkers</c> calls <c>clearMarkers</c> first, even with nothing to mark — so
    /// an unannotated served page lost the author's element too, and comparing served-to-served would have
    /// agreed perfectly while both were wrong. The artifact carries no SDK by construction (invariant 1), and
    /// it is parsed by the same browser, so it is the only reading of "what the author wrote" that this suite
    /// can trust. That is also the answer to whether this test belonged in
    /// <c>AnnotatedLayoutRegressionGateTests</c>: it did not — every comparison that file makes is
    /// annotated-page against served control page, and this defect is invisible to all of them.</para>
    ///
    /// <para>Read at three moments — on load, after several real marker passes, and after
    /// <c>dispose()</c> — because the sweep runs at each of them.</para>
    /// </summary>
    [SkippableFact]
    public async Task The_escape_hatch_keeps_author_markup_that_carries_Charters_own_marker_names()
    {
        var planPath = NewPlanPath("escape-hatch-integrity");
        await File.WriteAllTextAsync(planPath, EscapeHatchIntegrityPlan);
        var exportPath = Path.ChangeExtension(planPath, ".export.html");
        await File.WriteAllTextAsync(
            exportPath, ArtifactExporter.Export(EscapeHatchIntegrityPlan, Path.GetDirectoryName(planPath)!));

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        try
        {
            var launched = await TryLaunchAsync();
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;

            // ---- the control: the author's body as the renderer emitted it, with no SDK anywhere ----
            var control = await NewInstrumentedPageAsync(launched);
            await control.Page.GotoAsync(
                new Uri(exportPath).AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            Assert.False(
                await control.Page.EvaluateAsync<bool>("() => typeof window.CharterAnnotate !== 'undefined'"),
                "the exported artifact must ship WITHOUT the annotation SDK");
            var authored = await HatchBodyAsync(control.Page);

            // The fixture has to actually carry the names, or every comparison below is vacuous.
            foreach (var name in new[]
                     {
                         "charter-annotation-badge", "charter-badge-rail", "charter-has-annotations",
                         "data-charter-annotation-count", "data-charter-ui", "data-question-id",
                     })
            {
                Assert.Contains(name, authored, StringComparison.Ordinal);
            }

            // ---- the served page, at the three moments the sweep runs ----
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;
            await page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(page, "ready");

            Assert.Equal(authored, await HatchBodyAsync(page));

            var paragraph = await AnchorIdAsync(page, "body > p");
            Assert.False(string.IsNullOrEmpty(paragraph), "the fixture's prose paragraph carries no anchor");
            await SeedNotesAsync(page, paragraph, 3, "a note that forces a marker pass");

            // The premise: the SDK really did paint its own chrome, so clearMarkers really did run — asserted
            // on an element the SDK OWNS, which the author's forged badge cannot be mistaken for.
            Assert.True(
                await page.Locator(Ui("badge")).CountAsync() > 0,
                "no SDK badge was painted, so the marker sweep this test is about never ran");
            Assert.Equal(authored, await HatchBodyAsync(page));

            await page.EvaluateAsync("() => { window.CharterAnnotate.dispose(); return null; }");
            Assert.Equal(0, await page.Locator(Ui("badge")).CountAsync());
            Assert.Equal(authored, await HatchBodyAsync(page));

            AssertNoBrowserErrors(control);
            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
            Cleanup(exportPath);
        }
    }

    // ---- #177: mermaid rewrites Charter's diagrams, and nothing else --------------------------------------

    /// <summary>
    /// Charter #177, and invariant 1. The inlined bootstrap called <c>mermaid.run()</c> with no argument, and
    /// Mermaid's default selector is <c>.mermaid</c> document-wide — so an escape hatch containing a
    /// <c>&lt;pre class="mermaid"&gt;</c> (a plan DOCUMENTING Mermaid, most obviously) had that element
    /// replaced by a rendered SVG. Not only in the review page: the runtime is inlined into the exported
    /// artifact too, so the saved file rewrote itself the moment anybody opened it.
    ///
    /// <para><b>The artifact is the assertion that matters</b>, so it is made twice — once on the served page
    /// and once on the file, each after Mermaid has demonstrably finished with the REAL diagram. Without that
    /// wait the test would pass against a page where Mermaid never ran at all.</para>
    /// </summary>
    [SkippableFact]
    public async Task Mermaid_renders_the_plans_own_diagram_and_leaves_the_escape_hatch_alone()
    {
        var planPath = NewPlanPath("forged-diagram");
        await File.WriteAllTextAsync(planPath, ForgedDiagramPlan);
        var exportPath = Path.ChangeExtension(planPath, ".export.html");
        await File.WriteAllTextAsync(
            exportPath, ArtifactExporter.Export(ForgedDiagramPlan, Path.GetDirectoryName(planPath)!));

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        try
        {
            var launched = await TryLaunchAsync();
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;

            var diagramId = BlockDocument.Parse(ForgedDiagramPlan).Blocks
                .First(b => b.Kind == BlockKind.Diagram).Id;

            var served = await NewInstrumentedPageAsync(launched);
            await served.Page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(served.Page, "ready");
            await AssertOnlyTheRealDiagramWasRenderedAsync(served.Page, diagramId);
            await AssertTheSdkGaveTheAuthorNoDiagramChromeAsync(served.Page);

            var artifact = await NewInstrumentedPageAsync(launched);
            await artifact.Page.GotoAsync(
                new Uri(exportPath).AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            Assert.False(
                await artifact.Page.EvaluateAsync<bool>("() => typeof window.CharterAnnotate !== 'undefined'"),
                "the exported artifact must ship WITHOUT the annotation SDK");
            await AssertOnlyTheRealDiagramWasRenderedAsync(artifact.Page, diagramId);

            AssertNoBrowserErrors(served);
            AssertNoBrowserErrors(artifact);
        }
        finally
        {
            Cleanup(planPath);
            Cleanup(exportPath);
        }
    }

    /// <summary>
    /// Mermaid finished with the block Charter authored, and touched neither of the two the author wrote. The
    /// wait on the real diagram is what stops this being satisfied by a page where Mermaid never ran.
    /// </summary>
    private static async Task AssertOnlyTheRealDiagramWasRenderedAsync(IPage page, string diagramId)
    {
        await page.WaitForSelectorAsync("#" + diagramId + " svg");

        // The two source-text forgeries still read as the source text the author typed.
        Assert.Equal(0, await page.Locator("#forged-pre svg").CountAsync());
        Assert.Equal(
            ForgedDiagramSource,
            (await page.Locator("#forged-pre").InnerTextAsync()).Trim());
        Assert.Equal(0, await page.Locator(".custom-html div.mermaid svg").CountAsync());
        Assert.Equal(
            ForgedDiagramDivSource,
            (await page.Locator(".custom-html div.mermaid").InnerTextAsync()).Trim());

        // And the third, which already holds an <svg> of the author's own: still THEIR svg, not a rendering
        // of it. Mermaid replaces the element's contents outright, so a rewritten block would carry a fresh
        // viewBox and the <g> groups Mermaid draws with.
        Assert.Equal(1, await page.Locator("#forged-svg > svg").CountAsync());
        Assert.Equal(0, await page.Locator("#forged-svg svg g").CountAsync());
        Assert.Equal("0 0 2000 200", await page.Locator("#forged-svg > svg").GetAttributeAsync("viewBox"));

        // Mermaid stamps data-processed on everything it has consumed; the author's blocks must carry none.
        Assert.Equal(
            0, await page.Locator(".custom-html [data-processed], .custom-html[data-processed]").CountAsync());
    }

    /// <summary>
    /// The SDK's own half of the same coupling. <c>scanDiagrams</c> also selects <c>pre.mermaid</c>
    /// document-wide, so an author who writes one containing an <c>&lt;svg&gt;</c> — which the fixture's
    /// third forged block does, at a viewBox far wider than its rendered width — had #51's review-time
    /// affordance injected into their markup: a zoom bar element, a tab stop, a <c>role</c>, an
    /// <c>aria-label</c>, and an <c>&lt;svg&gt;</c> whose width the SDK then rewrites on every zoom step.
    ///
    /// <para>Asserted on the SDK's own <c>diagram-zoomable</c> event as well as on the DOM, because the event
    /// is what fires whether or not the chrome happens to be visible — and over a settle window, since the
    /// activation is driven by a MutationObserver rather than by the load.</para>
    /// </summary>
    private static async Task AssertTheSdkGaveTheAuthorNoDiagramChromeAsync(IPage page)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(750);
        while (DateTime.UtcNow < deadline)
        {
            Assert.Equal(0, await CountEventsAsync(page, "diagram-zoomable"));
            Assert.Equal(0, await page.Locator("#forged-svg [data-charter-ui]").CountAsync());
            Assert.Equal(0, await page.Locator("#forged-svg.charter-zoomable").CountAsync());
            Assert.Equal(0, await page.Locator("#forged-svg[tabindex], #forged-svg[role]").CountAsync());
            await Task.Delay(100);
        }
    }

    // ---- #178: a gesture that produces nothing must say so -----------------------------------------------

    /// <summary>
    /// Charter #178, first half. <c>submit()</c> bailed on a null <c>anchorId</c> by emitting an
    /// <c>error</c> over the postMessage boundary and returning — no <c>setStatus</c>, no panel, nothing a
    /// human sees. The reviewer had already typed the note by then: the composer opens BEFORE the anchor is
    /// checked, so the whole cost of the defect lands on work already done.
    ///
    /// <para><b>Second half, and it is what makes the first half a belt rather than the braces.</b>
    /// <c>textRangeAnchor</c> never asked <c>anchorIdOf(block)</c> anything, so it could hand the composer an
    /// anchor with no id at all. The mutation control below is exactly the state one refactor away: an element
    /// that satisfies <c>closestAnchored</c> (it carries <c>data-anchor</c>) and yields no id (the attribute is
    /// empty, and it has no <c>id</c>). The composer must not open over it — an anchor that cannot be recorded
    /// must be refused before the reviewer is invited to type into it, not after.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_note_that_cannot_be_anchored_is_refused_out_loud_and_never_silently_dropped()
    {
        var planPath = NewPlanPath("no-anchor-note");
        await File.WriteAllTextAsync(planPath, PlainNotePlan);

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        try
        {
            var launched = await TryLaunchAsync();
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;

            await page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(page, "ready");

            // ---- the reachable half: submit() is handed an anchor it cannot record ----
            await page.EvaluateAsync(
                "() => { window.CharterAnnotate.annotate(" +
                "  { kind: 'text-range', anchorId: null, quote: 'some prose'," +
                "    note: 'a note the reviewer typed and expected to keep' }); return null; }");

            await page.WaitForSelectorAsync(Ui("panel-status"), new PageWaitForSelectorOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 10_000,
            });
            var told = await page.Locator(Ui("panel-status")).InnerTextAsync();
            Assert.False(
                string.IsNullOrWhiteSpace(told),
                "the note was dropped and the reviewer was told nothing at all");
            Assert.Contains("not saved", told, StringComparison.OrdinalIgnoreCase);

            // And the loss is real, which is why silence was the defect: nothing reached the queue.
            var listed = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(0, listed.GetArrayLength());

            // ---- the latent half: an anchor with no id must never reach the composer ----
            //
            // The mutation control is exactly the state one refactor away: an element that SATISFIES
            // closestAnchored (it carries data-anchor) and yields no id (the attribute is empty and there is
            // no id). Before the fix the walk stopped there, textRangeAnchor built
            // `{ kind: 'text-range', anchorId: null }`, and the composer opened over a block whose note could
            // never be recorded.
            var paragraph = await AnchorIdAsync(page, "body > p");
            Assert.False(string.IsNullOrEmpty(paragraph), "the fixture's paragraph carries no anchor to remove");
            await page.EvaluateAsync(
                "() => { const p = document.querySelector('body > p');" +
                "  p.removeAttribute('id'); p.setAttribute('data-anchor', ''); return null; }");

            await SelectContentsAsync(page, "body > p");
            await AssertNoComposerForAsync(page, 700);

            // The control: the same gesture on the same paragraph, with its anchor put back, DOES open one —
            // so the refusal above is about the missing id and not about the selection never landing.
            await page.EvaluateAsync(
                "(id) => { const p = document.querySelector('body > p');" +
                "  p.removeAttribute('data-anchor'); p.id = id; return null; }",
                paragraph);
            await SelectContentsAsync(page, "body > p");
            await page.WaitForSelectorAsync(Ui("composer-input"));

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    /// <summary>
    /// Charter #178, the related half. A <c>:::question</c> is deliberately not annotatable — it is native
    /// controls, and a note competing with the answer the block exists to collect would be worse than no note
    /// — but Alt+click there simply RETURNED. A reviewer who makes the product's one annotation gesture on a
    /// visible block and gets no composer, no outline and no message has no way to tell a rule from a bug.
    ///
    /// <para>The house pattern is <c>renderItem</c>'s orphan handling, which #170 followed: state the neutral
    /// fact where the reviewer is already looking, rather than inventing a new on-page marker. So the panel
    /// opens and says why — and the ordinary paragraph beside it still annotates normally, which is what
    /// proves the refusal is scoped to the question rather than to the gesture.</para>
    /// </summary>
    [SkippableFact]
    public async Task Alt_click_on_a_question_explains_itself_instead_of_doing_nothing()
    {
        var planPath = NewPlanPath("question-alt-click");
        await File.WriteAllTextAsync(planPath, UnanchorableGesturePlan);

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        try
        {
            var launched = await TryLaunchAsync();
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;

            await page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(page, "ready");

            await page.Locator(Question("q-store") + " legend").ClickAsync(
                new LocatorClickOptions { Modifiers = new[] { KeyboardModifier.Alt } });

            await page.WaitForSelectorAsync(Ui("panel-status"), new PageWaitForSelectorOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 10_000,
            });
            var told = await page.Locator(Ui("panel-status")).InnerTextAsync();
            Assert.Contains("question", told, StringComparison.OrdinalIgnoreCase);

            // No composer: the refusal is real, it is only no longer mute.
            await AssertNoComposerForAsync(page, 500);

            // …and the gesture itself still works one block up, so this is a rule about the block.
            await AnnotateAsync(page.Locator("body > p").First, "The paragraph still takes a note.");
            var listed = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(1, listed.GetArrayLength());

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- #179: the offset frame is the text a human reads -------------------------------------------------

    /// <summary>
    /// Charter #179. <c>blockTextNodes</c> builds the reference frame a text-range annotation's
    /// <c>start</c>/<c>end</c> are recorded against by walking every descendant text node — and it counted
    /// <c>&lt;style&gt;</c> and <c>&lt;script&gt;</c> source. An escape hatch carrying inline CSS or JS
    /// therefore shifted every offset in the block by the length of that source, and the agent was handed a
    /// span pointing at the wrong text. The same walk also SKIPPED any subtree carrying
    /// <c>data-charter-ui</c> — which an author's body passes through verbatim — shifting the offsets the
    /// other way (the second half of #176).
    ///
    /// <para><b>Asserted as a round trip against an independent frame.</b> The reference is the EXPORTED
    /// artifact's own text, minus machinery, read out of a page with no SDK in it — so the three sides of the
    /// triangle come from three places: the quote from the browser's own <c>Selection</c>, the offsets from
    /// the SDK, the frame from the saved document. Slicing the frame with the offsets has to reproduce the
    /// quote, and the span has to sit exactly where the prose sits.</para>
    /// </summary>
    [SkippableFact]
    public async Task Offsets_recorded_in_a_block_carrying_inline_CSS_index_the_text_a_human_reads()
    {
        var planPath = NewPlanPath("offset-frame");
        await File.WriteAllTextAsync(planPath, EscapeHatchIntegrityPlan);
        var exportPath = Path.ChangeExtension(planPath, ".export.html");
        await File.WriteAllTextAsync(
            exportPath, ArtifactExporter.Export(EscapeHatchIntegrityPlan, Path.GetDirectoryName(planPath)!));

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        try
        {
            var launched = await TryLaunchAsync();
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;

            // The frame: the saved artifact's own block text, machinery excluded. No SDK is loaded in this
            // page at all, so nothing about it can echo the code under test.
            var control = await NewInstrumentedPageAsync(launched);
            await control.Page.GotoAsync(
                new Uri(exportPath).AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            var frame = await ReadableBlockTextAsync(control.Page);

            Assert.Contains(AuthorProse, frame, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "rgb(11, 22, 33)", frame,
                StringComparison.Ordinal);

            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;
            await page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(page, "ready");

            await SelectContentsAsync(page, ".author-prose");
            await page.WaitForSelectorAsync(Ui("composer-input"));
            await page.FillAsync(Ui("composer-input"), "This sentence is the one I mean.");
            await SaveComposerAsync(page);

            var drained = await DrainAllAsync(server.Address, session.Key.Value, 1);
            var note = drained[0];
            Assert.Equal("text-range", note.GetProperty("kind").GetString());

            var start = note.GetProperty("start").GetInt32();
            var end = note.GetProperty("end").GetInt32();
            var quote = note.GetProperty("quote").GetString() ?? string.Empty;

            Assert.True(end > start, $"end must follow start; got start={start}, end={end}");
            Assert.InRange(end, 0, frame.Length);
            Assert.Equal(Normalize(quote), Normalize(frame[start..end]));
            Assert.Equal(frame.IndexOf(AuthorProse, StringComparison.Ordinal), start);

            AssertNoBrowserErrors(control);
            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
            Cleanup(exportPath);
        }
    }

    // ---- shared probes -----------------------------------------------------------------------------------

    /// <summary>
    /// The escape hatch's body exactly as the browser parsed it. Read off <c>.custom-html-scroll</c> rather
    /// than the anchored <c>div.custom-html</c> so that the SDK's own count badge — which legitimately hangs
    /// off the block — is outside the reading and only the AUTHOR's markup is compared.
    /// </summary>
    private static Task<string> HatchBodyAsync(IPage page)
        => page.EvaluateAsync<string>(
            "() => { const el = document.querySelector('.custom-html-scroll');" +
            "  return el ? el.innerHTML : '(no escape hatch in this document)'; }");

    /// <summary>
    /// The block's text as a human reads it: every text node in <c>div.custom-html</c>, with
    /// <c>&lt;style&gt;</c> and <c>&lt;script&gt;</c> subtrees left out because their contents are source, not
    /// words. Computed here, over a document with no SDK in it, so it is a genuinely independent frame.
    /// </summary>
    private static Task<string> ReadableBlockTextAsync(IPage page)
        => page.EvaluateAsync<string>(
            "() => {" +
            "  const out = [];" +
            "  (function walk(n) {" +
            "    if (n.nodeType === 1) {" +
            "      const tag = String(n.tagName || '').toUpperCase();" +
            "      if (tag === 'STYLE' || tag === 'SCRIPT') return;" +
            "      for (const c of n.childNodes) walk(c);" +
            "      return;" +
            "    }" +
            "    if (n.nodeType === 3) out.push(n.nodeValue || '');" +
            "  })(document.querySelector('div.custom-html'));" +
            "  return out.join('');" +
            "}");

    /// <summary>
    /// Select everything inside <paramref name="selector"/> and release the mouse, which is the gesture the
    /// SDK's text-range path listens for.
    /// </summary>
    private static Task SelectContentsAsync(IPage page, string selector)
        => page.EvaluateAsync(
            "(sel) => {" +
            "  const el = document.querySelector(sel);" +
            "  const range = document.createRange();" +
            "  range.selectNodeContents(el);" +
            "  const s = window.getSelection();" +
            "  s.removeAllRanges(); s.addRange(range);" +
            "  el.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));" +
            "  return null;" +
            "}",
            selector);
}
