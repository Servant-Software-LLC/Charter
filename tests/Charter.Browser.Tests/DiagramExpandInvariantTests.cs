using System.Net;
using System.Text.Json;
using Charter.Core;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #234 — the things a reviewer ALREADY HAS, which must keep working once one diagram fills the
/// viewport. Written RED, alongside the affordance suite in <c>DiagramExpandAffordanceTests.cs</c>.
///
/// <para><b>Why this file exists separately from the affordance one.</b> The affordance suite asks "can the
/// reviewer get into expand mode, and does it look right when they do". This one asks the question that
/// actually decides whether the feature ships: <i>is the expanded view still Charter?</i> Expand mode is a
/// second layout for a block that is simultaneously a #51 pan/zoom scroll container, a #48 annotation target,
/// and a thing <c>render()</c> sweeps chrome across whenever any teammate saves a note. Every one of those is
/// a capability the reviewer had a moment ago and would silently lose to a <c>position: fixed</c> that was
/// only ever tested empty and idle.</para>
///
/// <para><b>What each test refuses to settle for</b>, each because the weaker form is green while the defect
/// is fully present:</para>
/// <list type="bullet">
///   <item>The annotation test asserts the POSTED PAYLOAD — the record that reached the server carries THAT
///     diagram node's identity (#48). "A composer opened" proves nothing: a whole-diagram click opens the
///     same composer, and the decay from a node note into a block note is exactly #48's original failure. It
///     is silent from the DOM's side, which is why it is asserted from the server's.</item>
///   <item>The re-entrancy tests cause the render THE WAY THE PRODUCT CAUSES IT — a second author's record
///     landing in <c>&lt;plan&gt;.review/</c> while the server is running, exactly as a <c>git pull</c>
///     delivers it. Calling <c>render()</c> or <c>renderMarkers()</c> from the test would assert the browser,
///     not the product.</item>
///   <item>The anti-steal test is not a duplicate of the survival test. "Always restore focus after a render"
///     passes everything, including the repair firing when nothing was taken — which #168 left panel focus
///     opt-IN to avoid and #200 wrote a whole test for. So focus is put somewhere the render does not rebuild
///     (a half-typed composer) and the claim is that a teammate's note moved NOTHING.</item>
/// </list>
///
/// <para><b>Traps this file refuses, each already paid for once in this repo.</b> No
/// <c>WaitForFunctionAsync</c> — the served page's CSP has no <c>'unsafe-eval'</c>, so its polling predicate
/// throws the moment it genuinely has to wait, and only ever appears to work when the condition was already
/// true. No second <c>WaitForEventAsync</c> in one test: it asks "has this EVER happened", so the second call
/// returns instantly. Every measurement is one synchronous in-page evaluation taken AFTER the last
/// <c>await</c>, because <c>renderMarkers</c> opens with <c>clearMarkers()</c> and a handle captured before an
/// <c>await</c> can be detached — it reports a 0x0 rect at the origin and an EMPTY computed style, which is
/// how detachment is told apart from <c>display: none</c>, which keeps a real one. And the two tests that
/// measure a scroll affordance launch with <see cref="TryLaunchAsync"/>'s <c>showScrollbars</c>, because
/// Playwright passes <c>--hide-scrollbars</c> to headless Chromium and the measurement would otherwise
/// measure the flag (#68's lesson).</para>
///
/// <para><b>Every test here is expected to FAIL until the sibling implementation task lands.</b> None of these
/// invariants can hold for a state the page cannot enter: each one dies where it tries to open the expanded
/// view, which is a genuine red and a red for the right reason.</para>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    /// <summary>
    /// The half-typed note the anti-steal test leaves in the composer. Fifteen characters, so the caret
    /// position it is read back with is a number this file states rather than one it computes from the same
    /// string it is checking.
    /// </summary>
    private const string HalfTypedNote = "half a sentence";

    // ---- #51's pan/zoom, inside the expanded view ----------------------------------------------------------

    /// <summary>
    /// Zoom still works while the diagram is expanded — and "works" means the diagram gets BIGGER.
    ///
    /// <para>This is not the ceremony it looks like. <c>activateDiagram</c> measures <c>view.baseWidth</c> ONCE,
    /// when the block is still bound by the content column, and <c>applyZoom</c> writes
    /// <c>svg.style.width = baseWidth * scale</c> — an absolute pixel width. Expanding widens the box without
    /// touching that number, so an implementation that leaves the measurement stale makes the reviewer's first
    /// press of <c>+</c> render the diagram NARROWER than the expanded view was already showing it. Every
    /// individual piece keeps working; the gesture means the opposite of what it says. That is why the growth
    /// is measured against the fit width INSIDE the expanded view rather than against the resting one.</para>
    ///
    /// <para>All three routes #51 gave the reviewer are walked, because they run through different handlers and
    /// a fix in one proves nothing about the others: the bar's <c>+</c> button, <c>Ctrl+scroll</c> (a
    /// <c>wheel</c> listener on the block, which anything painted over the expanded view would swallow), and
    /// <c>Reset</c>. And the view is asserted to be still expanded after each of them — a zoom that quietly
    /// collapsed the diagram back into the column would satisfy every width assertion here on the way down.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "DiagramExpandInvariants")]
    public async Task Zoom_still_works_while_the_diagram_is_expanded()
    {
        var planPath = NewPlanPath("diagram-expand-zoom");
        await File.WriteAllTextAsync(planPath, PanZoomDiagramPlan);

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

            await OpenExpandGateAsync(page, server, session);

            var inColumn = await ExpandProbeAsync(page, Oversized);
            Assert.Equal("100%", await page.InnerTextAsync(Ui("diagram-zoom-level")));

            var expanded = await ExpandAsync(page, Oversized);
            Assert.True(
                expanded.Width > inColumn.Width + 100,
                "this test proves nothing unless the diagram really expanded first (" + Round(inColumn.Width) +
                    "px -> " + Round(expanded.Width) + "px): " + expanded);

            // The level the reviewer is looking at when they enter the view. Whether expanding resets the zoom
            // or carries it in is the implementation's call; nothing was zoomed before it, so either answer is
            // 100% and the two clicks below start from a state this test has stated rather than assumed.
            Assert.Equal("100%", await page.InnerTextAsync(Ui("diagram-zoom-level")));

            // The fit width INSIDE the expanded view — the number a zoom has to beat, and the one an
            // implementation with a stale baseWidth silently undercuts.
            var fitExpanded = (await DiagramProbeAsync(page, Oversized))
                .GetProperty("renderedWidth").GetDouble();

            // ---- the bar's own + button ----
            await page.ClickAsync(Ui("diagram-zoom-in"));
            await page.ClickAsync(Ui("diagram-zoom-in"));
            Assert.Equal("156%", await page.InnerTextAsync(Ui("diagram-zoom-level")));

            var zoomed = await DiagramProbeAsync(page, Oversized);
            var zoomedWidth = zoomed.GetProperty("renderedWidth").GetDouble();
            Assert.True(
                zoomedWidth > fitExpanded + 100,
                "Charter #234 — two presses of '+' took the diagram from " + Round(fitExpanded) + "px to " +
                    Round(zoomedWidth) + "px inside the expanded view, so zooming in did not make it bigger. " +
                    "applyZoom writes an ABSOLUTE width of baseWidth x scale, and baseWidth was measured while " +
                    "the block was still bound by the content column — expanding has to re-measure it or the " +
                    "reviewer's first zoom shrinks the diagram they opened the view to read: " + zoomed);
            // Widened, never transformed: a CSS transform would rasterize the label text this whole feature
            // exists to make readable, and would move every rect the annotation overlay is painted from.
            Assert.Equal("none", zoomed.GetProperty("svgTransform").GetString());

            var afterZoom = await ExpandProbeAsync(page, Oversized);
            Assert.True(
                afterZoom.IsExpanded,
                "Charter #234 — zooming in collapsed the expanded view. The reviewer expanded the diagram in " +
                    "order to zoom it; a zoom that puts them back in the column has undone the thing it was " +
                    "asked to help with: " + afterZoom);

            // ---- Ctrl+scroll, which is a wheel listener on the block and not a button ----
            await WheelOverDiagramAsync(page, Oversized, -240, control: true);
            var wheeled = await page.InnerTextAsync(Ui("diagram-zoom-level"));
            Assert.True(
                !string.Equals(wheeled, "156%", StringComparison.Ordinal),
                "Charter #234 — Ctrl+scroll over the EXPANDED diagram did not change the zoom (still " +
                    wheeled + "). The gesture is a 'wheel' listener on the block itself, so anything the " +
                    "expanded view paints between the cursor and the block takes it away — and it is the one " +
                    "zoom route that needs no aiming at a 24px button.");

            // ---- and Reset returns the view to ITS fit, not to the column's ----
            await page.ClickAsync(Ui("diagram-zoom-reset"));
            Assert.Equal("100%", await page.InnerTextAsync(Ui("diagram-zoom-level")));

            var reset = await DiagramProbeAsync(page, Oversized);
            var resetWidth = reset.GetProperty("renderedWidth").GetDouble();
            Assert.True(
                Math.Abs(resetWidth - fitExpanded) <= 1,
                "Charter #234 — Reset inside the expanded view returned the diagram to " + Round(resetWidth) +
                    "px, not to the " + Round(fitExpanded) + "px it was fitting the expanded box at. Reset " +
                    "means 'fit the space I have', and the space the reviewer has is the viewport: " + reset);

            var afterReset = await ExpandProbeAsync(page, Oversized);
            Assert.True(
                afterReset.IsExpanded,
                "Charter #234 — Reset collapsed the expanded view. It resets the ZOOM; the view is left by " +
                    "the expand control or by Escape: " + afterReset);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    /// <summary>
    /// Pan still works while the diagram is expanded, in both directions, with a real press-drag-release.
    ///
    /// <para>The expanded view does not remove the reason panning exists: this fixture's diagram is 1826px of
    /// content, so even at the full width of a 1280px window it is still drawn smaller than Mermaid laid it
    /// out, and a reviewer who zooms to read the labels is immediately navigating a scroll region again. What
    /// the expanded view DOES change is every input assumption underneath the gesture — the block is out of
    /// flow, its rect is the viewport's, and <c>onDiagramPointerDown</c> refuses to engage at all unless
    /// <c>canPan</c> reads live overflow off it.</para>
    ///
    /// <para>So the test zooms until there is genuinely something to pan to, drags, and drags BACK. The return
    /// leg is what separates "panning works" from "one drag moved the scroll offset once": a pan that could
    /// only ever go one way would satisfy the first assertion and strand the reviewer at the far edge of a
    /// diagram they cannot get back across.</para>
    ///
    /// <para><c>showScrollbars: true</c> because everything here turns on <c>scrollWidth</c> vs
    /// <c>clientWidth</c>. Playwright passes <c>--hide-scrollbars</c> to headless Chromium, which forces every
    /// scrollbar to zero width and moves that comparison by ~15px in the direction that hides the failure.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "DiagramExpandInvariants")]
    public async Task Pan_still_works_while_the_diagram_is_expanded()
    {
        var planPath = NewPlanPath("diagram-expand-pan");
        await File.WriteAllTextAsync(planPath, PanZoomDiagramPlan);

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        try
        {
            var launched = await TryLaunchAsync(showScrollbars: true);
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;

            await OpenExpandGateAsync(page, server, session);
            await ExpandAsync(page, Oversized);

            // Zoomed only as far as it takes to make the expanded box a scroll region again — the premise the
            // pan gesture is gated on, asserted rather than assumed.
            await ZoomUntilPannableAsync(page, Oversized);

            // Zooming is focal, so it has already moved the scroll offset. Cleared, so the drag below is the
            // only thing that could have produced what is measured after it.
            await ScrollDiagramAsync(page, Oversized, 0, 0);

            var pansBefore = await CountEventsAsync(page, "diagram-panned");
            await DragOverDiagramAsync(page, Oversized, -220, 0);

            var panned = await DiagramProbeAsync(page, Oversized);
            var scrolledTo = panned.GetProperty("scrollLeft").GetDouble();
            Assert.True(
                scrolledTo > 0,
                "Charter #234 — a primary-button drag across the EXPANDED diagram did not pan it. The block " +
                    "is out of flow and viewport-sized now, and onDiagramPointerDown refuses to engage unless " +
                    "canPan() reads real overflow off it — so a fixed box whose overflow was styled away " +
                    "leaves the reviewer with a diagram they can see all of and navigate none of: " + panned);
            Assert.True(
                await CountEventsAsync(page, "diagram-panned") > pansBefore,
                "Charter #234 — the diagram's scroll offset moved but no 'diagram-panned' was emitted, so " +
                    "whatever scrolled it was not the SDK's pan gesture.");

            var midPan = await ExpandProbeAsync(page, Oversized);
            Assert.True(
                midPan.IsExpanded,
                "Charter #234 — panning collapsed the expanded view: " + midPan);

            // ---- and BACK, which is what makes it a pan rather than a one-way trip ----
            await DragOverDiagramAsync(page, Oversized, 220, 0);

            var returned = await DiagramProbeAsync(page, Oversized);
            var returnedTo = returned.GetProperty("scrollLeft").GetDouble();
            Assert.True(
                returnedTo <= 2,
                "Charter #234 — dragging back the same distance left the expanded diagram at " +
                    Round(returnedTo) + "px instead of returning it to its left edge (it had been panned to " +
                    Round(scrolledTo) + "px). A pan the reviewer cannot reverse strands them at the far side " +
                    "of the diagram they expanded to read: " + returned);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    /// <summary>
    /// The zoom bar stays pinned to the expanded diagram's corner while the reviewer pans it.
    ///
    /// <para>The bar is an <c>absolute</c>-positioned child of the block, so it scrolls WITH the content and is
    /// pushed back every frame by <c>pinDiagramChrome</c>, which writes <c>translate(scrollLeft, scrollTop)</c>
    /// onto it. Nothing about that survives on trust: it is a compensation computed from the block's own scroll
    /// offsets, and the expanded view changes what the block's box is. If it stops being applied, the only
    /// controls that leave the view — the expand toggle lives in this bar — ride off the edge of a diagram that
    /// is now covering the whole screen, and Escape is all the reviewer has left.</para>
    ///
    /// <para>Measured at TWO different pan positions, so the claim is "it did not drift" rather than "it is
    /// somewhere plausible" — and each position is asserted to be a real one, because a bar that stayed put
    /// over a diagram that never moved is not evidence of anything. The bar's viewport rect is checked too:
    /// pinned to a corner that is itself off-screen is the same loss wearing a passing relative
    /// measurement.</para>
    ///
    /// <para><b>Both readings are taken after a DRAG, and that is not incidental.</b> A drag re-pins
    /// synchronously, in the same turn as the scroll it causes (#113). A direct <c>scrollLeft</c> assignment
    /// does not: the compensation for it rides on the <c>scroll</c> event, which the spec dispatches at the
    /// next rendering opportunity, so a reading taken straight after one catches the bar still carrying the
    /// previous offset and reports a ~100px phantom drift. That is the test measuring its own setup, and it is
    /// how the first draft of this test failed.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "DiagramExpandInvariants")]
    public async Task The_zoom_bar_stays_pinned_while_panning_expanded()
    {
        var planPath = NewPlanPath("diagram-expand-bar-pinned");
        await File.WriteAllTextAsync(planPath, PanZoomDiagramPlan);

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        try
        {
            var launched = await TryLaunchAsync(showScrollbars: true);
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;

            await OpenExpandGateAsync(page, server, session);
            await ExpandAsync(page, Oversized);

            await ZoomUntilPannableAsync(page, Oversized);

            // ---- panned OUT ----
            await DragOverDiagramAsync(page, Oversized, -240, -80);
            var panned = await ZoomBarPinAsync(page, Oversized);

            Assert.True(
                panned.Present,
                "Charter #234 — the expanded diagram carries no zoom bar at all, so there is nothing to keep " +
                    "pinned and the reviewer has no controls inside the view: " + panned);
            Assert.True(
                panned.ScrollLeft > 0,
                "the drag did not pan the expanded diagram, so a bar that stayed put proves nothing: " + panned);
            Assert.True(
                panned.InsideBlock,
                "Charter #234 — the zoom bar is no longer inside the diagram it belongs to: " + panned);
            Assert.True(
                panned.OffsetLeft >= -1 && panned.OffsetLeft < 40
                    && panned.OffsetTop >= -1 && panned.OffsetTop < 40,
                "Charter #234 — the zoom bar rode away with the content while the expanded diagram was " +
                    "panned: " + panned + "\nThe bar is an absolutely positioned child of a scroll container, " +
                    "so pinDiagramChrome has to push it back by the block's scroll offset every time that " +
                    "offset changes. The expand toggle lives in this bar: losing it inside a view that covers " +
                    "the screen leaves Escape as the only way out.");

            // Pinned to a corner that is itself off-screen is the same loss with a passing relative measure.
            Assert.True(
                panned.Left >= -1 && panned.Right <= panned.InnerWidth + 1
                    && panned.Top >= -1 && panned.Bottom <= panned.InnerHeight + 1,
                "Charter #234 — the zoom bar is pinned to the block's corner but sits outside the " +
                    Round(panned.InnerWidth) + "x" + Round(panned.InnerHeight) + " viewport the expanded " +
                    "diagram is supposed to be filling: " + panned);

            // ---- and panned BACK, which changes the offset the compensation is computed from ----
            await DragOverDiagramAsync(page, Oversized, 240, 80);
            var returned = await ZoomBarPinAsync(page, Oversized);

            Assert.True(
                returned.ScrollLeft < panned.ScrollLeft,
                "the second drag did not move the expanded diagram, so the two readings compared below are " +
                    "of the same pan position and the comparison is vacuous:\n  out  " + panned +
                    "\n  back " + returned);
            Assert.True(
                Math.Abs(returned.OffsetLeft - panned.OffsetLeft) <= 1
                    && Math.Abs(returned.OffsetTop - panned.OffsetTop) <= 1,
                "Charter #234 — the zoom bar sits in a different place at two different pan positions of the " +
                    "expanded diagram, so it is riding the content rather than staying pinned:\n  out  " +
                    panned + "\n  back " + returned);
            Assert.True(
                returned.OffsetLeft >= -1 && returned.OffsetLeft < 40
                    && returned.OffsetTop >= -1 && returned.OffsetTop < 40,
                "Charter #234 — panning back left the zoom bar outside the expanded diagram's top-left " +
                    "corner: " + returned);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- #48's anchoring, inside the expanded view ---------------------------------------------------------

    /// <summary>
    /// Charter #48, through the back door: Alt+clicking a node while the diagram is expanded still posts a
    /// <c>diagram-node</c> record carrying THAT node's identity.
    ///
    /// <para><b>Asserted on the POSTED PAYLOAD, and that is the whole point of the test.</b> A composer opening
    /// proves nothing — a whole-diagram click opens the same composer with the same input — so a node note
    /// that decayed into a block note is invisible from the DOM's side. It is #48's original failure exactly:
    /// the node's identity is dropped, the anchor walk stops somewhere the source map cannot read, and the
    /// reviewer's note reaches the agent pointing at the wrong thing (or at nothing). It fails silently, and it
    /// is one plausible mistake away — <c>setPointerCapture</c> during a pan retargets the compatibility click
    /// at the captured element, which is why the SDK deliberately does not use it, and a new pointer path added
    /// for the expanded view is the obvious place to reintroduce that.</para>
    ///
    /// <para>The click is a real mouse click at a point computed from the LIVE expanded layout, never a locator
    /// click — a locator click scrolls its target into view and would move the very geometry under test. And
    /// the fixture is checked to hold more than one node first, because "the posted nodeId is a node" is not
    /// the claim; "it is the node the reviewer clicked" is, and that is only discriminating where there was
    /// something else it could have been.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "DiagramExpandInvariants")]
    public async Task Annotating_a_node_while_expanded_posts_that_nodes_anchor()
    {
        const string note = "this node needs an explicit failure path";

        var planPath = NewPlanPath("diagram-expand-node-anchor");
        await File.WriteAllTextAsync(planPath, PanZoomDiagramPlan);

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

            await OpenExpandGateAsync(page, server, session);

            var blockId = await page.EvaluateAsync<string>(
                "() => document.querySelectorAll('pre.mermaid')[0].id");
            Assert.False(
                string.IsNullOrEmpty(blockId),
                "the renderer must stamp a stable block id on pre.mermaid, or nothing can anchor to it");

            var nodeCount = await page.Locator("pre.mermaid").Nth(Oversized).Locator("g.node").CountAsync();
            Assert.True(
                nodeCount > 1,
                "fixture drift: the oversized diagram renders " + nodeCount + " node(s), so 'the record " +
                    "carries THAT node' is not a discriminating claim — there is nothing else it could have " +
                    "been.");

            await ExpandAsync(page, Oversized);

            // A point on a node that is genuinely visible inside the expanded box right now, taken from the
            // live layout. A real mouse click, because a locator click scrolls its target into view and would
            // move the geometry this test is about.
            var node = await VisibleDiagramNodePointAsync(page, Oversized);
            await page.Keyboard.DownAsync("Alt");
            await page.Mouse.ClickAsync(node.X, node.Y);
            await page.Keyboard.UpAsync("Alt");
            await page.WaitForSelectorAsync(
                Ui("composer-input"), new PageWaitForSelectorOptions { Timeout = ReadinessTimeoutMs });

            var context = await page.InnerTextAsync(Ui("composer-context"));
            Assert.True(
                context.Contains("diagram node", StringComparison.OrdinalIgnoreCase),
                "Charter #234/#48 — Alt+clicking a NODE inside the expanded view opened the composer reading " +
                    "'" + context + "'. The reviewer is being told they are annotating something other than " +
                    "the node they aimed at.");

            await page.FillAsync(Ui("composer-input"), note);
            await SaveComposerAsync(page);

            // ---- what actually reached the server ----
            var listed = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(1, listed.GetArrayLength());

            var posted = FindByNote(listed, note);
            Assert.Equal("diagram-node", posted.GetProperty("kind").GetString());
            Assert.Equal(blockId, posted.GetProperty("anchorId").GetString());

            var postedNodeId = posted.GetProperty("nodeId").GetString();
            Assert.True(
                string.Equals(postedNodeId, node.NodeId, StringComparison.Ordinal),
                "Charter #234/#48 — the record that reached the server names node '" + postedNodeId +
                    "' but the reviewer clicked '" + node.NodeId + "' inside the expanded view. The agent is " +
                    "handed a note pointed at a different part of the diagram than the one the human was " +
                    "looking at, and nothing in the browser says so.");
            AssertMapsToThePanZoomDiagram(posted);

            // ...and that id still names a real node of THIS fixture, so the field carries an identity rather
            // than a string that merely round-tripped. Compared with whitespace removed, because Mermaid wraps
            // a long label across tspans and the plan's source does not.
            var label = await DiagramNodeLabelAsync(page, node.NodeId);
            Assert.False(
                string.IsNullOrWhiteSpace(label),
                "fixture drift: the clicked node '" + node.NodeId + "' carries no label text, so the posted " +
                    "nodeId cannot be tied back to anything the reviewer could see.");
            Assert.Contains(
                WithoutWhitespace(label), WithoutWhitespace(PanZoomDiagramPlan), StringComparison.Ordinal);

            // The save drove a render. The reviewer must still be in the view they were annotating from.
            var stillExpanded = await ExpandProbeAsync(page, Oversized);
            Assert.True(
                stillExpanded.IsExpanded,
                "Charter #234 — saving a note from inside the expanded view collapsed it, so annotating a " +
                    "diagram costs the reviewer the view they were reading it in — once per note: " +
                    stillExpanded);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- render() re-entrancy, inside the expanded view ----------------------------------------------------

    /// <summary>
    /// A teammate's record arriving over the review log rebuilds the page's chrome, and the expanded view has
    /// to be exactly where the reviewer left it afterwards.
    ///
    /// <para><b>The render is caused the way the product causes it</b> — a second author's record landing in
    /// <c>&lt;plan&gt;.review/</c> while the server is running, exactly as a <c>git pull</c> delivers it.
    /// Calling <c>render()</c> from the test would assert the browser rather than the product. And because
    /// <c>render()</c> completes in ONE synchronous turn (#198's rule: no frame may be painted without a
    /// badge), a badge that has appeared is proof the decision about the expanded view has already been
    /// made — there is no further wait that would be either necessary or trustworthy.</para>
    ///
    /// <para>What is compared is the whole reviewing STATE, not merely the class: the box, its position, its
    /// parent, the zoom level and the scroll offset. A reviewer who has zoomed to 156% and panned to a
    /// subgraph is in a place they navigated to; <c>renderMarkers</c> ends with <c>pinDiagramChrome</c> over
    /// every diagram, so a rebuild that reset the view would be a plausible tidy-up and it would throw them
    /// back to the top-left every time a colleague saved a note. The parent is asserted because a reparent
    /// changes ancestry — and with it anchors and the source map — while every geometric check here still
    /// passes.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "DiagramExpandInvariants")]
    public async Task The_expanded_view_survives_a_render_from_a_teammate_record()
    {
        var directory = NewPlanDirectory("diagram-expand-rerender");
        var planPath = Path.Combine(directory, "expand-rerender.charter.md");
        await File.WriteAllTextAsync(planPath, PanZoomDiagramPlan);

        var mine = new ReviewLogWriter(planPath, new ReviewAuthor("Alice Ng", "alice@example.com"));
        var teammate = new ReviewLogWriter(planPath, new ReviewAuthor("Bob Chen", "bob@example.com"));

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(session, new ReviewServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            ReviewLog = mine,
        });

        try
        {
            var launched = await TryLaunchAsync(showScrollbars: true);
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;

            await OpenExpandGateAsync(page, server, session);
            await WaitForEventAsync(page, "review-log-loaded");

            var paragraph = await AnchorIdAsync(page, "body > p:nth-of-type(1)");
            Assert.False(
                string.IsNullOrEmpty(paragraph),
                "fixture/renderer drift: the prose above the diagram carries no anchor, so the teammate has " +
                    "nothing to attach a record to and no render can be caused.");

            await ExpandAsync(page, Oversized);
            await ZoomUntilPannableAsync(page, Oversized);
            await ScrollDiagramAsync(page, Oversized, 0, 0);
            await DragOverDiagramAsync(page, Oversized, -200, 0);

            var before = await ExpandProbeAsync(page, Oversized);
            var beforeLevel = await page.InnerTextAsync(Ui("diagram-zoom-level"));
            var beforeScroll = (await DiagramProbeAsync(page, Oversized))
                .GetProperty("scrollLeft").GetDouble();

            // The premises, so "it survived" is a claim about a state that was really reached.
            Assert.True(before.IsExpanded, "the diagram is not expanded, so nothing here is about #234");
            Assert.True(
                !string.Equals(beforeLevel, "100%", StringComparison.Ordinal),
                "the diagram was never zoomed, so 'the zoom level survived' would be true of a page where " +
                    "nothing had happened (level reads " + beforeLevel + ")");
            Assert.True(
                beforeScroll > 0,
                "the diagram was never panned, so 'the pan survived' would be vacuous (scrollLeft " +
                    Round(beforeScroll) + ")");

            var rendersBefore = await CountEventsAsync(page, "markers-rendered");

            // The real route: a second author's record in <plan>.review/, exactly as a git pull delivers it.
            teammate.AppendCreate(
                new ReviewAnchor(paragraph, "element", "an ordinary prose paragraph", null),
                "A teammate's note, landing while the reviewer is deep inside the expanded diagram.");
            await WaitForBadgeAsync(page, paragraph, teammate);

            Assert.True(
                await CountEventsAsync(page, "markers-rendered") > rendersBefore,
                "Charter #234 — the teammate's record never caused a marker pass, so nothing was rebuilt " +
                    "under the expanded view and this test would pass without asserting anything.");

            // Re-read from scratch: renderMarkers opens with clearMarkers(), so anything held across the yield
            // can be detached and would answer for a page the reviewer is no longer looking at (#198).
            var after = await ExpandProbeAsync(page, Oversized);
            var afterLevel = await page.InnerTextAsync(Ui("diagram-zoom-level"));
            var afterScroll = (await DiagramProbeAsync(page, Oversized))
                .GetProperty("scrollLeft").GetDouble();

            Assert.True(
                after.IsExpanded,
                "Charter #234 — a teammate's note collapsed the expanded view out from under the reviewer. " +
                    "render() destroys and rebuilds the SDK's chrome on every arriving record, and on a plan " +
                    "under active review that is a steady stream:\n  before " + before + "\n  after  " + after);
            Assert.Equal("fixed", after.Position);
            Assert.Equal(before.Parent, after.Parent);
            Assert.True(
                after.HasExpandControl,
                "Charter #234 — the rebuild left the expanded view with no expand control, so the reviewer " +
                    "cannot collapse it the way they opened it: " + after);

            Assert.True(
                Math.Abs(after.Width - before.Width) <= 1 && Math.Abs(after.Height - before.Height) <= 1,
                "Charter #234 — the teammate's note resized the expanded diagram:\n  before " + before +
                    "\n  after  " + after);
            Assert.True(
                Math.Abs(after.Left - before.Left) <= 1 && Math.Abs(after.Top - before.Top) <= 1,
                "Charter #234 — the teammate's note moved the expanded diagram:\n  before " + before +
                    "\n  after  " + after);

            Assert.True(
                string.Equals(afterLevel, beforeLevel, StringComparison.Ordinal),
                "Charter #234 — a teammate's note reset the diagram's zoom from " + beforeLevel + " to " +
                    afterLevel + ". The reviewer chose that magnification to read the labels with.");
            Assert.True(
                Math.Abs(afterScroll - beforeScroll) <= 1,
                "Charter #234 — a teammate's note put the expanded diagram back to " + Round(afterScroll) +
                    "px from the " + Round(beforeScroll) + "px the reviewer had panned it to, so every note " +
                    "a colleague saves throws them back to the top-left of the diagram they are reading.");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            CleanupDirectory(directory);
        }
    }

    /// <summary>
    /// Charter #200's other half, inside the expanded view: a render must move focus it did not take.
    ///
    /// <para><b>This is not a duplicate of the survival test and it is not optional.</b> The obvious way to
    /// make a rebuild keep a keyboard reviewer's place is "restore focus afterwards", and a repair that fires
    /// when nothing was taken is strictly worse than the drop it repairs — which is why #168 left panel focus
    /// opt-IN for all three automatic opens, and why #200 spent a whole test on a re-render that moved nothing.
    /// Without this half, "always restore focus" passes everything.</para>
    ///
    /// <para>The caret is put in the open composer, which <c>render()</c> does NOT rebuild — so "it is our
    /// chrome" is no licence to touch it — and the composer is opened INSIDE the expanded view, on the diagram
    /// the reviewer is annotating. The typed text and the caret offsets are read back together, so a caret
    /// that was merely re-placed at the start (or the end) of a half-typed note is not accepted as untouched.
    /// Yanking a reviewer out of a sentence because a colleague saved something would be a worse bug than any
    /// this feature is fixing, and it is completely silent.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "DiagramExpandInvariants")]
    public async Task A_render_while_expanded_does_not_steal_focus()
    {
        var directory = NewPlanDirectory("diagram-expand-focus");
        var planPath = Path.Combine(directory, "expand-focus.charter.md");
        await File.WriteAllTextAsync(planPath, PanZoomDiagramPlan);

        var mine = new ReviewLogWriter(planPath, new ReviewAuthor("Alice Ng", "alice@example.com"));
        var teammate = new ReviewLogWriter(planPath, new ReviewAuthor("Bob Chen", "bob@example.com"));

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(session, new ReviewServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            ReviewLog = mine,
        });

        try
        {
            var launched = await TryLaunchAsync();
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;

            await OpenExpandGateAsync(page, server, session);
            await WaitForEventAsync(page, "review-log-loaded");

            var paragraph = await AnchorIdAsync(page, "body > p:nth-of-type(1)");
            Assert.False(
                string.IsNullOrEmpty(paragraph),
                "fixture/renderer drift: the prose above the diagram carries no anchor, so the teammate has " +
                    "nothing to attach a record to and no render can be caused.");

            await ExpandAsync(page, Oversized);

            // A composer opened from INSIDE the expanded view, mid-sentence — SDK chrome that render() does
            // not rebuild, which is exactly why touching it would be a choice rather than a side effect.
            var background = await VisibleDiagramBackgroundPointAsync(page, Oversized);
            await page.Keyboard.DownAsync("Alt");
            await page.Mouse.ClickAsync(background.X, background.Y);
            await page.Keyboard.UpAsync("Alt");
            await page.WaitForSelectorAsync(
                Ui("composer-input"), new PageWaitForSelectorOptions { Timeout = ReadinessTimeoutMs });

            await page.Keyboard.TypeAsync(HalfTypedNote);
            Assert.Equal("TEXTAREA[composer-input]", await FocusIdentityAsync(page));

            var openedOver = await ExpandProbeAsync(page, Oversized);
            Assert.True(
                openedOver.IsExpanded,
                "the composer is not open over an EXPANDED diagram, so nothing below is about #234: " +
                    openedOver);

            var rendersBefore = await CountEventsAsync(page, "markers-rendered");

            teammate.AppendCreate(
                new ReviewAnchor(paragraph, "element", "an ordinary prose paragraph", null),
                "A teammate's note, landing while the reviewer is mid-sentence inside the expanded diagram.");
            await WaitForBadgeAsync(page, paragraph, teammate);

            Assert.True(
                await CountEventsAsync(page, "markers-rendered") > rendersBefore,
                "Charter #234 / #200 — the teammate's record never caused a marker pass, so no render " +
                    "happened and 'the render moved nothing' would be a claim about nothing.");

            // Read ONCE: a second read for the failure message would be a second chance for focus to be
            // somewhere else, and the message would then describe a moment the assertion did not test.
            var landed = await FocusIdentityAsync(page);
            Assert.True(
                string.Equals(landed, "TEXTAREA[composer-input]", StringComparison.Ordinal),
                "Charter #234 / #200 — a teammate's note pulled focus out of the half-typed composer and " +
                    "onto " + landed + " while the diagram was expanded. Restoring focus after a rebuild is a " +
                    "repair; a repair that fires when nothing was taken is the worse bug, and #168 left panel " +
                    "focus opt-in for exactly this reason.");

            var caret = await page.EvaluateAsync<string>(
                "() => { const a = document.activeElement;" +
                "  if (!a || a.selectionStart === undefined) return '(not a text field)';" +
                "  return a.value + '|' + a.selectionStart + ',' + a.selectionEnd; }");
            var expectedCaret = HalfTypedNote + "|" + HalfTypedNote.Length + "," + HalfTypedNote.Length;
            Assert.True(
                string.Equals(caret, expectedCaret, StringComparison.Ordinal),
                "Charter #234 / #200 — the composer read '" + caret + "' after a teammate's note arrived " +
                    "under the expanded view, expected '" + expectedCaret + "'. Focus that lands back on the " +
                    "right element with the caret re-placed satisfies every activeElement check and still " +
                    "costs the reviewer their sentence.");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            CleanupDirectory(directory);
        }
    }

    // ---- probes and small helpers --------------------------------------------------------------------------

    /// <summary>
    /// Press the zoom bar's <c>+</c> until the diagram has something to pan to, and FAIL naming #234 if it
    /// never does.
    ///
    /// <para>Gated on the same live geometry <c>canPan</c> reads — <c>scrollWidth</c> against
    /// <c>clientWidth</c> — rather than on a scale, because the pan gesture is gated on exactly that and a
    /// count of clicks would be a guess about the ceiling. The ceiling is respected: <c>syncZoomBar</c>
    /// disables <c>+</c> there (#204), and clicking a disabled button would sit out Playwright's actionability
    /// timeout and report it as a hang.</para>
    /// </summary>
    private static async Task<JsonElement> ZoomUntilPannableAsync(IPage page, int index, int maxPresses = 8)
    {
        var probe = await DiagramProbeAsync(page, index);
        for (var i = 0; i < maxPresses && !DiagramCanPan(probe); i++)
        {
            if (await page.Locator(Ui("diagram-zoom-in")).IsDisabledAsync())
            {
                break;
            }

            await page.ClickAsync(Ui("diagram-zoom-in"));
            probe = await DiagramProbeAsync(page, index);
        }

        Assert.True(
            DiagramCanPan(probe),
            "Charter #234 — the expanded diagram never became a scroll region, even zoomed to the ceiling, so " +
                "there is nothing to pan and the pan invariant cannot be exercised. Either the expanded box " +
                "stopped overflowing (overflow styled away, or the zoom writing a width smaller than the box " +
                "it is in) or the zoom stopped growing it: " + probe);
        return probe;
    }

    /// <summary>Is there anything to pan to — asked of the live scroll geometry, the way <c>canPan</c> asks it.</summary>
    private static bool DiagramCanPan(JsonElement probe)
        => probe.GetProperty("scrollWidth").GetDouble() > probe.GetProperty("clientWidth").GetDouble() + 1;

    /// <summary>
    /// Where the zoom bar is, relative to the block it is pinned inside AND in the viewport, read in ONE
    /// synchronous in-page pass so no <c>await</c> can land between two halves of one measurement.
    /// </summary>
    private sealed record ZoomBarPin(
        bool Present, bool InsideBlock, double OffsetLeft, double OffsetTop,
        double Left, double Right, double Top, double Bottom,
        double ScrollLeft, double ScrollTop, double InnerWidth, double InnerHeight)
    {
        public override string ToString()
            => Present
                ? "zoom bar offset=[" + Round(OffsetLeft) + "," + Round(OffsetTop) + "] viewport=[" +
                  Round(Left) + "," + Round(Top) + " -> " + Round(Right) + "," + Round(Bottom) +
                  "] insideBlock=" + InsideBlock + " blockScroll=[" + Round(ScrollLeft) + "," +
                  Round(ScrollTop) + "] window=" + Round(InnerWidth) + "x" + Round(InnerHeight)
                : "(no [data-charter-ui=\"diagram-zoom\"] inside that diagram)";
    }

    private static async Task<ZoomBarPin> ZoomBarPinAsync(IPage page, int index)
    {
        var json = await page.EvaluateAsync<string>(
            "i => {" +
            "  const block = document.querySelectorAll('pre.mermaid')[i];" +
            "  const bar = block ? block.querySelector('[data-charter-ui=\"diagram-zoom\"]') : null;" +
            "  const sl = block ? block.scrollLeft : 0, st = block ? block.scrollTop : 0;" +
            "  if (!bar) return JSON.stringify({ present: false, insideBlock: false," +
            "    offsetLeft: 0, offsetTop: 0, left: 0, right: 0, top: 0, bottom: 0," +
            "    scrollLeft: sl, scrollTop: st," +
            "    innerWidth: window.innerWidth, innerHeight: window.innerHeight });" +
            "  const b = bar.getBoundingClientRect();" +
            "  const p = block.getBoundingClientRect();" +
            "  return JSON.stringify({ present: true, insideBlock: block.contains(bar)," +
            "    offsetLeft: b.left - p.left, offsetTop: b.top - p.top," +
            "    left: b.left, right: b.right, top: b.top, bottom: b.bottom," +
            "    scrollLeft: sl, scrollTop: st," +
            "    innerWidth: window.innerWidth, innerHeight: window.innerHeight });" +
            "}",
            index);

        return JsonSerializer.Deserialize<ZoomBarPin>(
                   json!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("the zoom-bar pin probe returned nothing");
    }

    /// <summary>The label text of one Mermaid node, so a posted <c>nodeId</c> can be tied back to what the
    /// reviewer could actually see.</summary>
    private static Task<string> DiagramNodeLabelAsync(IPage page, string nodeId)
        => page.EvaluateAsync<string>(
            "id => { const n = document.getElementById(id);" +
            "  return n ? (n.textContent || '').replace(/\\s+/g, ' ').trim() : ''; }",
            nodeId);

    /// <summary>
    /// The same text with every space removed. Mermaid wraps a long label across tspans and the plan's source
    /// writes it on one line, so this is the honest comparison of the two.
    /// </summary>
    private static string WithoutWhitespace(string? text)
        => System.Text.RegularExpressions.Regex.Replace(text ?? string.Empty, @"\s+", string.Empty);
}
