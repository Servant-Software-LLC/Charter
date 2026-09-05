using System.Net;
using System.Text.Json;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #234 — the review-time <b>expand mode</b> for an oversized <c>:::diagram</c>, written RED.
///
/// <para>A reviewer looking at a two-subgraph diagram has two choices today and both are bad: pan blind at a
/// legible zoom, or zoom out until the labels stop being words. #51 already did the expensive half — zooming
/// WIDENS the <c>&lt;svg&gt;</c> rather than transforming it, so the block is an ordinary scroll container and
/// there is no coordinate frame of Charter's own — which is why the remaining feature is "make the container
/// the size of the viewport" and not a second rendering path.</para>
///
/// <para><b>What these tests pin, and why each one is a contract rather than a preference.</b> Three of the
/// plan's four settled decisions are load-bearing here and are asserted directly:</para>
/// <list type="bullet">
///   <item><b>In place, <c>position: fixed</c>, no reparent.</b> Asserted as the computed position PLUS the
///     block's parent being the same element before, during and after — a reparent would change ancestry, and
///     with it anchors and the source map, while every geometric assertion below still passed.</item>
///   <item><b>Nothing is <c>display: none</c> while expanded.</b> The expanded view paints OVER the page; it
///     never removes anything from layout. #221 is an undiagnosed focus defect whose leading hypothesis is
///     that focus into a <c>display: none</c> subtree silently does nothing, so manufacturing more of that
///     condition on this page is the one thing this feature must not do.</item>
///   <item><b>The button and the hint are the only ways IN.</b> A keyboard shortcut was offered in review and
///     refused, so nothing here reaches expand mode by a chord. Escape only ever LEAVES.</item>
/// </list>
///
/// <para><b>The name this suite drives the feature by.</b> The control is
/// <c>[data-charter-ui="diagram-expand"]</c> — SDK chrome in the existing zoom bar, built like its siblings
/// (<c>diagram-zoom-in</c>, <c>diagram-zoom-reset</c>) with no <c>id</c>, so <c>closestAnchored</c> refuses it
/// as an annotation target — and the expanded state is the class <c>charter-expand</c> on the block, which is
/// the token the plan's terminal union invariant gates its contribution check on.</para>
///
/// <para><b>Every test in this file is expected to FAIL until the sibling implementation task lands.</b> None
/// of them asserts a state the current SDK can reach: there is no expand control, so each one dies at the
/// point it tries to find or drive the real thing. That is the point — a test never seen to fail is a
/// hypothesis about your own code.</para>
///
/// <para><b>Traps this file refuses, each already paid for once in this repo.</b> No
/// <c>WaitForFunctionAsync</c> (the served page's CSP has no <c>'unsafe-eval'</c>, so its polling predicate
/// throws the moment it genuinely has to wait — it only ever appears to work when the condition was already
/// true). No second <c>WaitForEventAsync</c> in one test (it asks "has this EVER happened", so the second one
/// returns instantly); <see cref="SaveComposerAsync"/> and <see cref="WaitForEventCountAsync"/> exist for
/// that. Every measurement is one synchronous in-page evaluation taken AFTER the last <c>await</c>, because a
/// render sweeps the SDK's chrome away and rebuilds it, and a handle captured before an <c>await</c> can be
/// detached — it reports a 0x0 rect and an EMPTY computed style, which is how detachment is told apart from
/// <c>display: none</c>, which keeps a real one.</para>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    /// <summary>
    /// The <c>data-charter-ui</c> name of the expand control. Named here rather than inline because it is the
    /// contract between this file and the implementation: these tests drive the control BY THIS NAME, and the
    /// zoom bar's existing children (<c>diagram-zoom-in</c>, <c>diagram-zoom-out</c>,
    /// <c>diagram-zoom-reset</c>) are what it is spelled to match.
    /// </summary>
    private const string ExpandControlName = "diagram-expand";

    /// <summary>
    /// The class that marks a block as expanded. Pinned rather than inferred: the plan's terminal union
    /// invariant gates the expand chrome's contribution check on the <c>charter-expand</c> stem, so a
    /// differently-named class would leave that half of the gate permanently inert while looking fine here.
    /// </summary>
    private const string ExpandedClass = "charter-expand";

    /// <summary>
    /// The reviewing window these tests measure in. Wider than the 1000x800 the #51 pan/zoom tests use, and
    /// for a measured reason: the content column is <c>max-width</c>-bound at 52rem, so at a 1000px window the
    /// resting diagram is already 832px — 83% of the viewport — and "the expanded view fills the viewport"
    /// would be separated from "nothing happened" by eighteen pixels. The column does not grow with the
    /// window, so a wider one leaves the resting block at the same 832px and the difference this measures
    /// becomes the several hundred pixels it actually is.
    /// </summary>
    private const int ExpandGateWidth = 1280;

    private const int ExpandGateHeight = 800;

    /// <summary>
    /// How long an expand/collapse may take before it is a failure. Deliberately SHORT and local, unlike the
    /// suite's readiness timeouts: this is an assertion deadline for a synchronous DOM state change the
    /// reviewer's own click causes, not a browser→server round trip, so a runner under contention is not an
    /// explanation for it. It is also what keeps the red run brisk while the feature is absent.
    /// </summary>
    private const int ExpandDeadlineMs = 5_000;

    // ---- the affordance ------------------------------------------------------------------------------------

    /// <summary>
    /// The discovery route, and the only one: a control in the zoom bar the reviewer can see and a screen
    /// reader can name. Both halves are asserted, because either alone ships a defect — an unnamed button is a
    /// tab stop that announces as nothing (#68's lesson, one control over), and a named control that is not in
    /// the bar is a second place to look for something the reviewer already has one place for.
    ///
    /// <para>Two supporting facts are asserted with it. It carries no <c>id</c>: SDK chrome that acquired one
    /// would be accepted by <c>closestAnchored</c>'s walk and could capture the anchor of the block it sits in
    /// (#166, in the escape hatch, cost exactly that). And the diagram that FITS gains nothing — expand is for
    /// the diagram whose column is too narrow for it, and a control on a diagram showing everything it has is
    /// noise on every plan in the repo.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "DiagramExpandAffordance")]
    public async Task An_oversized_diagram_offers_an_expand_control()
    {
        var planPath = NewPlanPath("diagram-expand-control");
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

            // ---- the fixture's premise: this diagram really is bigger than its column ----
            var big = await DiagramProbeAsync(page, Oversized);
            Assert.True(
                big.GetProperty("intrinsicWidth").GetDouble()
                    > big.GetProperty("renderedWidth").GetDouble() + 8,
                "fixture drift: the wide diagram is not being drawn smaller than Mermaid laid it out, so it " +
                    "is not the oversized diagram #234 is about and nothing below means anything: " + big);
            Assert.True(
                big.GetProperty("hasBar").GetBoolean(),
                "the oversized diagram carries no zoom bar, so there is nothing for the expand control to " +
                    "live in: " + big);

            await RequireExpandControlAsync(page);
            var control = await ExpandControlProbeAsync(page);

            // One control, in the bar of the diagram it expands — not a second one somewhere else, and not on
            // the wrong diagram.
            Assert.Equal(1, control.Count);
            Assert.True(
                control.InZoomBar,
                "Charter #234 — the expand control is not inside the zoom bar. The bar is the one place a " +
                    "reviewer already has to look, which is why the plan put it there: " + control);
            Assert.Equal(Oversized, control.BlockIndex);

            // A real button, so Enter/Space and the browser's own focus handling come for free.
            Assert.Equal("BUTTON", control.Tag);
            Assert.False(
                control.Disabled,
                "Charter #234 — the expand control is disabled on a diagram that is too wide for its column, " +
                    "which is precisely the case it exists for: " + control);

            // SDK chrome carries `data-charter-ui` and NO id — an id would be accepted by closestAnchored's
            // walk, and a note taken on the diagram could bind to the button instead of the block (#166).
            Assert.Equal(string.Empty, control.Id);

            // The accessible name. Not merely non-empty: it has to say what the control DOES, or the tab stop
            // announces as "button" and the reviewer is no better off than with no name at all.
            Assert.False(
                string.IsNullOrWhiteSpace(control.AccessibleName),
                "Charter #234 — the expand control has no accessible name, so it announces as nothing to a " +
                    "screen reader and as an unlabelled glyph to everyone else: " + control);
            Assert.True(
                NamesTheExpandAction(control.AccessibleName),
                "Charter #234 — the expand control's accessible name ('" + control.AccessibleName + "') does " +
                    "not name the action. It should say expand (or full screen), the way 'Zoom the diagram " +
                    "in' names its own: " + control);

            // ---- and the diagram that FITS gains nothing ----
            var small = await ExpandProbeAsync(page, Fitting);
            Assert.False(
                small.HasBar,
                "a diagram that fits its column must gain no zoom chrome at all: " + small);
            Assert.False(
                small.HasExpandControl,
                "Charter #234 — the diagram that already shows everything it has was given an expand control " +
                    "too. Expand answers a diagram being wider than its column; on one that fits it is noise " +
                    "on every plan in the repo: " + small);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    /// <summary>
    /// The capability itself: the control makes THAT diagram the size of the viewport.
    ///
    /// <para>The premise is asserted first — at rest the block is nowhere near viewport-sized — so "it fills
    /// the viewport" cannot pass by having always been true. Then the three things that make this the
    /// mechanism the plan settled on rather than a lookalike: the block is <c>position: fixed</c> (so the page
    /// behind it can scroll under it and the expanded box does not move), it is still the child of the same
    /// parent (in place, NO reparent — a reparent changes ancestry and with it anchors and the source map,
    /// and every measurement here would still pass), and it carries the <c>charter-expand</c> class the plan's
    /// union invariant is keyed on.</para>
    ///
    /// <para>The size bounds are deliberately loose — 85% of the window's width, 75% of its height — because
    /// an implementation that insets the expanded view by a margin is still filling the viewport in every
    /// sense a reviewer means. What they exclude is the failure that matters: a box that is still bounded by
    /// its content column.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "DiagramExpandAffordance")]
    public async Task Expanding_a_diagram_fills_the_viewport()
    {
        var planPath = NewPlanPath("diagram-expand-fills");
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

            // ---- the premise ----
            var atRest = await ExpandProbeAsync(page, Oversized);
            Assert.True(
                atRest.Width < atRest.InnerWidth * 0.85,
                "fixture drift: the diagram already spans the window at rest, so 'expanding fills the " +
                    "viewport' would be true of a page where nothing happened: " + atRest);
            Assert.NotEqual("fixed", atRest.Position);

            var expanded = await ExpandAsync(page, Oversized);

            // ---- the settled mechanism, not a lookalike ----
            Assert.True(
                expanded.Expanded,
                "Charter #234 — the expanded block does not carry the '" + ExpandedClass + "' class. The " +
                    "plan's union invariant is keyed on that token, so a differently-named marker leaves that " +
                    "gate inert: " + expanded);
            Assert.Equal("fixed", expanded.Position);
            Assert.Equal(atRest.Parent, expanded.Parent);

            // ---- and it really is the viewport ----
            Assert.True(
                expanded.Width > atRest.Width + 100,
                "Charter #234 — expanding did not make the diagram meaningfully wider (" +
                    Round(atRest.Width) + "px -> " + Round(expanded.Width) + "px): " + expanded);
            Assert.True(
                expanded.Width >= expanded.InnerWidth * 0.85,
                "Charter #234 — the expanded diagram is " + Round(expanded.Width) + "px wide in a " +
                    Round(expanded.InnerWidth) + "px window, so it is still bounded by something. Expand " +
                    "mode exists because the column is what makes a two-subgraph diagram unreadable: " +
                    expanded);
            Assert.True(
                expanded.Height >= expanded.InnerHeight * 0.75,
                "Charter #234 — the expanded diagram is " + Round(expanded.Height) + "px tall in a " +
                    Round(expanded.InnerHeight) + "px window: " + expanded);
            Assert.True(
                expanded.Left <= expanded.InnerWidth * 0.1 && expanded.Top <= expanded.InnerHeight * 0.15,
                "Charter #234 — the expanded diagram is the right SIZE but is anchored at (" +
                    Round(expanded.Left) + "," + Round(expanded.Top) + "), so most of it is off-screen: " +
                    expanded);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    /// <summary>
    /// The round trip. Expand mode is temporary by construction — the reviewer goes in to read one diagram and
    /// comes back to the plan — so the exit has to leave the page exactly as it found it.
    ///
    /// <para>"Exactly" is measured in DOCUMENT coordinates (viewport rect plus the page's scroll offset), not
    /// viewport ones: taking the block out of flow shortens the page, the browser may adjust the scroll
    /// position, and Playwright's own click scrolls its target into view. Comparing viewport rects would
    /// therefore report a scroll as a restore failure — and, worse, could report a real restore failure as a
    /// pass on a page that happened to scroll back.</para>
    ///
    /// <para>The pan/zoom affordance is asserted to have survived as well. Collapsing by tearing the view down
    /// and rebuilding it would restore the BOX while leaving the reviewer without the tab stop, the role, the
    /// bar, or their zoom level — a restore that passes a geometry check and loses the feature.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "DiagramExpandAffordance")]
    public async Task Expanding_then_collapsing_restores_the_original_box()
    {
        var planPath = NewPlanPath("diagram-expand-restore");
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

            var before = await ExpandProbeAsync(page, Oversized);
            var expanded = await ExpandAsync(page, Oversized);
            Assert.True(
                expanded.Width > before.Width + 100,
                "this test proves nothing unless the diagram really expanded first: " + expanded);

            // ---- and the same control takes it back ----
            await page.ClickAsync(Ui(ExpandControlName));
            var level = await page.InnerTextAsync(Ui("diagram-zoom-level"));
            var after = await PollExpandedAsync(
                page, Oversized, expanded: false,
                "Charter #234 — activating the expand control a second time did not collapse the diagram. " +
                    "The control is a toggle: the reviewer who expanded a diagram to read it has to be able " +
                    "to put it back the same way they opened it");

            Assert.DoesNotContain(ExpandedClass, after.Classes, StringComparison.Ordinal);
            Assert.NotEqual("fixed", after.Position);
            Assert.Equal(before.Position, after.Position);

            // The box itself, in document coordinates.
            Assert.True(
                Math.Abs(after.Width - before.Width) <= 1 && Math.Abs(after.Height - before.Height) <= 1,
                "Charter #234 — collapsing did not restore the diagram's own size:\n  before " + before +
                    "\n  after  " + after);
            Assert.True(
                Math.Abs(after.DocLeft - before.DocLeft) <= 1 && Math.Abs(after.DocTop - before.DocTop) <= 1,
                "Charter #234 — the collapsed diagram came back in a different place on the page:\n  before " +
                    before + "\n  after  " + after);

            // ...and the reviewer still has everything #51 gave them.
            Assert.Equal(before.Parent, after.Parent);
            Assert.True(
                after.HasBar,
                "Charter #234 — collapsing took the zoom bar with it, so the reviewer is left with a diagram " +
                    "they can no longer zoom, pan or expand: " + after);
            Assert.Equal(0, after.TabIndex);
            Assert.Equal("group", after.Role);
            Assert.Equal("100%", level);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    /// <summary>
    /// Escape is the exit every reviewer will try first, and the plan's one free collision.
    ///
    /// <para>Both halves are driven. Plain Escape, with no composer open, collapses the view and puts the box
    /// back. Then the PRECEDENCE: the composer already handles Escape and calls <c>stopPropagation()</c>, so
    /// with a composer open inside the expanded view the first Escape closes the composer and LEAVES the view
    /// expanded, and the second one exits. That ordering is what a reviewer would expect and it comes for
    /// free — but only as long as nothing fights it, which is exactly the kind of thing a document-level
    /// handler added in a hurry does. A capture-phase listener, or one that swallowed Escape before the
    /// composer saw it, would throw the reviewer out of the view AND lose the note they were typing; both
    /// halves of that are silent.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "DiagramExpandAffordance")]
    public async Task Escape_collapses_the_expanded_diagram()
    {
        var planPath = NewPlanPath("diagram-expand-escape");
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

            var before = await ExpandProbeAsync(page, Oversized);
            await ExpandAsync(page, Oversized);

            // ---- Escape, with nothing else open, leaves ----
            await page.Keyboard.PressAsync("Escape");
            var collapsed = await PollExpandedAsync(
                page, Oversized, expanded: false,
                "Charter #234 — Escape did not collapse the expanded diagram. It is the first thing anyone " +
                    "presses to get out of something that took the screen, and a view with no keyboard exit " +
                    "is a trap");

            Assert.True(
                Math.Abs(collapsed.Width - before.Width) <= 1
                    && Math.Abs(collapsed.DocTop - before.DocTop) <= 1,
                "Charter #234 — Escape dropped the expanded class but did not put the diagram's box back:\n" +
                    "  before " + before + "\n  after  " + collapsed);

            // ---- and with a composer open, the composer gets the first Escape ----
            await ExpandAsync(page, Oversized);

            var point = await VisibleDiagramBackgroundPointAsync(page, Oversized);
            await page.Keyboard.DownAsync("Alt");
            await page.Mouse.ClickAsync(point.X, point.Y);
            await page.Keyboard.UpAsync("Alt");
            await page.WaitForSelectorAsync(
                Ui("composer-input"), new PageWaitForSelectorOptions { Timeout = ReadinessTimeoutMs });

            await page.Keyboard.PressAsync("Escape");
            await page.WaitForSelectorAsync(
                Ui("composer"),
                new PageWaitForSelectorOptions
                {
                    State = WaitForSelectorState.Detached,
                    Timeout = ExpandDeadlineMs,
                });

            var stillExpanded = await ExpandProbeAsync(page, Oversized);
            Assert.True(
                stillExpanded.IsExpanded,
                "Charter #234 — the Escape that closed the composer ALSO collapsed the expanded view, so a " +
                    "reviewer cancelling a note is thrown out of the diagram they were annotating. The " +
                    "composer calls stopPropagation() on Escape; a document-level listener must not be " +
                    "reaching past it: " + stillExpanded);

            // ...and the next one exits, which is the precedence a reviewer expects.
            await page.Keyboard.PressAsync("Escape");
            await PollExpandedAsync(
                page, Oversized, expanded: false,
                "Charter #234 — with the composer closed, the next Escape did not collapse the view");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    /// <summary>
    /// Charter #68's precedent, applied to the new control: an affordance only a mouse can reach hides the
    /// diagram from a keyboard-only reviewer exactly as effectively as the narrow column did.
    ///
    /// <para>Walked with real <c>Tab</c> presses from a blurred document and activated with a real
    /// <c>Enter</c> — never <c>tabIndex &gt;= 0</c>, which is true of plenty of elements Tab never reaches, and
    /// never a locator click, which proves only that the element is clickable.</para>
    ///
    /// <para>The third assertion is the one the #168/#200/#204 family exists for. The control is inside the
    /// block it expands, so activating it must not leave focus on <c>&lt;body&gt;</c>: a reviewer who entered
    /// expand mode from the keyboard and lost the caret has to re-traverse the document to get back out, and
    /// the only exits are the button they just lost and an Escape whose handler they can no longer be sure
    /// of.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "DiagramExpandAffordance")]
    public async Task The_expand_control_is_reachable_by_keyboard()
    {
        var planPath = NewPlanPath("diagram-expand-keys");
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

            // Reported as "the control does not exist" rather than as "40 Tab presses did not find it", which
            // is the same fact wearing a misleading number.
            await RequireExpandControlAsync(page);

            // ---- Tab reaches it, from a blurred document, with no mouse ----
            await TabUntilAsync(page, ExpandControlName, maxPresses: 40);

            // ---- and Enter operates it ----
            await page.Keyboard.PressAsync("Enter");
            var expanded = await PollExpandedAsync(
                page, Oversized, expanded: true,
                "Charter #234 — Enter on the focused expand control did not expand the diagram, so the " +
                    "control is reachable from the keyboard but not operable from it");

            Assert.Equal("fixed", expanded.Position);

            // ---- and the caret is still somewhere the reviewer can act from ----
            var focus = await ExpandFocusAsync(page, Oversized);
            Assert.True(
                focus.InsideBlock,
                "Charter #234 — expanding from the keyboard left focus on " + focus.Description + ". The " +
                    "control lives inside the block it expands and is not rebuilt by this gesture, so focus " +
                    "should still be on it; dropping to <body> is the #168/#200/#204 shape and it strands a " +
                    "keyboard reviewer inside a view whose exits they can no longer reach.");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    /// <summary>
    /// The second discovery route the reviewer chose: the zoom bar's hint names expand while the diagram is
    /// wider than its column.
    ///
    /// <para>The hint is a single <c>&lt;span&gt;</c> that <c>syncZoomBar</c> already drives between two
    /// states — <c>'Ctrl+scroll to zoom'</c> at fit, <c>'drag or arrow keys to pan'</c> once zoomed — so expand
    /// is a THIRD state competing for one slot. This asserts it at FIT and only at fit, which is the state
    /// where the reviewer has not yet done anything and the condition that motivated #234 (the diagram is
    /// bigger than the space it is given) is plainly true. What wins when a diagram is both too wide AND
    /// already zoomed is the implementation's call to make and to write down; nothing here pins it, because
    /// pinning an unmade decision is how a test starts dictating a design instead of checking one.</para>
    ///
    /// <para>The premise — intrinsic width really does exceed rendered width — is asserted first, and the
    /// diagram that fits is checked to have no hint at all, so this cannot pass by the hint being a constant
    /// that happens to mention expanding.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "DiagramExpandAffordance")]
    public async Task The_zoom_hint_names_expand_when_the_diagram_is_too_wide()
    {
        var planPath = NewPlanPath("diagram-expand-hint");
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

            // ---- the premise: this diagram is wider than the column it is drawn into ----
            var big = await DiagramProbeAsync(page, Oversized);
            Assert.True(
                big.GetProperty("intrinsicWidth").GetDouble()
                    > big.GetProperty("renderedWidth").GetDouble() + 8,
                "fixture drift: the wide diagram fits its column, which is the one condition under which the " +
                    "hint is NOT supposed to name expand: " + big);
            Assert.Equal("100%", await page.InnerTextAsync(Ui("diagram-zoom-level")));

            var hinted = await ExpandProbeAsync(page, Oversized);
            Assert.True(
                NamesTheExpandAction(hinted.Hint),
                "Charter #234 — the zoom bar's hint reads '" + hinted.Hint + "' on a diagram that is " +
                    Round(big.GetProperty("intrinsicWidth").GetDouble()) + "px of content in a " +
                    Round(big.GetProperty("renderedWidth").GetDouble()) + "px column. The reviewer asked for " +
                    "the hint to name expand exactly when the diagram is too wide for its column, which is " +
                    "the condition that motivated the issue: " + hinted);

            // The control the hint is advertising has to be there to be advertised.
            Assert.True(
                hinted.HasExpandControl,
                "Charter #234 — the hint names expand on a diagram whose zoom bar carries no expand control: " +
                    hinted);

            // ---- and the diagram that fits is not being told about a control it does not have ----
            var small = await ExpandProbeAsync(page, Fitting);
            Assert.Equal(string.Empty, small.Hint);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    /// <summary>
    /// The plan's hard rule, and the reason this feature was allowed to be built before #221 is diagnosed:
    /// <b>the expanded view hides nothing.</b> It paints over the page; it never removes anything from layout.
    ///
    /// <para>#221 is an unexplained focus defect on this page and its leading hypothesis is that focus into a
    /// <c>display: none</c> subtree silently does nothing — the panel hides exactly that way. So an expand
    /// implementation that tidied the page away with <c>display: none</c> would manufacture more of the
    /// suspected condition on the page where it is already failing. That is why this is a requirement of the
    /// plan rather than a preference, and why it is measured as the computed <c>display</c> PROPERTY:
    /// <c>getComputedStyle</c> on a hidden element still returns a real style object, so the property can be
    /// read directly, and inferring hiddenness from a zero-sized box would confuse it with the detached
    /// elements a render legitimately produces.</para>
    ///
    /// <para>The census is taken over element REFERENCES held in the page, not over selectors, so the two
    /// readings are of the same elements; anything already hidden before the expand (the panel toggle hides
    /// itself when the panel opens, and a <c>&lt;script&gt;</c> is display:none by definition) is exempt,
    /// because the rule is that EXPANDING hides nothing, not that nothing on the page is ever hidden.</para>
    ///
    /// <para>A real note is posted first, through the real composer, and the POSTED PAYLOAD is asserted. That
    /// is not ceremony: it is what makes the panel a thing with contents to lose, and it is the standard here
    /// because a DOM-only assertion has repeatedly certified a control that looked right and delivered
    /// nothing.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "DiagramExpandAffordance")]
    public async Task Expanding_hides_no_existing_chrome()
    {
        const string note = "the two halves of this diagram need one legible view";

        var planPath = NewPlanPath("diagram-expand-nothing-hidden");
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

            // The realistic reviewing condition: the notes panel open, with a note in it. A panel that is
            // empty and closed cannot demonstrate anything about what expanding does to it.
            await page.ClickAsync(Ui("panel-toggle"));
            await WaitForEventAsync(page, "panel-opened");

            await page.Locator("body > p:nth-of-type(1)")
                .ClickAsync(new LocatorClickOptions { Modifiers = new[] { KeyboardModifier.Alt } });
            await page.WaitForSelectorAsync(
                Ui("composer-input"), new PageWaitForSelectorOptions { Timeout = ReadinessTimeoutMs });
            await page.FillAsync(Ui("composer-input"), note);
            await SaveComposerAsync(page);

            // ---- what actually reached the server, before anything about layout is asserted ----
            var listed = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(1, listed.GetArrayLength());
            AssertBoundToTheProseAboveTheDiagram(
                FindByNote(listed, note), await AnchorIdAsync(page, "body > p:nth-of-type(1)"));

            await page.WaitForSelectorAsync(
                Ui("item"), new PageWaitForSelectorOptions { Timeout = ReadinessTimeoutMs });

            // ---- the census, of the elements themselves ----
            var atRest = await ExpandProbeAsync(page, Oversized);
            var before = await CaptureChromeCensusAsync(page);
            var panelBefore = CensusEntry(before, "panel");
            Assert.NotEqual("none", panelBefore.Display);

            var expanded = await ExpandAsync(page, Oversized);
            Assert.True(
                expanded.Expanded && expanded.Width > atRest.Width + 100,
                "this rule is vacuous unless the diagram really is expanded while it is measured (" +
                    Round(atRest.Width) + "px -> " + Round(expanded.Width) + "px): " + expanded);

            var after = await ReadChromeCensusAsync(page);
            Assert.Equal(before.Length, after.Length);

            // The panel by name, because it is the one #221 points at and a generic message would bury it.
            var panelAfter = CensusEntry(after, "panel");
            Assert.True(
                panelAfter.Connected,
                "Charter #234 — expanding removed the review panel from the document entirely: " + panelAfter);
            Assert.True(
                !string.Equals(panelAfter.Display, "none", StringComparison.Ordinal),
                "Charter #234 — expanding set the review panel to display:none. The expanded view must PAINT " +
                    "OVER the page, never remove anything from it: #221 is an undiagnosed focus defect whose " +
                    "leading hypothesis is that focus into a display:none subtree silently does nothing, and " +
                    "the panel hides exactly that way. Dim it, cover it, make it inert — do not hide it.");

            // ...and then everything else that was visible a moment ago.
            for (var i = 0; i < before.Length; i++)
            {
                if (string.Equals(before[i].Display, "none", StringComparison.Ordinal))
                {
                    // Already hidden before the expand — the panel toggle hides itself when the panel opens,
                    // and a <script> is display:none by definition. The rule is that EXPANDING hides nothing.
                    continue;
                }

                Assert.True(
                    after[i].Connected,
                    "Charter #234 — expanding took " + before[i].Label + " out of the document: " + after[i]);
                Assert.True(
                    !string.Equals(after[i].Display, "none", StringComparison.Ordinal),
                    "Charter #234 — expanding set " + before[i].Label + " to display:none (it was '" +
                        before[i].Display + "' a moment earlier). Nothing on the expand path may be hidden " +
                        "that way — paint over the page instead.");
            }

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- fixture and probes --------------------------------------------------------------------------------

    /// <summary>
    /// The same opening every test here makes: the served page at a realistic reviewing window, both diagrams
    /// rendered by Mermaid, and #51's pan/zoom view built — which is what puts the zoom bar on the page for
    /// the expand control to live in.
    /// </summary>
    private static async Task OpenExpandGateAsync(IPage page, ReviewServer server, ReviewSession session)
    {
        await page.SetViewportSizeAsync(ExpandGateWidth, ExpandGateHeight);
        await page.GotoAsync(
            CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        await WaitForEventAsync(page, "ready");
        await WaitForDiagramsAsync(page, 2);
        await WaitForEventAsync(page, "diagram-zoomable");
    }

    /// <summary>
    /// Does this reviewer-facing string name the expand action? Both idioms are accepted, because "Expand the
    /// diagram" and "Show the diagram full screen" are the same promise and choosing between them is a wording
    /// decision, not a contract.
    /// </summary>
    private static bool NamesTheExpandAction(string? text)
        => text is not null
           && (text.Contains("expand", StringComparison.OrdinalIgnoreCase)
               || text.Contains("full screen", StringComparison.OrdinalIgnoreCase)
               || text.Contains("full-screen", StringComparison.OrdinalIgnoreCase)
               || text.Contains("fullscreen", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Everything the expand affordance could get wrong about one rendered <c>:::diagram</c>, read in ONE
    /// synchronous in-page pass so no <c>await</c> can land between two halves of a measurement.
    /// </summary>
    private sealed record DiagramExpandProbe(
        bool Expanded, string Classes, string Position, string Display,
        double Left, double Top, double Width, double Height, double DocLeft, double DocTop,
        string Parent, bool HasBar, bool HasExpandControl, string Hint,
        int TabIndex, string Role, double InnerWidth, double InnerHeight)
    {
        /// <summary>
        /// Expanded by EITHER of the two things the plan settled — the class the union invariant is keyed on,
        /// or the fixed positioning that is the mechanism. Polling on the union rather than on the class alone
        /// means a state change that happened but was marked differently is reported by the specific assertion
        /// that names it, instead of as an indistinguishable timeout.
        /// </summary>
        public bool IsExpanded
            => Expanded || string.Equals(Position, "fixed", StringComparison.Ordinal);

        public override string ToString()
            => "diagram class='" + Classes + "' position=" + Position + " display=" + Display +
               " viewport=[" + Round(Left) + "," + Round(Top) + " " + Round(Width) + "x" + Round(Height) +
               "] doc=[" + Round(DocLeft) + "," + Round(DocTop) + "] window=" + Round(InnerWidth) + "x" +
               Round(InnerHeight) + " parent=" + Parent + " bar=" + HasBar + " expandControl=" +
               HasExpandControl + " hint='" + Hint + "' tabIndex=" + TabIndex + " role=" + Role;
    }

    private static async Task<DiagramExpandProbe> ExpandProbeAsync(IPage page, int index)
    {
        var json = await page.EvaluateAsync<string>(
            "i => {" +
            "  const el = document.querySelectorAll('pre.mermaid')[i];" +
            "  if (!el) return 'null';" +
            "  const cs = getComputedStyle(el);" +
            "  const r = el.getBoundingClientRect();" +
            "  const sig = n => n ? n.tagName + (String(n.className || '').trim()" +
            "    ? '.' + String(n.className).trim().split(/\\s+/).join('.') : '') : '(detached)';" +
            "  const hint = el.querySelector('[data-charter-ui=\"diagram-zoom-hint\"]');" +
            "  return JSON.stringify({" +
            "    expanded: el.classList.contains('" + ExpandedClass + "')," +
            "    classes: el.className," +
            "    position: cs.position, display: cs.display," +
            "    left: r.left, top: r.top, width: r.width, height: r.height," +
            "    docLeft: r.left + window.scrollX, docTop: r.top + window.scrollY," +
            "    parent: sig(el.parentElement)," +
            "    hasBar: !!el.querySelector('[data-charter-ui=\"diagram-zoom\"]')," +
            "    hasExpandControl: !!el.querySelector('[data-charter-ui=\"" + ExpandControlName + "\"]')," +
            "    hint: hint ? (hint.textContent || '') : ''," +
            "    tabIndex: el.tabIndex, role: el.getAttribute('role') || ''," +
            "    innerWidth: window.innerWidth, innerHeight: window.innerHeight" +
            "  });" +
            "}",
            index);

        Assert.NotEqual("null", json);
        return JsonSerializer.Deserialize<DiagramExpandProbe>(
                   json!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("the expand probe returned nothing");
    }

    /// <summary>The expand control as the browser has it — or <c>Present: false</c>, which is today's answer.</summary>
    private sealed record ExpandControlProbe(
        bool Present, int Count, string Tag, string Id, string AriaLabel, string Title, string Text,
        bool Disabled, bool InZoomBar, int BlockIndex)
    {
        /// <summary>
        /// What a screen reader would announce, in the order the accessible-name computation prefers:
        /// <c>aria-label</c>, then <c>title</c>, then the control's own text.
        /// </summary>
        public string AccessibleName
            => AriaLabel.Length > 0 ? AriaLabel : (Title.Length > 0 ? Title : Text);

        public override string ToString()
            => Present
                ? "expand control <" + Tag + "> count=" + Count + " id='" + Id + "' name='" + AccessibleName +
                  "' disabled=" + Disabled + " inZoomBar=" + InZoomBar + " onDiagram#" + BlockIndex
                : "(no [data-charter-ui=\"" + ExpandControlName + "\"] anywhere on the page)";
    }

    private static async Task<ExpandControlProbe> ExpandControlProbeAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(
            "() => {" +
            "  const all = document.querySelectorAll('[data-charter-ui=\"" + ExpandControlName + "\"]');" +
            "  const el = all[0];" +
            "  if (!el) return JSON.stringify({ present: false, count: 0, tag: '', id: ''," +
            "    ariaLabel: '', title: '', text: '', disabled: false, inZoomBar: false, blockIndex: -1 });" +
            "  const block = el.closest('pre.mermaid');" +
            "  const blocks = Array.prototype.slice.call(document.querySelectorAll('pre.mermaid'));" +
            "  return JSON.stringify({" +
            "    present: true, count: all.length, tag: el.tagName, id: el.id || ''," +
            "    ariaLabel: el.getAttribute('aria-label') || ''," +
            "    title: el.getAttribute('title') || ''," +
            "    text: (el.textContent || '').trim()," +
            "    disabled: !!el.disabled," +
            "    inZoomBar: !!el.closest('[data-charter-ui=\"diagram-zoom\"]')," +
            "    blockIndex: blocks.indexOf(block)" +
            "  });" +
            "}");

        return JsonSerializer.Deserialize<ExpandControlProbe>(
                   json!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("the expand-control probe returned nothing");
    }

    /// <summary>
    /// Fail with the CONTRACT rather than with a timeout, while the control does not exist. Every test in this
    /// file goes through here first, so the red they produce today names the thing to build instead of
    /// reporting that a click could not find its target.
    /// </summary>
    private static async Task RequireExpandControlAsync(IPage page, int timeoutMs = ExpandDeadlineMs)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await page.Locator(Ui(ExpandControlName)).CountAsync() > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail(
            "Charter #234 — no [data-charter-ui=\"" + ExpandControlName + "\"] exists on the page after " +
            timeoutMs + "ms. An oversized :::diagram's zoom bar must carry an expand control, built as SDK " +
            "chrome the way its siblings are (make()/button(), a data-charter-ui name, no id), and this suite " +
            "drives the real control by that name.");
    }

    /// <summary>Activate the control and wait for the view it opens. Fails naming #234 if it does not open.</summary>
    private static async Task<DiagramExpandProbe> ExpandAsync(IPage page, int index)
    {
        await RequireExpandControlAsync(page);
        await page.ClickAsync(Ui(ExpandControlName));

        return await PollExpandedAsync(
            page, index, expanded: true,
            "Charter #234 — activating the expand control did not expand the diagram");
    }

    /// <summary>
    /// Poll the block's own state until it is (or is no longer) expanded. A bounded <c>EvaluateAsync</c> poll,
    /// never <c>WaitForFunctionAsync</c>, whose in-page <c>eval</c> the served page's CSP correctly refuses —
    /// and which would therefore throw <c>EvalError</c> at exactly the moment it had real waiting to do.
    /// </summary>
    private static async Task<DiagramExpandProbe> PollExpandedAsync(
        IPage page, int index, bool expanded, string because, int timeoutMs = ExpandDeadlineMs)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        DiagramExpandProbe probe;
        while (true)
        {
            probe = await ExpandProbeAsync(page, index);
            if (probe.IsExpanded == expanded)
            {
                return probe;
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.Fail(because + " (within " + timeoutMs + "ms): " + probe);
        return probe;
    }

    /// <summary>Where the caret is, and whether it is anywhere inside the diagram that was just expanded.</summary>
    private sealed record ExpandFocus(string Description, bool InsideBlock);

    private static async Task<ExpandFocus> ExpandFocusAsync(IPage page, int index)
    {
        var json = await page.EvaluateAsync<string>(
            "i => {" +
            "  const el = document.querySelectorAll('pre.mermaid')[i];" +
            "  const a = document.activeElement;" +
            "  const describe = n => !n ? '(null)' : (n === document.body ? 'BODY'" +
            "    : n.tagName + '[' + (n.getAttribute('data-charter-ui') || '') + ']');" +
            "  return JSON.stringify({" +
            "    description: describe(a)," +
            "    insideBlock: !!(el && a && (el === a || el.contains(a)))" +
            "  });" +
            "}",
            index);

        return JsonSerializer.Deserialize<ExpandFocus>(
                   json!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("the focus probe returned nothing");
    }

    /// <summary>
    /// One censused element's visibility. <c>Connected</c> is carried beside <c>Display</c> because they are
    /// different failures with the same symptom in a screenshot: a detached element reports an EMPTY computed
    /// style, so reading <c>display</c> alone would let "removed from the document" pass as "not none".
    /// </summary>
    private sealed record ChromeVisibility(string Label, string Display, bool Connected)
    {
        public override string ToString()
            => Label + " display='" + Display + "' connected=" + Connected;
    }

    /// <summary>
    /// The shared signature helper, and the read over <c>window.__charterExpandCensus</c>. Held as element
    /// REFERENCES in the page rather than as selectors, so the second reading is of the same elements: a
    /// selector could resolve to a replacement built by a render and report it as if nothing had changed.
    /// </summary>
    private const string ChromeCensusRead =
        "const sig = el => el === document.body ? 'BODY'" +
        "  : el.tagName + (el.getAttribute('data-charter-ui')" +
        "    ? '[' + el.getAttribute('data-charter-ui') + ']'" +
        "    : (String(el.className || '').trim()" +
        "      ? '.' + String(el.className).trim().split(/\\s+/).join('.') : ''));" +
        "const read = list => JSON.stringify(list.map(el => ({" +
        "  label: sig(el)," +
        "  display: el.isConnected ? getComputedStyle(el).display : '(detached)'," +
        "  connected: el.isConnected" +
        "})));";

    private static async Task<ChromeVisibility[]> CaptureChromeCensusAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(
            "() => {" + ChromeCensusRead +
            "  const list = [];" +
            "  const add = el => { if (el && list.indexOf(el) < 0) list.push(el); };" +
            "  add(document.body);" +
            "  const tops = document.querySelectorAll('body > *');" +
            "  for (let i = 0; i < tops.length; i++) {" +
            // A badge rail is SDK chrome that render() clears and rebuilds, so a reference to the one that
            // exists now says nothing about whether anything was hidden — a REPLACED rail is not a hidden one.
            "    if (tops[i].getAttribute('data-charter-ui') === 'badge-rail') continue;" +
            "    add(tops[i]);" +
            "  }" +
            "  add(document.querySelector('[data-charter-ui=\"panel\"]'));" +
            "  window.__charterExpandCensus = list;" +
            "  return read(list);" +
            "}");

        return DeserializeCensus(json);
    }

    private static async Task<ChromeVisibility[]> ReadChromeCensusAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(
            "() => {" + ChromeCensusRead +
            "  return read(window.__charterExpandCensus || []);" +
            "}");

        return DeserializeCensus(json);
    }

    private static ChromeVisibility[] DeserializeCensus(string? json)
        => JsonSerializer.Deserialize<ChromeVisibility[]>(
               json!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
           ?? throw new InvalidOperationException("the chrome census returned nothing");

    /// <summary>The one censused element whose label names <paramref name="ui"/> — the panel, in practice.</summary>
    private static ChromeVisibility CensusEntry(ChromeVisibility[] census, string ui)
    {
        var match = census.SingleOrDefault(
            c => c.Label.EndsWith("[" + ui + "]", StringComparison.Ordinal));
        Assert.True(
            match is not null,
            "the census holds no '" + ui + "', so the rule below would be vacuous for it:\n  " +
                string.Join("\n  ", census.Select(c => c.ToString())));
        return match!;
    }

    /// <summary>
    /// The seeded note is bound to the paragraph the reviewer clicked and resolves to that paragraph's own
    /// markdown line — the payload the AGENT is handed, not merely a card that appeared in the panel.
    /// </summary>
    private static void AssertBoundToTheProseAboveTheDiagram(JsonElement annotation, string anchorId)
    {
        Assert.False(
            string.IsNullOrEmpty(anchorId),
            "renderer drift: the paragraph above the diagram carries no anchor, so nothing can be bound to it");
        Assert.Equal(anchorId, annotation.GetProperty("anchorId").GetString());
        Assert.Equal("resolved", annotation.GetProperty("anchorStatus").GetString());

        var line = annotation.GetProperty("sourceLine");
        Assert.True(
            line.ValueKind == JsonValueKind.Number,
            "the annotation reached the agent with no sourceLine (anchorId '" + anchorId + "')");
        Assert.Equal(
            "Prose above the diagram, at the content column's full width.",
            PanZoomDiagramPlan.Split('\n')[line.GetInt32() - 1]);
    }
}
