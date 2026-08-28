using System.Net;
using Charter.Core;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #200 — a re-render must not cost a keyboard reviewer their place.
///
/// <para><b>The mechanism.</b> <c>render()</c> does not update its chrome, it destroys and rebuilds it:
/// <c>renderPanel</c> empties the list and <c>renderMarkers</c> opens with <c>clearMarkers</c>. Both are
/// deliberate — the ownership ledger can only undo exactly what it did (#176), and the sweep-and-rebuild has
/// to finish in one synchronous turn so no frame is painted without a badge (#198) — so the teardown is not
/// what these tests are about. What they are about is the half that was missing: the element holding focus is
/// REMOVED, and the browser drops focus to <c>&lt;body&gt;</c>. That is #168's end state reached by a route
/// the reviewer did not initiate, and the routes are ordinary: a teammate's note arriving over SSE, or
/// <c>hydrateLog()</c> after any local save.</para>
///
/// <para><b>What every measurement here refuses to do</b>, each because it is green while the defect is fully
/// present:</para>
/// <list type="bullet">
///   <item><c>.focus()</c> from the test as the way focus gets where it is asserted. Focus is walked with real
///     <c>Tab</c> presses and the chrome is activated with a real <c>Enter</c>, because the claim is about a
///     reviewer's keyboard, not about an element being programmatically focusable.</item>
///   <item>Calling <c>renderMarkers</c> (or <c>render</c>) to produce the re-render. The re-render is caused
///     the way the product causes it — a second author's record landing in <c>&lt;plan&gt;.review/</c> while
///     the server is running, exactly as a <c>git pull</c> delivers it.</item>
///   <item>Holding an element handle across the yield. <c>renderMarkers</c> destroys and rebuilds, so a
///     reference taken before the note arrives is detached afterwards and would answer for a badge the page is
///     no longer showing (#198). Every assertion re-reads <c>document.activeElement</c>.</item>
/// </list>
///
/// <para><b>The anti-steal half is not optional.</b> Restoring focus is a repair, and a repair that fires when
/// nothing was taken is a worse bug than the one being fixed — #168 made panel focus opt-IN for exactly this
/// reason and left it off for all three automatic opens. So there is a test whose whole content is that a
/// re-render moved nothing.</para>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    // ---- #200: a badge keeps focus across the rebuild that replaces it ------------------------------------

    /// <summary>
    /// Charter #200 — a count badge holding keyboard focus still holds it after a teammate's note rebuilds
    /// the markers, and is still the control the reviewer was about to press.
    ///
    /// <para>Both badge shapes are walked, because they are built by different paths and a fix in one proves
    /// nothing about the other: the paragraph's badge is APPENDED inside the block, and the list's is hung on
    /// a sibling <c>.charter-badge-rail</c> whose offset is measured after the fact (#164/#165).</para>
    ///
    /// <para>The last step is the one that makes this a bug report rather than an attribute check: a real
    /// <c>Enter</c> on the still-focused rail badge has to open the panel AT THAT BLOCK'S notes. Focus that is
    /// restored to something that no longer works would satisfy every <c>activeElement</c> assertion above and
    /// none of the reviewer's purpose.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_badge_keeps_keyboard_focus_when_a_teammates_note_rebuilds_the_markers()
    {
        var directory = NewPlanDirectory("badge-focus-rerender");
        var planPath = Path.Combine(directory, "badge-focus.charter.md");
        await File.WriteAllTextAsync(planPath, BadgeGatePlan);

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

            await OpenBadgeGateAsync(page, server, session);
            await WaitForEventAsync(page, "review-log-loaded");

            var paragraph = await AnchorIdAsync(page, "body > p:nth-of-type(1)");
            var list = await AnchorIdAsync(page, "body > ul");
            var ordered = await AnchorIdAsync(page, "body > ol");
            Assert.False(
                string.IsNullOrEmpty(paragraph) || string.IsNullOrEmpty(list) || string.IsNullOrEmpty(ordered),
                "fixture/renderer drift: the badge-gate plan no longer renders an anchored paragraph, list " +
                    "and numbered list at the top level, so neither badge shape can be exercised.");

            // ---- an APPENDED badge, on the paragraph -------------------------------------------------
            teammate.AppendCreate(
                new ReviewAnchor(paragraph, "element", "an ordinary prose paragraph", null),
                "The teammate's first note, which is what puts a badge on the paragraph.");
            await WaitForBadgeAsync(page, paragraph, teammate);

            await TabToAsync(page, "BUTTON[badge]#" + paragraph);

            var rendersBefore = await CountEventsAsync(page, "markers-rendered");
            teammate.AppendCreate(
                new ReviewAnchor(list, "element", "a plain bullet", null),
                "A second note, arriving while the reviewer is poised on the paragraph's badge.");
            await WaitForBadgeAsync(page, list, teammate);
            Assert.True(
                await CountEventsAsync(page, "markers-rendered") > rendersBefore,
                "Charter #200 — the teammate's note never caused a marker pass, so nothing was rebuilt and " +
                    "this test would pass without asserting anything.");

            await AssertFocusAsync(
                page, "BUTTON[badge]#" + paragraph,
                "a second note rebuilt the markers under an in-block badge");

            // ---- a RAILED badge, on the list --------------------------------------------------------
            // Built by mountBadgeRail on a sibling rail rather than appended inside the block, so it is torn
            // down and rebuilt by a different branch of renderMarkers.
            await TabToAsync(page, "BUTTON[badge]#" + list);

            teammate.AppendCreate(
                new ReviewAnchor(ordered, "element", "an ordered step", null),
                "A third note, arriving while the reviewer is poised on the list's rail badge.");
            await WaitForBadgeAsync(page, ordered, teammate);

            await AssertFocusAsync(
                page, "BUTTON[badge]#" + list,
                "a third note rebuilt the markers under a RAILED badge");

            // ---- and it is still the control the reviewer was about to press -------------------------
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForSelectorAsync(Ui("item"));

            var openedOn = await page.EvaluateAsync<string>(
                "() => { const a = document.activeElement;" +
                "  const item = (a && a.closest) ? a.closest('[data-charter-ui=\"item\"]') : null;" +
                "  return item ? (item.getAttribute('data-anchor-id') || '') : ''; }");
            Assert.True(
                openedOn == list,
                "Charter #200 — Enter on the restored rail badge opened the panel at '" + openedOn +
                    "' rather than at the list's own notes. Focus that comes back on an element which no " +
                    "longer does its job satisfies every activeElement assertion above and none of the " +
                    "reviewer's purpose.");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            CleanupDirectory(directory);
        }
    }

    // ---- #200: the panel's own rebuilt chrome ------------------------------------------------------------

    /// <summary>
    /// Charter #200, the panel half. <c>renderPanel</c> empties the list and rebuilds every card on the same
    /// pass that rebuilds the markers, so a note card — and the controls inside it — lose focus for the same
    /// reason a badge does. The card is a tab stop in its own right (#137) and its <c>Jump</c> is the one
    /// control in the row that reads as an action (#42), so both are walked.
    /// </summary>
    [SkippableFact]
    public async Task A_note_card_and_its_controls_keep_keyboard_focus_when_the_panel_is_rebuilt()
    {
        var directory = NewPlanDirectory("panel-focus-rerender");
        var planPath = Path.Combine(directory, "panel-focus.charter.md");
        await File.WriteAllTextAsync(planPath, BadgeGatePlan);

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

            await OpenBadgeGateAsync(page, server, session);
            await WaitForEventAsync(page, "review-log-loaded");

            var paragraph = await AnchorIdAsync(page, "body > p:nth-of-type(1)");
            var list = await AnchorIdAsync(page, "body > ul");

            var watched = teammate.AppendCreate(
                new ReviewAnchor(paragraph, "element", "an ordinary prose paragraph", null),
                "The note whose card the reviewer is reading when the next one lands.");
            await WaitForCardAsync(page, watched.Id, teammate);

            // Opened by the reviewer's own gesture, which is what puts focus in the panel to begin with (#168).
            await EnsurePanelClosedAsync(page);
            await TabToAsync(page, "BUTTON[panel-toggle]");
            await page.Keyboard.PressAsync("Enter");
            Assert.Equal("DIV[panel]", await FocusIdentityAsync(page));

            // ---- the card itself ---------------------------------------------------------------------
            await TabForwardToAsync(page, "DIV[item]#" + watched.Id);

            // Gate on the product's OWN completion signal, not on the card being in the DOM (Charter #221).
            // The card's presence is a PROXY: the trace shows an arriving note drives SEVERAL render passes
            // (`markers-rendered -> focus-restored -> review-log-loaded -> review-log-changed -> ...`), and
            // the pass that first puts the card in the DOM is not the last one. `focus-restored` is emitted by
            // `restoreChromeFocus` itself, so waiting for it to advance ties this assertion to the thing it is
            // actually about. `atLeast` because the count per note is not fixed.
            var restoresBefore = await CountEventsAsync(page, "focus-restored");
            var second = teammate.AppendCreate(
                new ReviewAnchor(list, "element", "a plain bullet", null),
                "A second note, arriving while the reviewer is reading the first one's card.");
            await WaitForCardAsync(page, second.Id, teammate);
            await WaitForFocusToSettleAsync(page, "DIV[item]#" + watched.Id, restoresBefore);

            await AssertFocusAsync(
                page, "DIV[item]#" + watched.Id,
                "a second note landed while the reviewer was on the first note's CARD",
                restoredKey: "item:" + watched.Id, restoresBefore: restoresBefore);

            // ---- and the control inside it ------------------------------------------------------------
            await TabForwardToAsync(page, "BUTTON[item-jump]#" + watched.Id);

            var jumpRestoresBefore = await CountEventsAsync(page, "focus-restored");
            var third = teammate.AppendCreate(
                new ReviewAnchor(list, "element", "a second bullet", null),
                "A third note, arriving while the reviewer is on that card's Jump.");
            await WaitForCardAsync(page, third.Id, teammate);
            await WaitForFocusToSettleAsync(
                page, "BUTTON[item-jump]#" + watched.Id, jumpRestoresBefore);

            await AssertFocusAsync(
                page, "BUTTON[item-jump]#" + watched.Id,
                "a third note landed while the reviewer was on that card's JUMP -- the control the SDK "
                    + "itself names as the one that can come back DISABLED",
                restoredKey: "item-jump:" + watched.Id, restoresBefore: jumpRestoresBefore);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            CleanupDirectory(directory);
        }
    }

    // ---- #200: the anti-steal control --------------------------------------------------------------------

    /// <summary>
    /// Charter #200's other half — a re-render must move focus it did not take.
    ///
    /// <para>Two cases, and the second is the one that would hurt most. A reviewer reading the plan is focused
    /// on a block of the DOCUMENT, which the SDK does not own and must never claim. A reviewer mid-sentence in
    /// the composer is focused on SDK chrome that <c>render()</c> does NOT rebuild — so "it is ours" is not a
    /// licence to touch it, and yanking the caret out of a half-typed note because a teammate saved something
    /// would be strictly worse than the drop this fix repairs. Both are asserted after a re-render that
    /// really happened, and the composer's typed text and caret position are read back to prove the caret was
    /// not merely re-placed.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_rerender_leaves_focus_alone_when_it_did_not_take_it()
    {
        var directory = NewPlanDirectory("focus-not-stolen");
        var planPath = Path.Combine(directory, "not-stolen.charter.md");
        await File.WriteAllTextAsync(planPath, BadgeGatePlan);

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

            await OpenBadgeGateAsync(page, server, session);
            await WaitForEventAsync(page, "review-log-loaded");

            var paragraph = await AnchorIdAsync(page, "body > p:nth-of-type(1)");
            var list = await AnchorIdAsync(page, "body > ul");

            // ---- focus in the DOCUMENT: a wide table's scroll region (#68 gives it the tab stop) -------
            await TabToAsync(page, "DIV.table-scroll");

            teammate.AppendCreate(
                new ReviewAnchor(paragraph, "element", "an ordinary prose paragraph", null),
                "A note landing while the reviewer is reading the table.");
            await WaitForBadgeAsync(page, paragraph, teammate);

            // Read ONCE: a second read for the message would be a second chance for focus to be somewhere
            // else, and the failure would then describe a moment the assertion did not test.
            var stillOnTheTable = await FocusIdentityAsync(page);
            Assert.True(
                stillOnTheTable == "DIV.table-scroll",
                "Charter #200 — a teammate's note moved focus out of the block the reviewer was reading and " +
                    "onto " + stillOnTheTable + ". Restoring focus is a repair; a repair that fires when " +
                    "nothing was taken is the worse bug (#168 left panel focus opt-in for exactly this " +
                    "reason).");

            // ---- focus in the COMPOSER: SDK chrome that render() does not rebuild ----------------------
            await page.ClickAsync(
                "body > p:nth-of-type(1)", new PageClickOptions { Modifiers = new[] { KeyboardModifier.Alt } });
            await page.WaitForSelectorAsync(Ui("composer-input"));
            await page.Keyboard.TypeAsync("half a sentence");
            Assert.Equal("TEXTAREA[composer-input]", await FocusIdentityAsync(page));

            teammate.AppendCreate(
                new ReviewAnchor(list, "element", "a plain bullet", null),
                "A note landing while the reviewer is mid-sentence in the composer.");
            await WaitForBadgeAsync(page, list, teammate);

            Assert.Equal("TEXTAREA[composer-input]", await FocusIdentityAsync(page));
            var caret = await page.EvaluateAsync<string>(
                "() => { const a = document.activeElement;" +
                "  return a.value + '|' + a.selectionStart + ',' + a.selectionEnd; }");
            Assert.True(
                caret == "half a sentence|15,15",
                "Charter #200 — the composer read '" + caret + "' after a teammate's note arrived, so the " +
                    "re-render disturbed a half-typed note. Expected 'half a sentence|15,15'.");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            CleanupDirectory(directory);
        }
    }

    // ---- #200: the vanished anchor -----------------------------------------------------------------------

    /// <summary>
    /// Charter #200 — when the badge holding focus has no counterpart in the new render, focus is LEFT WHERE
    /// THE BROWSER PUT IT and the absence is disclosed.
    ///
    /// <para><b>The decision this pins.</b> Restoring focus to nothing is worse than leaving it: every landing
    /// place Charter could invent — the toggle, the first badge, the top of the document — is one the reviewer
    /// did not ask for, and moving them there SILENTLY tells a screen-reader user where they now are without
    /// ever telling them that what they were on is gone. So the browser's own outcome stands, and #170's rule
    /// supplies the rest: Charter has no vocabulary for "this is gone, and that was correct", so it says so
    /// instead of leaving it to be inferred. The sentence goes through <c>explain</c>, which opens the panel
    /// without taking focus — the same call #168 makes for every automatic open.</para>
    ///
    /// <para>Both halves are asserted, and the first is the one that would be quietly wrong: focus must be on
    /// nothing, not merely "not on the old badge".</para>
    /// </summary>
    [SkippableFact]
    public async Task A_badge_that_the_new_render_does_not_rebuild_leaves_focus_alone_and_says_so()
    {
        var directory = NewPlanDirectory("focus-anchor-gone");
        var planPath = Path.Combine(directory, "anchor-gone.charter.md");
        await File.WriteAllTextAsync(planPath, BadgeGatePlan);

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

            await OpenBadgeGateAsync(page, server, session);
            await WaitForEventAsync(page, "review-log-loaded");

            var paragraph = await AnchorIdAsync(page, "body > p:nth-of-type(1)");
            var list = await AnchorIdAsync(page, "body > ul");

            var only = teammate.AppendCreate(
                new ReviewAnchor(paragraph, "element", "an ordinary prose paragraph", null),
                "The only note on this block, and its author is about to withdraw it.");
            await WaitForBadgeAsync(page, paragraph, teammate);

            await TabToAsync(page, "BUTTON[badge]#" + paragraph);

            // Withdrawn by its own author, so the fold APPLIES it and markingCounts stops counting the block
            // (#170) — the badge the reviewer is standing on is simply not built this pass. The second write
            // is what the wait hangs on: it is appended after the retract, so any fold that has seen it has
            // seen both.
            teammate.AppendRetract(only.Id, prev: null);
            teammate.AppendCreate(
                new ReviewAnchor(list, "element", "a plain bullet", null),
                "An unrelated note, so this round of the fold has something the page can wait for.");
            await WaitForBadgeAsync(page, list, teammate);

            // The premise, asserted rather than assumed: without it a render that still badged the paragraph
            // would satisfy "focus is on BODY" for a completely different reason and the test would be blind.
            Assert.True(
                await page.Locator(
                    "[data-charter-ui=\"badge\"][data-anchor-id=\"" + paragraph + "\"]").CountAsync() == 0,
                "the retraction did not remove the paragraph's badge, so the vanished-counterpart case this " +
                    "test exists for never arose.");

            var landed = await FocusIdentityAsync(page);
            Assert.True(
                landed == "BODY",
                "Charter #200 — the badge's anchor left the render and Charter moved focus to " + landed +
                    " anyway. There is no landing place here the reviewer asked for, and a silent relocation " +
                    "tells a screen-reader user where they are without ever telling them what became of what " +
                    "they were on.");

            var told = (await page.Locator(Ui("panel-status")).InnerTextAsync()).Trim();
            Assert.True(
                told.Contains("no longer shows a review marker", StringComparison.Ordinal) &&
                told.Contains("left where it is", StringComparison.Ordinal),
                "Charter #200 / #170 — the reviewer's focus was dropped by a change they did not make and " +
                    "nothing said so. The panel status read: '" + told + "'.");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            CleanupDirectory(directory);
        }
    }

    // ---- shared helpers ----------------------------------------------------------------------------------

    /// <summary>
    /// A directory to hold the plan, because a review log lives BESIDE it — the teammate's records land in
    /// <c>&lt;plan&gt;.review/</c> and the server watches that folder.
    /// </summary>
    private static string NewPlanDirectory(string prefix)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "charter-" + prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void CleanupDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is harmless if the OS still holds a handle during a slow dispose.
        }
        catch (UnauthorizedAccessException)
        {
            // Likewise.
        }
    }

    /// <summary>
    /// What has focus, named well enough to tell one badge from another and one card from another:
    /// <c>TAG[data-charter-ui]#key</c>, where the key is the note id for panel chrome and the anchor id for a
    /// badge. Plan content, which carries no <c>data-charter-ui</c>, is named by its class instead.
    ///
    /// <para>Re-read on every assertion rather than captured once: <c>renderMarkers</c> destroys and rebuilds,
    /// so anything held across the yield answers for an element the page is no longer showing (#198).</para>
    /// </summary>
    private static Task<string> FocusIdentityAsync(IPage page)
        => page.EvaluateAsync<string>(
            "() => { const a = document.activeElement;" +
            "  if (!a) return '(null)';" +
            "  if (a === document.body) return 'BODY';" +
            "  if (a === document.documentElement) return 'HTML';" +
            "  const ui = a.getAttribute('data-charter-ui') || '';" +
            "  const owner = a.closest ? a.closest('[data-annotation-id]') : null;" +
            "  const key = a.getAttribute('data-annotation-id') || a.getAttribute('data-anchor-id') ||" +
            "    (owner ? owner.getAttribute('data-annotation-id') : '') || '';" +
            "  const cls = String(a.className || '').trim();" +
            "  return a.tagName + (ui ? '[' + ui + ']' : (cls ? '.' + cls.split(/\\s+/).join('.') : '')) +" +
            "    (key ? '#' + key : ''); }");

    /// <summary>
    /// Charter #221 — a control that REALLY vanished still says so, and says it with `built: false`.
    ///
    /// <para>
    /// The regression guard for splitting <c>focus-not-restored</c> in two. The change stops the SDK claiming
    /// something is gone when it is visibly still there — but the claim is TRUE when it really went, and that
    /// path had to keep working. Sending everything down the new message would trade a false "it is gone" for
    /// a false "it is still here": the same defect pointed the other way.
    /// </para>
    /// <para>
    /// <b>It is a BADGE, not a card, and that is a finding rather than a convenience.</b> The first draft
    /// retracted the note under a reviewer reading its CARD and waited for the vanished case; nothing fired.
    /// The SDK says why: <i>"in the log a retract HIDES the body and KEEPS the thread"</i>, and there is CSS
    /// for a retracted card. A card does not leave the list, so <c>ITEM_GONE</c>'s premise is not reachable
    /// this way — which is itself evidence about #221, recorded on the issue: the failure observed in CI
    /// almost certainly had a counterpart that WAS built.
    /// </para>
    /// <para>
    /// A block's marker does vanish, because a retracted note is not counted — so the badge is the honest
    /// deterministic route to the case <c>BADGE_GONE</c> exists for.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_marker_that_really_vanished_reports_GONE_with_built_false()
    {
        var directory = NewPlanDirectory("badge-focus-vanish");
        var planPath = Path.Combine(directory, "badge-focus.charter.md");
        await File.WriteAllTextAsync(planPath, BadgeGatePlan);

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

            await OpenBadgeGateAsync(page, server, session);
            await WaitForEventAsync(page, "review-log-loaded");

            var paragraph = await AnchorIdAsync(page, "body > p:nth-of-type(1)");

            // The block's ONLY note, so retracting it takes the marker with it.
            var only = teammate.AppendCreate(
                new ReviewAnchor(paragraph, "element", "an ordinary prose paragraph", null),
                "The only note on this block, and therefore the whole reason it has a badge.");
            await WaitForBadgeAsync(page, paragraph, teammate);

            await TabToAsync(page, "BUTTON[badge]#" + paragraph);
            Assert.Equal("BUTTON[badge]#" + paragraph, await FocusIdentityAsync(page));

            // It vanishes under them. No counterpart is built, and a badge has no fallback (#200).
            teammate.AppendRetract(only.Id, null);
            await WaitForEventCountAsync(page, "focus-not-restored", 1, atLeast: true);

            var trace = await page.EvaluateAsync<string[]>("() => window.__charterFocusTrace || []");
            var vanished = Assert.Single(
                trace, e => e.StartsWith("focus-not-restored", StringComparison.Ordinal));

            // built=false is the whole point: "no counterpart was built" told apart from "a counterpart
            // exists but would not take focus".
            Assert.Contains("built=False", vanished, StringComparison.OrdinalIgnoreCase);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            CleanupDirectory(directory);
        }
    }

    /// <summary>
    /// Assert where focus is, and on failure say what the SDK CLAIMED about it (Charter #221).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bare <c>Assert.Equal</c> here produced <c>Actual: "BODY"</c> and nothing else, twice, at two
    /// different assertions, on commits that touched no browser code at all — the second of them a
    /// documentation-only commit. Two occurrences and still no way to tell apart the three things that
    /// produce that same string:
    /// </para>
    /// <list type="number">
    ///   <item><description><c>restoreChromeFocus</c> never ran — nothing emitted.</description></item>
    ///   <item><description>It ran and SUCCEEDED, and focus was lost again afterwards —
    ///     <c>focus-restored</c> in the trace.</description></item>
    ///   <item><description>It ran and REFUSED, for a reason it states — <c>focus-not-restored</c>, which
    ///     <c>landChromeFocus</c> returns when the rebuilt control came back <b>disabled</b> and
    ///     <c>focus()</c> silently did nothing. The SDK names a note's <c>Jump</c> as exactly that case,
    ///     and one of the two failures was on a <c>Jump</c>.</description></item>
    /// </list>
    /// <para>
    /// <b>This does not fix the flake, and is not dressed up as fixing it.</b> It makes the next occurrence
    /// answer the question instead of being a third identical data point. Diagnosing from the symptom alone
    /// is what let Charter #209 spend two rounds being called a flake while it was a product bug.
    /// </para>
    /// </remarks>
    /// <param name="restoredKey">
    /// When supplied, the focus key the SDK must have reported landing on — the MECHANISM, not the state
    /// (Charter #221, prompted by the Guardrails session naming the general move: assert that the child was
    /// terminated, not that it took under 60 seconds).
    /// <para>
    /// <c>document.activeElement</c> being right is a weaker claim than it looks: it is also true when this
    /// render never took focus at all (<c>takeChromeFocus</c> returns null for anything that is not chrome
    /// this pass rebuilds), in which case nothing was preserved and nothing was exercised. The card test had
    /// no vacuity guard at all — the badge test's <c>markers-rendered</c> check is the same idea.
    /// </para>
    /// <para>
    /// It also separates a restore from a FALLBACK. A control keys itself with the card as its fallback, so a
    /// Jump that could not be re-focused lands the reviewer on the card and still emits
    /// <c>focus-restored</c> — with the fallback's key. Asserting the key says which of the two happened.
    /// </para>
    /// </param>
    private static async Task AssertFocusAsync(
        IPage page, string expected, string moment, string? restoredKey = null, int? restoresBefore = null)
    {
        var actual = await FocusIdentityAsync(page);
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            if (restoredKey is null)
            {
                return;
            }

            // A rebuild that never took focus away repairs nothing and announces nothing (restoreChromeFocus
            // returns early on `inDocument`). That is a correct outcome, not a silent pass -- focus is where
            // the reviewer left it because it was never moved -- so the key is asserted only when a restore
            // ACTUALLY happened. Asserting it unconditionally is the same over-strictness that made the #227
            // wait sit out its deadline.
            if (restoresBefore is { } before
                && await CountEventsAsync(page, "focus-restored") <= before)
            {
                return;
            }

            var entries = await page.EvaluateAsync<string[]>("() => window.__charterFocusTrace || []");
            var last = entries.LastOrDefault();
            var wanted = "focus-restored key=" + restoredKey + " ";

            if (last is not null && last.StartsWith(wanted, StringComparison.Ordinal))
            {
                return;
            }

            Assert.Fail(
                $"focus WAS on {expected}, but the SDK did not report putting it there ({moment})."
                    + Environment.NewLine + Environment.NewLine
                    + "expected the last focus event to be: " + wanted.TrimEnd()
                    + Environment.NewLine
                    + "last focus event was: " + (last ?? "(none emitted)")
                    + Environment.NewLine + Environment.NewLine
                    + "A correct activeElement with no matching restore means either this render never "
                    + "took focus -- so nothing was preserved and nothing was exercised -- or the SDK "
                    + "landed on a FALLBACK. Both pass an activeElement check and neither is what this "
                    + "test is named for.");
        }

        var trace = await page.EvaluateAsync<string[]>("() => window.__charterFocusTrace || []");
        var tail = await page.EvaluateAsync<string[]>(
            "() => (window.__charterEvents || []).slice(-12)");

        Assert.Fail(
            $"focus was {actual}, expected {expected} ({moment})." +
            "\n\nfocus events the SDK emitted (empty = restoreChromeFocus never reported anything, which is " +
            "itself the finding):\n  " +
            (trace.Length == 0 ? "(none)" : string.Join("\n  ", trace)) +
            "\n\nlast events on the wire:\n  " + string.Join(" -> ", tail));
    }

    /// <summary>
    /// Wait until the rebuild has finished with focus — EITHER the SDK repaired it, OR it is already where it
    /// belongs because the element survived (Charter #221).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Waiting for <c>focus-restored</c> alone was wrong, and CI caught it.</b> #227 blocked on that count
    /// advancing on every arriving note. It does not: <c>restoreChromeFocus</c> returns EARLY AND SILENTLY
    /// when <c>inDocument(taken.el)</c> — the rebuild did not take focus away, so there is nothing to repair
    /// and nothing to announce. On a run where the third rebuild left the Jump in place, the wait sat out its
    /// full 90s and failed with <i>"emitted 2, expected at least 3"</i>.
    /// </para>
    /// <para>
    /// The trap was named in this issue's own notes before the wait was written, and the wait walked into it
    /// anyway. Both terminal states are correct outcomes of a rebuild, so the gate has to accept either.
    /// </para>
    /// </remarks>
    private static async Task WaitForFocusToSettleAsync(
        IPage page, string expected, int restoresBefore, int timeoutMs = ReadinessTimeoutMs)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await CountEventsAsync(page, "focus-restored") > restoresBefore)
            {
                return;   // repaired, and the key is asserted by the caller
            }

            if (string.Equals(await FocusIdentityAsync(page), expected, StringComparison.Ordinal))
            {
                return;   // never taken away: the element survived this rebuild
            }

            await Task.Delay(25);
        }

        // Neither happened. Let the assertion report it with the full trace rather than failing here with
        // only a count — the trace is what distinguishes the three ways this ends up on BODY.
    }

    /// <summary>
    /// Press Tab from a BLURRED document until <see cref="FocusIdentityAsync"/> reports
    /// <paramref name="wanted"/>, and leave focus there. Real presses — never <c>.focus()</c> and never
    /// <c>tabIndex &gt;= 0</c>, which is true of plenty of things Tab never reaches. Bounded, so a focus trap
    /// fails with the walk it took rather than hanging.
    /// </summary>
    private static async Task TabToAsync(IPage page, string wanted, int maxPresses = 40)
    {
        await page.EvaluateAsync(
            "() => { if (document.activeElement && document.activeElement.blur) document.activeElement.blur();" +
            "  window.scrollTo(0, 0); return null; }");
        await TabForwardToAsync(page, wanted, maxPresses);
    }

    /// <summary>The same walk, continued from wherever focus already is — the panel is reached this way.</summary>
    private static async Task TabForwardToAsync(IPage page, string wanted, int maxPresses = 24)
    {
        var stops = new List<string>();
        for (var i = 0; i < maxPresses; i++)
        {
            await page.Keyboard.PressAsync("Tab");
            var at = await FocusIdentityAsync(page);
            stops.Add(at);
            if (at == wanted)
            {
                return;
            }
        }

        Assert.Fail(
            "'" + wanted + "' was not reachable by Tab within " + maxPresses + " presses:\n  " +
            string.Join("\n  ", stops));
    }

    /// <summary>Close the panel if it is open, so the reviewer's own Enter is what opens it below.</summary>
    private static async Task EnsurePanelClosedAsync(IPage page)
    {
        var open = await page.EvaluateAsync<bool>(
            "() => { const p = document.querySelector('[data-charter-ui=\"panel\"]');" +
            "  return !!p && String(p.className).indexOf('charter-hidden') < 0; }");
        if (open)
        {
            await page.Locator(Ui("panel-close")).ClickAsync();
        }
    }

    /// <summary>
    /// Wait until the teammate's record has reached the page as a BADGE on <paramref name="anchorId"/>,
    /// re-touching their log on a cadence so the test never depends on one filesystem event landing after the
    /// server's watcher was armed. Presence of the badge means the marker pass that built it has completed —
    /// and, since render() is one synchronous turn, that whatever it was going to do about focus is done.
    /// </summary>
    private static Task WaitForBadgeAsync(IPage page, string anchorId, ReviewLogWriter teammate)
        => WaitForSelectorWhileTouchingAsync(
            page,
            "[data-charter-ui=\"badge\"][data-anchor-id=\"" + anchorId + "\"]",
            new[] { teammate.LogPath });

    /// <summary>The same, for a record that has to reach the panel as a card.</summary>
    private static Task WaitForCardAsync(IPage page, string annotationId, ReviewLogWriter teammate)
        => WaitForSelectorWhileTouchingAsync(
            page,
            "[data-charter-ui=\"item\"][data-annotation-id=\"" + annotationId + "\"]",
            new[] { teammate.LogPath });
}
