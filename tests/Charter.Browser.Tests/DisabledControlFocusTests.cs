using System.Net;
using Charter.Core;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #204 — a control that disables itself under keyboard focus must hand the reviewer on, not drop
/// them to <c>&lt;body&gt;</c>.
///
/// <para><b>Why this is not #200.</b> #200 repairs a REBUILD: <c>render()</c> removes the focused element and
/// builds a new one, and the reviewer is put back on the counterpart. Its first guard returns early while the
/// captured element is still in the document — which is correct, and is precisely why it cannot see this
/// case: the element survives, it just stops being focusable underneath the reviewer. Same end state, a
/// different mechanism, and it needs its own answer.</para>
///
/// <para><b>Why it is the worst of the three routes.</b> #168's was a disclosure, #200's is somebody else's
/// note arriving. This one is the reviewer's OWN gesture, every time it works: <c>syncZoomBar</c> disables
/// <c>Reset</c> at fit, so every successful Reset dropped them; <c>syncSendButton</c> disables "Send to
/// agent" on hand-off, at the exact moment the panel writes the sentence they now need to read. And it is
/// invisible to anyone testing with a mouse — WebKit does not focus a button on click at all.</para>
///
/// <para><b>What is asserted, and what is refused.</b> Every measurement is a real <c>Tab</c> walk, a real
/// <c>Enter</c> (or the block's own real key press), and a live <c>document.activeElement</c> read.
/// <c>disabled === true</c> appears only as a PREMISE — proof the state change under test actually
/// happened — never as the rule, because it is green while the defect is fully present. No test here calls
/// <c>.focus()</c>, and no element handle is held across a yield.</para>
///
/// <para><b>The anti-steal half carries the same weight as the fix.</b> "Move focus whenever a control is
/// disabled" would pass every positive test here and be a worse bug than the one being fixed — #168 left
/// panel focus opt-in for exactly that reason. So each site has a counterpart test that produces the SAME
/// state change with the reviewer's focus somewhere else and asserts nothing moved.</para>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    // ---- the zoom bar --------------------------------------------------------------------------------

    /// <summary>
    /// Charter #204 — Reset, pressed at any zoom, disables itself AND <c>−</c>. The reviewer is handed on to
    /// <c>+</c>, which is the one direction that still means anything at fit, rather than being dropped to
    /// the top of the document.
    /// </summary>
    [SkippableFact]
    public async Task Resetting_the_zoom_hands_the_reviewer_to_the_control_that_still_works()
    {
        var planPath = NewPlanPath("zoom-reset-focus");
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

            await OpenZoomableDiagramAsync(page, server, session);

            // At fit both `−` and Reset are already disabled, so Tab walks straight past them to `+`. That
            // is the product's own doing, not a convenience of this test.
            await TabToAsync(page, "BUTTON[diagram-zoom-in]");
            await PressZoomAsync(page, "Enter");
            await PressZoomAsync(page, "Enter");
            Assert.NotEqual("100%", await ZoomLevelAsync(page));

            await TabForwardToAsync(page, "BUTTON[diagram-zoom-reset]");
            await PressZoomAsync(page, "Enter");

            // The premise: Reset really did take itself away. Without this the assertion below would be
            // satisfied by a Reset that did nothing at all.
            Assert.Equal("100%", await ZoomLevelAsync(page));
            Assert.True(
                await page.IsDisabledAsync(Ui("diagram-zoom-reset")),
                "fixture/product drift: Reset is still enabled after resetting to fit, so the control never " +
                    "disabled itself and #204's case did not arise on this run.");

            var landed = await FocusIdentityAsync(page);
            Assert.True(
                landed == "BUTTON[diagram-zoom-in]",
                "Charter #204 — Reset disabled itself under the reviewer's finger and focus went to " +
                    landed + ". Every successful Reset does this, so a keyboard reviewer pays a full " +
                    "re-traverse of the document for the gesture that worked.");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    /// <summary>
    /// Charter #204, the mirror — <c>+</c> disables itself on the press that reaches life-size, and the
    /// reviewer is handed to <c>−</c>. Asserted separately from Reset because it is a different branch of
    /// <c>syncZoomBar</c> and a different ladder: the direction that still means something here is out.
    /// </summary>
    [SkippableFact]
    public async Task Zooming_to_the_ceiling_hands_the_reviewer_to_the_opposite_direction()
    {
        var planPath = NewPlanPath("zoom-ceiling-focus");
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

            await OpenZoomableDiagramAsync(page, server, session);

            await TabToAsync(page, "BUTTON[diagram-zoom-in]");

            // Press until the ceiling takes the control away. Bounded, and it stops the moment focus leaves
            // `+` — pressing on past that point would zoom back OUT through whatever it landed on.
            var at = "BUTTON[diagram-zoom-in]";
            var presses = 0;
            while (at == "BUTTON[diagram-zoom-in]" && presses < 25)
            {
                await PressZoomAsync(page, "Enter");
                at = await FocusIdentityAsync(page);
                presses++;
            }

            Assert.True(
                await page.IsDisabledAsync(Ui("diagram-zoom-in")),
                "fixture/product drift: `+` is still enabled after " + presses + " presses, so the ceiling " +
                    "was never reached and #204's case did not arise on this run (zoom now reads " +
                    await ZoomLevelAsync(page) + ").");

            Assert.True(
                at == "BUTTON[diagram-zoom-out]",
                "Charter #204 — `+` disabled itself at the ceiling and focus went to " + at + " rather " +
                    "than to the one direction the reviewer can still travel.");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    /// <summary>
    /// Charter #204's anti-steal half, for the zoom bar. The SAME state change — Reset and <c>−</c> both
    /// disabled at fit — reached by the reviewer's keyboard on the BLOCK rather than on the bar, so nothing
    /// they were on was taken away and nothing may move.
    ///
    /// <para>Driven through <c>onDiagramKeyDown</c>'s own <c>=</c> and <c>0</c> shortcuts, which is a real
    /// reviewer gesture and keeps focus on the zoomable block throughout. A mouse click on Reset would be a
    /// worse control: Chromium focuses a button on click and WebKit does not, so the two engines would be
    /// asserting different things about the same test.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_zoom_reset_the_reviewer_drove_from_the_block_moves_no_focus()
    {
        var planPath = NewPlanPath("zoom-antisteal");
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

            await OpenZoomableDiagramAsync(page, server, session);

            // The block itself: a tab stop with role="group" and a label, because a region only a mouse can
            // enter hides half a diagram from a keyboard reviewer (#51, the #68 shape).
            await TabToElementAsync(page, "pre.mermaid");

            await PressZoomAsync(page, "Equal");
            await PressZoomAsync(page, "Equal");
            Assert.NotEqual("100%", await ZoomLevelAsync(page));

            await PressZoomAsync(page, "0");

            Assert.Equal("100%", await ZoomLevelAsync(page));
            Assert.True(
                await page.IsDisabledAsync(Ui("diagram-zoom-reset")),
                "fixture/product drift: the block's own `0` did not reset the zoom, so the disable this " +
                    "test is a control for never happened.");

            Assert.True(
                await FocusIsAsync(page, "pre.mermaid"),
                "Charter #204 — disabling Reset and `−` moved the reviewer off the diagram they were " +
                    "reading and onto " + await FocusIdentityAsync(page) + ". Handing focus on is a repair " +
                    "for a control that took it away; it must never claim focus that was somewhere else " +
                    "(#168, #200).");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- "Send to agent" -----------------------------------------------------------------------------

    /// <summary>
    /// Charter #204 — handing a round off disables the button that did it, and the reviewer lands on the
    /// panel's status line, which is where the sentence they now need is written.
    ///
    /// <para><b>The decision, stated.</b> #200's ruling for a VANISHED anchor was "do not move focus, disclose
    /// the absence" — because there was no landing place the reviewer had asked for. This is not that case.
    /// The reviewer pressed Enter on a deliberate control, the control answered by taking itself away, and
    /// the answer to "what happened?" is written one region below it. #168's precedent is that a disclosure
    /// lands on the region carrying it, so that is where they go. <c>sendRound</c> writes the line BEFORE it
    /// disables the button for exactly this reason.</para>
    /// </summary>
    [SkippableFact]
    public async Task Handing_a_round_to_the_agent_hands_focus_to_the_line_that_explains_it()
    {
        var planPath = NewPlanPath("send-focus");
        await File.WriteAllTextAsync(planPath, BadgeGatePlan);

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

            await OpenBadgeGateAsync(page, server, session);
            await SeedNotesAsync(page, await AnchorIdAsync(page, "body > p:nth-of-type(1)"), 1, "a note");
            await WaitForSendEnabledAsync(page);

            // Opened by the reviewer's own gesture, which is what puts focus in the panel at all (#168).
            await EnsurePanelClosedAsync(page);
            await TabToAsync(page, "BUTTON[panel-toggle]");
            await page.Keyboard.PressAsync("Enter");
            await TabForwardToAsync(page, "BUTTON[send-to-agent]");

            var sent = await CountEventsAsync(page, "round-sent");
            var rounds = await CountEventsAsync(page, "round-loaded");
            await page.Keyboard.PressAsync("Enter");
            await WaitForEventCountAsync(page, "round-sent", sent + 1, atLeast: true);
            // The authoritative re-read lands afterwards and syncs the button again. Waiting for it means
            // this asserts where the reviewer ENDS UP, not a position that a follow-up render then undoes.
            await WaitForEventCountAsync(page, "round-loaded", rounds + 1, atLeast: true);

            Assert.True(
                await page.IsDisabledAsync(Ui("send-to-agent")),
                "fixture/product drift: Send is still enabled after a hand-off, so the control never " +
                    "disabled itself and #204's case did not arise on this run.");

            var landed = await FocusIdentityAsync(page);
            Assert.True(
                landed == "DIV[panel-status]",
                "Charter #204 — the reviewer pressed Enter on Send, the button disabled itself, and focus " +
                    "went to " + landed + ". This is the end of the most deliberate gesture in the product " +
                    "and it left them with no way back to the panel but a full re-traverse — #168's " +
                    "measurement all over again.");

            // ...and they landed on a region that is actually SAYING something. A focused blank line would
            // satisfy the assertion above and announce nothing at all.
            var announced = await page.EvaluateAsync<string>(
                "() => (document.activeElement.textContent || '').trim()");
            Assert.False(
                string.IsNullOrEmpty(announced),
                "Charter #204 — focus landed on the status line while it was empty, so the reviewer was " +
                    "moved somewhere that explains nothing.");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    /// <summary>
    /// Charter #204's anti-steal half, for the panel. Send is disabled here by something the reviewer did NOT
    /// do — an attached agent draining the queue, which is the ordinary <c>poll --watch</c> case — while they
    /// are reading a table. Nothing may move.
    ///
    /// <para>This is the control that decides whether the fix is a repair or a claim on focus. The trigger is
    /// "the element being disabled is the one that holds focus", so an automatic re-render can only ever act
    /// on a control the reviewer was already standing on; here they are not, and the same code path runs.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_drain_that_disables_Send_leaves_a_reviewer_reading_the_plan_where_they_are()
    {
        var planPath = NewPlanPath("send-antisteal");
        await File.WriteAllTextAsync(planPath, BadgeGatePlan);

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

            await OpenBadgeGateAsync(page, server, session);
            await SeedNotesAsync(page, await AnchorIdAsync(page, "body > p:nth-of-type(1)"), 1, "a note");
            await WaitForSendEnabledAsync(page);

            // A block of the DOCUMENT — the wide table's scroll region, which #68 made a tab stop. Charter
            // does not own it and must never claim it.
            await TabToAsync(page, "DIV.table-scroll");

            await DrainAnnotationsAsync(server, session);
            await WaitForSendDisabledAsync(page);

            // Read ONCE: a second read for the message would be a second chance for focus to be elsewhere,
            // and the failure would then describe a moment the assertion did not test (#200's rule).
            var landed = await FocusIdentityAsync(page);
            Assert.True(
                landed == "DIV.table-scroll",
                "Charter #204 — an agent's drain disabled Send and Charter moved the reviewer off the table " +
                    "they were reading, onto " + landed + ". Handing focus on is a repair for a control " +
                    "that took focus away; a repair that fires when nothing was taken is the worse bug.");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    /// <summary>
    /// Charter #204 — the decision this file diverges on, pinned so it is visible rather than incidental.
    ///
    /// <para>#168 left panel focus opt-IN so an automatic event never moves the caret, and the obvious
    /// reading of that is "only repair a disable the reviewer caused". This does NOT follow that reading, and
    /// the reason is worth stating: the harm is identical either way. A reviewer poised on Send when an
    /// attached agent's drain empties the queue is dropped to <c>&lt;body&gt;</c> by a change they did not
    /// make — which is #200's premise exactly, not #168's. #168's rule governs focus the SDK does NOT hold;
    /// this fires only when the SDK's own control holds it. So the trigger is the focus, never the cause.</para>
    ///
    /// <para>It also pins the ladder's SECOND rung. There is no hand-off here, so the status line is empty and
    /// unfocusable, and the reviewer lands on the standing hint under the button — the line that always says
    /// why sending is unavailable. A fix that only ever looked at the status line would drop them to
    /// <c>&lt;body&gt;</c> in exactly this case.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_drain_that_disables_Send_under_the_reviewers_finger_still_hands_them_on()
    {
        var planPath = NewPlanPath("send-drain-focus");
        await File.WriteAllTextAsync(planPath, BadgeGatePlan);

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

            await OpenBadgeGateAsync(page, server, session);
            await SeedNotesAsync(page, await AnchorIdAsync(page, "body > p:nth-of-type(1)"), 1, "a note");
            await WaitForSendEnabledAsync(page);

            await EnsurePanelClosedAsync(page);
            await TabToAsync(page, "BUTTON[panel-toggle]");
            await page.Keyboard.PressAsync("Enter");
            await TabForwardToAsync(page, "BUTTON[send-to-agent]");

            // The premise: the status line really is not carrying anything, so this exercises the fallback
            // rung and not the one the hand-off test already covers.
            Assert.True(
                await page.Locator(Ui("panel-status")).IsHiddenAsync(),
                "fixture drift: the panel status line is showing before the drain, so this test would " +
                    "measure the same rung of the ladder as the hand-off case.");

            await DrainAnnotationsAsync(server, session);
            await WaitForSendDisabledAsync(page);

            var landed = await FocusIdentityAsync(page);
            Assert.True(
                landed == "SPAN[panel-hint]",
                "Charter #204 — an agent's drain took Send away while the reviewer was standing on it and " +
                    "focus went to " + landed + ". The cause was not their gesture, but the focus was " +
                    "theirs and on our control, so the drop is the same one #204 exists to stop.");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- shared helpers ------------------------------------------------------------------------------

    private static async Task OpenZoomableDiagramAsync(IPage page, ReviewServer server, ReviewSession session)
    {
        await page.SetViewportSizeAsync(1000, 800);
        await page.GotoAsync(
            CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        await WaitForEventAsync(page, "ready");
        await WaitForDiagramsAsync(page, 2);
        await WaitForEventAsync(page, "diagram-zoomable");
        await page.WaitForSelectorAsync(Ui("diagram-zoom-in"));
    }

    /// <summary>
    /// Press a key that changes the zoom and return once the SDK has reported the change — or once it is
    /// established that there was none to report (the ceiling, a disabled control), which is a legitimate
    /// outcome for several of the presses above. Gated on the <c>diagram-zoom</c> event count, never a sleep;
    /// <c>applyZoom</c> emits it in the same synchronous turn as the focus decision, so a press that DID
    /// change something is fully settled by the time this returns.
    /// </summary>
    private static async Task PressZoomAsync(IPage page, string key)
    {
        var before = await CountEventsAsync(page, "diagram-zoom");
        await page.Keyboard.PressAsync(key);

        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(10_000);
        while (DateTime.UtcNow < deadline)
        {
            if (await CountEventsAsync(page, "diagram-zoom") > before)
            {
                return;
            }

            await Task.Delay(20);
        }
    }

    private static Task<string> ZoomLevelAsync(IPage page)
        => page.InnerTextAsync(Ui("diagram-zoom-level"));

    /// <summary>
    /// Is THIS element the one with focus? Compared by identity rather than by a rendered name, because the
    /// zoomable block's class list changes with the zoom (<c>charter-zoomed</c> comes and goes) and a
    /// name-based assertion would be measuring the state instead of the focus.
    /// </summary>
    private static Task<bool> FocusIsAsync(IPage page, string selector)
        => page.EvaluateAsync<bool>(
            "(sel) => { const el = document.querySelector(sel);" +
            "  return !!el && document.activeElement === el; }",
            selector);

    /// <summary>
    /// <see cref="TabToAsync"/> for an element that has no stable rendered name — real presses from a blurred
    /// document, bounded, failing with the walk it took.
    /// </summary>
    private static async Task TabToElementAsync(IPage page, string selector, int maxPresses = 40)
    {
        await page.EvaluateAsync(
            "() => { if (document.activeElement && document.activeElement.blur) document.activeElement.blur();" +
            "  window.scrollTo(0, 0); return null; }");

        var stops = new List<string>();
        for (var i = 0; i < maxPresses; i++)
        {
            await page.Keyboard.PressAsync("Tab");
            if (await FocusIsAsync(page, selector))
            {
                return;
            }

            stops.Add(await FocusIdentityAsync(page));
        }

        Assert.Fail(
            "'" + selector + "' was not reachable by Tab within " + maxPresses + " presses:\n  " +
            string.Join("\n  ", stops));
    }

}
