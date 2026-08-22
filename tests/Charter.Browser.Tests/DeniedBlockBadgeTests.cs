using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Charter.Core;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #164 — the note-count badge on the blocks that could never host one.
///
/// <para><b>The defect.</b> <c>renderMarkers</c> withheld the badge from <c>UL</c>, <c>OL</c>, <c>TABLE</c> and
/// <c>HR</c> because a <c>&lt;button&gt;</c> is not a legal child of any of them. Those are the blocks a plan
/// collects the MOST feedback on, so the reviewer's notes on a comparison table or a step list were the notes
/// with no visible marker at all — and the reporter's "flickering badge" theory stood only because nothing in
/// the suite had ever watched a count change.</para>
///
/// <para><b>Why every assertion here is shaped the way it is.</b> This block type is unusually good at passing
/// tests it should fail, and two specific reads are FORBIDDEN in this file:</para>
/// <list type="bullet">
///   <item><c>WaitForSelectorAsync(".charter-annotation-badge")</c>, or any other existence check. A button the
///     content model relocated out of its <c>&lt;ul&gt;</c> still exists, still has a box, and satisfies it
///     perfectly. Existence is asserted only as the PRECONDITION of a hit test, never as the result.</item>
///   <item><c>data-charter-annotation-count</c>. The SDK has always set that attribute on denied elements —
///     before this fix as well as after — so an assertion on it is green on master with the bug fully
///     present. Truth comes from <c>GET /api/annotations</c>, the server's own queue, and the badge is
///     compared against THAT.</item>
/// </list>
///
/// <para><b>Two traps that cost the investigation real time, recorded so the next reader does not repeat
/// them.</b> <c>document.elementFromPoint</c> is VIEWPORT-relative, so a badge below the fold hit-tests as a
/// miss however correct it is — every probe here scrolls its badge into view first (the UX walk reported
/// <c>hitsSelf: false</c> for a perfectly good off-screen <c>&lt;ol&gt;</c> on exactly this). And Playwright's
/// own actionability HIDES the scroll defect this fix exists to avoid: <c>ClickAsync</c> calls
/// <c>scrollIntoViewIfNeeded</c> first, so a click-based test can never catch a badge that rode away with
/// <c>.table-scroll</c>'s <c>scrollLeft</c> — <see cref="A_railed_badge_does_not_ride_away_with_the_tables_horizontal_scroll"/>
/// sets <c>scrollLeft</c> from script and re-measures instead.</para>
///
/// <para>Everything here runs on whichever engine <c>CHARTER_BROWSER</c> selects. Content-model relocation is
/// precisely where engines diverge, so WebKit is not optional coverage for this file. The one exception is
/// <see cref="Railed_badges_survive_forced_colors"/>, and it names its own reason.</para>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    // ---- the fixture ---------------------------------------------------------------------------------------

    /// <summary>
    /// Wide enough that the review panel RESERVES its column instead of overlaying it (the SDK's
    /// <c>RESERVE_MIN_VIEWPORT</c> is 1200px). That matters for a top-RIGHT badge specifically: below the
    /// reserve threshold the open panel legitimately covers the content column's right edge, which is the
    /// product's documented design (see <c>LayoutRegressionGateTests</c>) — so hit-testing a corner badge
    /// underneath it would assert a design decision as a regression. The narrow widths are where the ANNOTATED
    /// layout gate measures instead, and it accounts for the panel explicitly.
    /// </summary>
    private const int BadgeGateWidth = 1280;

    private const int BadgeGateHeight = 800;

    /// <summary>
    /// One plan holding every shape #164 has to answer for.
    ///
    /// <para>Deliberate properties, each load-bearing:</para>
    /// <list type="bullet">
    ///   <item><b>No digit anywhere in the rendered text.</b> The copy-fidelity assertion is "the reviewer's
    ///     selection contains no badge digit", and it can only be stated cleanly over text that has none of its
    ///     own. Ordered-list markers are CSS counters, not text nodes, so <c>1.</c> in the source is safe.</item>
    ///   <item><b>Two byte-identical paragraphs.</b> The <c>&lt;hr&gt;</c> pixel diff needs a control that is
    ///     KNOWN to differ when annotated; two paragraphs alike in every pixel but the marker supply it, and
    ///     without that control a "these two images differ" assertion cannot tell a working comparison from a
    ///     broken one.</item>
    ///   <item><b>Two <c>&lt;hr&gt;</c> rules</b>, one annotated and one not — same reason.</item>
    ///   <item><b>A nested list inside <c>:::note</c> and a table inside <c>:::comparison</c>.</b> Neither is a
    ///     top-level anchored block, so neither may gain a rail: their notes belong to the callout and to the
    ///     comparison card. They are the anti-vacuity half — a fix that railed every <c>&lt;ul&gt;</c> and
    ///     <c>&lt;table&gt;</c> in the document would satisfy every positive assertion here and quietly put SDK
    ///     chrome inside two blocks that must stay clean.</item>
    /// </list>
    /// </summary>
    private static readonly string BadgeGatePlan =
        "# Badge gate\n\n" +
        "An ordinary prose paragraph, the control whose badge already worked before this fix.\n\n" +
        "- A plain bullet, the first row of a top-level list\n" +
        "- A second bullet, which must carry a badge of its own\n" +
        "- A third bullet\n\n" +
        "1. An ordered step, the first row of a numbered list\n" +
        "1. A second ordered step\n\n" +
        "| Source | Symbol | Guardrail |\n" +
        "| --- | --- | --- |\n" +
        "| `" + Unbreakable("table_source") + "` " +
        "| `" + Unbreakable("table_symbol") + "` " +
        "| `" + Unbreakable("table_guardrail") + "` |\n\n" +
        "---\n\n" +
        "A paragraph whose twin below it is byte-identical, so an annotated one and a plain one differ only by the marker.\n\n" +
        "A paragraph whose twin below it is byte-identical, so an annotated one and a plain one differ only by the marker.\n\n" +
        "---\n\n" +
        ":::note\n" +
        "A callout whose list is NESTED, so its rows gain no sub-anchors and a note here anchors to the callout.\n\n" +
        "- a nested bullet\n" +
        "- another nested bullet\n" +
        ":::\n\n" +
        ":::comparison\n" +
        "- Option A — keep the loopback review server exactly as it is\n" +
        "- Option B — replace it and pay for the migration from the feature budget\n\n" +
        "| Aspect | Option A | Option B |\n" +
        "| --- | --- | --- |\n" +
        "| Cost | low | high |\n" +
        ":::\n";

    /// <summary>
    /// What the fixture must have RENDERED, measured off the live DOM. Every rule in this file is of the form
    /// "for each railed block, …", which a sweep over an empty or truncated block list satisfies perfectly — so
    /// this is what stops the whole file becoming a no-op the day a renderer change stops emitting one of them.
    /// Same guard, and the same reason, as <c>LayoutRegressionGateTests.RequiredBlockSignatures</c>.
    /// </summary>
    private static readonly string[] RequiredBadgeGateSignatures =
    {
        "BODY > H1",
        "BODY > P",
        "BODY > UL",                 // a plain top-level list — per-<li> sub-anchors (#164 part 1)
        "BODY > OL",                 // and the numbered kind, which a UL-only fix would miss
        "BODY > DIV.table-scroll",   // a wide table inside its anchor-invisible scroll wrapper (#68)
        "BODY > HR",                 // the zero-height case
        "BODY > DIV.note",           // holds the nested list that must gain nothing
        "BODY > DIV.comparison",     // holds the nested table that must gain nothing
    };

    /// <summary>
    /// The blocks that cannot host an appended badge AND can be an anchor element, so each must be badged from
    /// a sibling rail. This is the whole reachable deny-set: <c>THEAD</c>/<c>TBODY</c>/<c>TFOOT</c>/<c>TR</c>
    /// are never stamped with an anchor (the renderer stamps top-level blocks and list-item sub-anchors only),
    /// and <c>IMG</c>/<c>BR</c> are inline content that renders inside the paragraph which anchors it — so a
    /// rail for any of them would assert a case that cannot arise.
    ///
    /// <para><c>Subject</c> is what the accessible name has to call the block. It is listed here rather than
    /// read back from the SDK on purpose: a test that asks the code what it calls itself cannot notice the code
    /// calling two different blocks the same thing.</para>
    /// </summary>
    private sealed record RailedBlock(string Tag, string Selector, string Subject)
    {
        public override string ToString() => Tag + " (" + Selector + ")";
    }

    private static readonly RailedBlock[] RailedBlocks =
    {
        new("UL", "body > ul", "list"),
        new("OL", "body > ol", "list"),
        new("TABLE", "body > div.table-scroll > table", "table"),
        new("HR", "body > hr:nth-of-type(1)", "rule"),
    };

    /// <summary>
    /// The two structures that look like deny-set members and are not: neither is a top-level anchored block,
    /// so neither may gain a rail, and a note written on either belongs to the block that CONTAINS it.
    /// </summary>
    private static readonly string[] NestedNonAnchors =
    {
        "body > div.note ul",
        "body > div.comparison div.table-scroll",
    };

    // ---- the headline sweep --------------------------------------------------------------------------------

    /// <summary>
    /// The gate, swept over every railed block: the badge is HIT-TESTABLE at its own centre, has a real box in
    /// the right corner of the block it names, reads the number the SERVER says is true, and changes when that
    /// number changes.
    ///
    /// <para>Asserted at ONE note and again at THREE, because the two failures are different: a badge that
    /// never renders fails at one, and a badge that renders once and then goes stale fails only at three. The
    /// suite had no equivalent of the second, and its absence is what let a "flickering badge" theory stand
    /// unfalsified.</para>
    /// </summary>
    [SkippableFact]
    public async Task Every_railed_block_shows_a_hit_testable_badge_whose_number_is_the_truth()
    {
        var planPath = NewPlanPath("badge-gate");
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
            await AssertTheFixtureRenderedEveryShapeAsync(page);

            var anchors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var block in RailedBlocks)
            {
                anchors[block.Tag] = await RequireAnchorIdAsync(page, block);
            }

            // ---- one note each -------------------------------------------------------------------
            foreach (var block in RailedBlocks)
            {
                await SeedNotesAsync(page, anchors[block.Tag], 1, "first note on " + block.Tag);
            }

            await AssertEveryRailedBadgeAsync(page, server, session, anchors, expected: 1);

            // ---- and then three, which is where a stale badge is finally visible ------------------
            foreach (var block in RailedBlocks)
            {
                await SeedNotesAsync(page, anchors[block.Tag], 2, "later note on " + block.Tag);
            }

            await AssertEveryRailedBadgeAsync(page, server, session, anchors, expected: 3);

            // ---- the accessible name, computed rather than read off the attribute -----------------
            await AssertRailedBadgeNamesAsync(page, anchors, expected: 3);

            // ---- the anti-vacuity half: nothing nested gained a rail ------------------------------
            await AssertNothingNestedGainedARailAsync(page);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    /// <summary>
    /// Part 1 of the fix, from the reviewer's side: a plain list's rows carry their OWN badge, because
    /// <c>LI</c> was never on the deny-list — and that badge's note round-trips to that row's own source line,
    /// which is the whole reason per-row anchoring is better than a rail on the list would have been.
    ///
    /// <para>The source line is checked against <see cref="SourceMap"/> built from the same markdown, so this
    /// asserts the browser and the renderer agree rather than asserting the browser against itself.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_list_items_badge_anchors_to_that_row_and_that_rows_source_line()
    {
        var planPath = NewPlanPath("badge-li");
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

            // The SECOND bullet, not the first: a plain list and its first item start on the same markdown
            // line, so row one is the one case where "resolved to the right line" could be right by accident.
            const string secondBullet = "- A second bullet, which must carry a badge of its own";
            var expectedAnchor = Block.StableId(secondBullet);
            var expectedLine = SourceMap.Build(BadgeGatePlan).LineForAnchor(expectedAnchor);
            Assert.True(
                expectedLine.HasValue,
                "fixture drift: the renderer no longer gives a plain list's rows their own sub-anchors, so " +
                    "this test is measuring nothing");

            var domAnchor = await page.EvaluateAsync<string>(
                "() => { const li = document.querySelectorAll('body > ul > li')[1];" +
                "  return li ? (li.getAttribute('data-anchor') || li.id || '') : ''; }");
            Assert.Equal(expectedAnchor, domAnchor);

            // The reviewer's real gesture, not a programmatic submit: Alt+click the row.
            await page.ClickAsync(
                "body > ul > li:nth-child(2)", new PageClickOptions { Modifiers = new[] { KeyboardModifier.Alt } });
            await page.WaitForSelectorAsync(Ui("composer-input"));
            await page.FillAsync(Ui("composer-input"), "this row needs a number");
            await page.ClickAsync(Ui("composer-save"));
            await WaitForEventAsync(page, "submitted");

            // The badge lands INSIDE the <li> — no rail needed, because LI was never denied. That is the
            // whole economy of part 1, so it is asserted as the absence of a rail as well as the presence of
            // a badge.
            var probe = await ProbeBadgeAsync(page, expectedAnchor);
            AssertBadgeIsRealAndPlaced(probe, "LI", expected: 1);
            Assert.False(
                probe.RailPresent,
                "a list ITEM can legally contain a button, so it must be badged in place rather than railed: " +
                    probe);

            // ...and the note round-trips to that row's own line, which is what the whole sub-anchor model is
            // for. Read from the server, resolved at drain time, not from the DOM.
            var annotations = await ListAnnotationsAsync(server.Address, session.Key.Value);
            var note = FindByNote(annotations, "this row needs a number");
            Assert.Equal(expectedAnchor, note.GetProperty("anchorId").GetString());
            Assert.Equal("resolved", note.GetProperty("anchorStatus").GetString());
            Assert.Equal(expectedLine!.Value, note.GetProperty("sourceLine").GetInt32());
            Assert.Equal(
                secondBullet,
                BadgeGatePlan.Split('\n')[note.GetProperty("sourceLine").GetInt32() - 1]);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- the scroll invariant ------------------------------------------------------------------------------

    /// <summary>
    /// A table's badge must not ride away with <c>.table-scroll</c>'s horizontal scroll — which is why the rail
    /// is hoisted to sit BEFORE the scroll box rather than inside it. This is the Charter #51 lesson restated:
    /// chrome absolutely positioned inside a scroll container moves with the content unless something pushes it
    /// back, and there is nothing to push a rail badge back because it must not be inside there at all.
    ///
    /// <para><b>Playwright will hide this from you if you let it.</b> <c>ClickAsync</c> calls
    /// <c>scrollIntoViewIfNeeded</c> first, so a click test passes against a badge that has scrolled a thousand
    /// pixels out of the reviewer's sight. The scroll is therefore driven from script and the badge's VIEWPORT
    /// rect is compared before and after.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_railed_badge_does_not_ride_away_with_the_tables_horizontal_scroll()
    {
        var planPath = NewPlanPath("badge-scroll");
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

            var table = RailedBlocks.Single(b => b.Tag == "TABLE");
            var anchorId = await RequireAnchorIdAsync(page, table);
            await SeedNotesAsync(page, anchorId, 2, "a note on the table");

            var before = await ProbeBadgeAsync(page, anchorId);
            AssertBadgeIsRealAndPlaced(before, "TABLE", expected: 2);

            // The fixture's own premise: the region must genuinely have somewhere to scroll, or "the rect did
            // not change" is true because nothing moved.
            var scrolled = await page.EvaluateAsync<double>(
                "() => { const s = document.querySelector('body > div.table-scroll');" +
                "  s.scrollLeft = s.scrollWidth - s.clientWidth; return s.scrollLeft; }");
            Assert.True(
                scrolled > 50,
                "fixture drift: the wide table does not scroll far enough for this to measure anything " +
                    "(scrollLeft reached " + Round(scrolled) + "px)");

            var after = await ProbeBadgeAsync(page, anchorId);
            Assert.NotNull(after.BadgeRect);

            Assert.True(
                Math.Abs(after.BadgeRect!.Left - before.BadgeRect!.Left) <= 0.5
                    && Math.Abs(after.BadgeRect.Top - before.BadgeRect.Top) <= 0.5,
                "Charter #164 — the table's badge moved when the table scrolled, so a reviewer who scrolls to " +
                    "read the far columns loses the marker telling them the block has notes:\n  before " +
                    before.BadgeRect + "\n  after  " + after.BadgeRect);

            // ...and it is still the thing under its own centre after the scroll, not merely still at the
            // same coordinates with the table's content now painted over it.
            Assert.True(after.HitsSelf, "after scrolling, the badge's own centre resolves to " + after.HitPath);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- keyboard reachability -----------------------------------------------------------------------------

    /// <summary>
    /// The badge is reachable by pressing Tab — asserted as an actual walk of the focus order from a blurred
    /// document, never as <c>tabIndex &gt;= 0</c>, which is true of controls no Tab can ever land on.
    ///
    /// <para>For a table the ORDER is load-bearing too. The rail precedes <c>.table-scroll</c>, which is itself
    /// a tab stop (#68), so a keyboard reviewer meets the count before they are dropped into a horizontally
    /// scrollable region — "this block has notes" arrives before "you are now inside a table you can pan". A
    /// rail placed after the region would invert that and be reported as a bug the day someone drives the page
    /// with a keyboard.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_railed_badge_is_reachable_by_Tab_before_the_scroll_region_it_precedes()
    {
        var planPath = NewPlanPath("badge-tab");
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

            var anchors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var block in RailedBlocks)
            {
                anchors[block.Tag] = await RequireAnchorIdAsync(page, block);
                await SeedNotesAsync(page, anchors[block.Tag], 1, "note on " + block.Tag);
            }

            // Bounded, so a focus trap fails the test rather than hanging it.
            var stops = await TabOrderAsync(page, maxPresses: 40);

            foreach (var block in RailedBlocks)
            {
                var at = Array.FindIndex(stops, s => s.Contains("#" + anchors[block.Tag], StringComparison.Ordinal));
                Assert.True(
                    at >= 0,
                    "Charter #164 — " + block + "'s badge is not reachable by Tab within 40 presses, so a " +
                        "keyboard-only reviewer cannot open the notes it counts. Focus order was:\n  " +
                        string.Join("\n  ", stops));
            }

            var badgeAt = Array.FindIndex(
                stops, s => s.Contains("#" + anchors["TABLE"], StringComparison.Ordinal));
            var regionAt = Array.FindIndex(stops, s => s.Contains("table-scroll", StringComparison.Ordinal));

            Assert.True(
                regionAt >= 0,
                "the table's scroll region (#68) is no longer a tab stop, so this ordering rule has nothing " +
                    "to order against:\n  " + string.Join("\n  ", stops));
            Assert.True(
                badgeAt < regionAt,
                "Charter #164 — the table's badge comes AFTER the scroll region in the focus order, so a " +
                    "keyboard reviewer is dropped inside the table before being told it carries notes:\n  " +
                    string.Join("\n  ", stops));

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- forced colors -------------------------------------------------------------------------------------

    /// <summary>
    /// In a forced-colors (Windows high contrast) context every railed block still carries a hit-testable,
    /// non-zero-area marker.
    ///
    /// <para><b>This assertion exists to be permanent.</b> The obvious cheap alternative to a badge — "just put
    /// a box-shadow on the block" — is what <c>.charter-has-annotations</c> already is, and forced colors
    /// strips <c>box-shadow</c> to <c>none</c>. There is no <c>forced-colors</c> or <c>prefers-contrast</c>
    /// handling anywhere in <c>sdk/</c> or <c>assets/</c>, so before this fix a high-contrast reviewer had NO
    /// signal at all on any denied block. A real <c>&lt;button&gt;</c> survives because the forced palette
    /// paints it; a shadow does not. Any future regression toward a shadow-only marker fails here.</para>
    ///
    /// <para><b>Chromium-gated, and the reason is the harness rather than the product.</b> Playwright emulates
    /// <c>forced-colors</c> on Chromium only — WebKit rejects the emulation outright — so on WebKit this would
    /// measure an ordinary context and pass while proving nothing, which is worse than skipping.</para>
    /// </summary>
    [SkippableFact]
    public async Task Railed_badges_survive_forced_colors()
    {
        Skip.IfNot(
            BrowserEngine.IsChromium,
            "Playwright emulates forced-colors on Chromium only; on " + BrowserEngine.Name +
                " this would silently measure an ordinary context.");

        var planPath = NewPlanPath("badge-contrast");
        await File.WriteAllTextAsync(planPath, BadgeGatePlan);

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        try
        {
            var launched = await TryLaunchAsync();
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(
                launched, new BrowserNewContextOptions { ForcedColors = ForcedColors.Active });
            var page = instrumented.Page;

            await OpenBadgeGateAsync(page, server, session);

            // The emulation is real, not assumed: without this the whole test could be measuring an ordinary
            // context because a Playwright option was renamed.
            Assert.True(
                await page.EvaluateAsync<bool>("() => matchMedia('(forced-colors: active)').matches"),
                "forced-colors is not actually active in this context, so this test proves nothing");

            // ...and the cheap alternative really is unavailable here. This is the fact the whole test is
            // built on, so it is measured rather than asserted in prose.
            var shadow = await page.EvaluateAsync<string>(
                "() => { const p = document.querySelector('body > p');" +
                "  p.classList.add('charter-has-annotations');" +
                "  const v = getComputedStyle(p).boxShadow;" +
                "  p.classList.remove('charter-has-annotations'); return v; }");
            Assert.Equal("none", shadow);

            foreach (var block in RailedBlocks)
            {
                var anchorId = await RequireAnchorIdAsync(page, block);
                await SeedNotesAsync(page, anchorId, 1, "high contrast note on " + block.Tag);

                var probe = await ProbeBadgeAsync(page, anchorId);
                Assert.True(
                    probe.HasBadge,
                    "Charter #164 — " + block + " carries no marker under forced colors, where the block's " +
                        "own box-shadow highlight is stripped to nothing: " + probe);
                Assert.True(
                    probe.BadgeRect!.Width > 0 && probe.BadgeRect.Height > 0,
                    "Charter #164 — " + block + "'s marker has no box under forced colors: " + probe);
                Assert.True(
                    probe.HitsSelf,
                    "Charter #164 — " + block + "'s marker is not the thing painted at its own centre under " +
                        "forced colors; " + probe.HitPath + " is: " + probe);
            }

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- the <hr> pixel diff -------------------------------------------------------------------------------

    /// <summary>
    /// An annotated <c>&lt;hr&gt;</c> must not RENDER identically to an unannotated one.
    ///
    /// <para>This is the case that would have failed silently forever. <c>charter.css</c> has no <c>hr</c> rule,
    /// so a thematic break keeps the UA default: a zero-height box with <c>overflow: hidden</c>, which paints no
    /// inset shadow at all. Before #164 the two rules below were BYTE-IDENTICAL images — a note on a
    /// <c>&lt;hr&gt;</c> was completely invisible, and no structural assertion anywhere could see it, because
    /// every structural fact (the class, the count attribute) was present and correct.</para>
    ///
    /// <para>The paragraph pair is the control, and it is not decoration: it proves the comparison can detect a
    /// difference at all. Without it a broken screenshot path — two empty images, a clip that missed — would
    /// report "identical" for the paragraphs too and this test would fail for a reason that has nothing to do
    /// with <c>&lt;hr&gt;</c>.</para>
    /// </summary>
    [SkippableFact]
    public async Task An_annotated_horizontal_rule_does_not_render_identically_to_an_unannotated_one()
    {
        var planPath = NewPlanPath("badge-hr");
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

            // The two rules and the two paragraphs are pairwise identical markup today...
            Assert.Equal(2, await page.Locator("body > hr").CountAsync());
            var twins = page.Locator("body > p").Filter(new LocatorFilterOptions { HasTextString = "byte-identical" });
            Assert.Equal(2, await twins.CountAsync());

            var plainRule = await page.Locator("body > hr").Nth(1).ScreenshotAsync();
            var plainTwin = await twins.Nth(1).ScreenshotAsync();

            // ...and the first of each pair gains a note.
            await SeedNotesAsync(
                page, await RequireAnchorIdAsync(page, RailedBlocks.Single(b => b.Tag == "HR")), 1, "note on the rule");
            await SeedNotesAsync(
                page, await AnchorIdAsync(page, "body > p:nth-of-type(2)"), 1, "note on the twin");

            var markedTwin = await twins.Nth(0).ScreenshotAsync();
            Assert.False(
                markedTwin.AsSpan().SequenceEqual(plainTwin),
                "the CONTROL failed: an annotated paragraph renders identically to its unannotated twin, so " +
                    "this comparison cannot detect a marker at all and the <hr> result below means nothing");

            var markedRule = await page.Locator("body > hr").Nth(0).ScreenshotAsync();
            Assert.False(
                markedRule.AsSpan().SequenceEqual(plainRule),
                "Charter #164 — an annotated <hr> is pixel-for-pixel the same image as an unannotated one, so " +
                    "a reviewer's note on a thematic break is invisible. charter.css has no `hr` rule, so the " +
                    "UA default zero-height overflow:hidden box paints no inset shadow — a structural marker " +
                    "on the element can never be seen here, only a real painted control can.");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- anchor integrity ----------------------------------------------------------------------------------

    /// <summary>
    /// The rail is ANCHOR-INVISIBLE, and inserting it changes no block's identity.
    ///
    /// <para>Three separate ways this could go wrong, all asserted:</para>
    /// <list type="number">
    ///   <item>the rail carrying an <c>id</c>, <c>data-anchor</c> or <c>data-charter-anchor</c> — the exact
    ///     assertion <c>TableScrollContainerTests</c> already makes for <c>.table-scroll</c>, restated here
    ///     because the rail is the new element that could break it;</item>
    ///   <item>the rail being an ANCESTOR of plan content rather than a sibling. <c>closestAnchored</c> tests
    ///     <c>el.closest(UNANCHORABLE)</c> BEFORE it walks for an id, and <c>[data-charter-ui]</c> is in that
    ///     set — so a chrome-marked ancestor would make every Alt+click inside the block resolve to
    ///     <c>null</c>, silently killing annotation on precisely the blocks this fix exists to serve;</item>
    ///   <item>a block's anchor id changing when the rail arrives, which would orphan every note already
    ///     written on it. Checked against the RENDERER's own anchors, not merely against the DOM before and
    ///     after, so a browser that had the wrong ids all along cannot agree with itself.</item>
    /// </list>
    /// </summary>
    [SkippableFact]
    public async Task The_rail_carries_no_anchor_and_leaves_every_blocks_identity_untouched()
    {
        var planPath = NewPlanPath("badge-anchors");
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

            var map = SourceMap.Build(BadgeGatePlan);
            var before = await AnchorIdsAsync(page);

            // The DOM's anchors are the RENDERER's anchors. Without this the two readings below could agree
            // with each other and both be wrong.
            foreach (var anchor in before)
            {
                Assert.True(
                    map.LineForAnchor(anchor).HasValue,
                    "the page carries anchor '" + anchor + "', which the source map cannot resolve — a note " +
                        "written there would orphan at drain time");
            }

            foreach (var block in RailedBlocks)
            {
                await SeedNotesAsync(page, await RequireAnchorIdAsync(page, block), 1, "note on " + block.Tag);
            }

            // 1 + 3: nothing the rail did moved or renamed an anchor.
            Assert.Equal(before, await AnchorIdsAsync(page));

            // The rails really are there, or 2 below sweeps an empty set.
            var rails = await page.Locator(Ui("badge-rail")).CountAsync();
            Assert.Equal(RailedBlocks.Length, rails);

            // 2a — no rail carries an anchor of any kind.
            var leaks = await page.EvaluateAsync<string>(
                "() => Array.prototype.map.call(" +
                "  document.querySelectorAll('[data-charter-ui=\"badge-rail\"]'), function (r) {" +
                "    return (r.id ? 'id=' + r.id + ' ' : '') +" +
                "      (r.hasAttribute('data-anchor') ? 'data-anchor ' : '') +" +
                "      (r.hasAttribute('data-charter-anchor') ? 'data-charter-anchor' : ''); })" +
                "  .filter(function (s) { return s.trim().length > 0; }).join(' | ')");
            Assert.Equal(string.Empty, leaks);

            // 2b — no SDK chrome is an ANCESTOR of an anchored block.
            var swallowed = await page.EvaluateAsync<string>(
                "() => { const bad = [];" +
                "  document.querySelectorAll('[data-charter-ui]').forEach(function (ui) {" +
                "    ui.querySelectorAll('[id],[data-anchor]').forEach(function (el) {" +
                "      if (!el.closest('[data-charter-ui]') || el.closest('[data-charter-ui]') === ui) {" +
                "        bad.push(ui.getAttribute('data-charter-ui') + ' contains ' + el.tagName); } }); });" +
                "  return bad.join(' | '); }");
            Assert.Equal(string.Empty, swallowed);

            // 3 — a note written AFTER the rail exists still round-trips to the block's own line.
            var tableAnchor = await RequireAnchorIdAsync(page, RailedBlocks.Single(b => b.Tag == "TABLE"));
            await page.ClickAsync(
                "body > div.table-scroll > table td",
                new PageClickOptions { Modifiers = new[] { KeyboardModifier.Alt } });
            await page.WaitForSelectorAsync(Ui("composer-input"));
            await page.FillAsync(Ui("composer-input"), "written after the rail arrived");
            await page.ClickAsync(Ui("composer-save"));
            await WaitForEventAsync(page, "submitted");

            var annotations = await ListAnnotationsAsync(server.Address, session.Key.Value);
            var note = FindByNote(annotations, "written after the rail arrived");
            Assert.Equal(tableAnchor, note.GetProperty("anchorId").GetString());
            Assert.Equal("resolved", note.GetProperty("anchorStatus").GetString());
            Assert.Equal(map.LineForAnchor(tableAnchor)!.Value, note.GetProperty("sourceLine").GetInt32());

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- copy fidelity -------------------------------------------------------------------------------------

    /// <summary>
    /// Selecting an annotated block must not copy the badge's digit into the reviewer's selection.
    ///
    /// <para><b>This is not a tidiness assertion; it is a silent-data-loss one.</b> The badge sits inside the
    /// block it counts, so without <c>user-select: none</c> a reviewer dragging across a list or a table pulls
    /// the digit into <c>String(getSelection())</c> — and that string becomes the note's <c>quote</c>. But
    /// <c>blockTextNodes</c> skips every <c>[data-charter-ui]</c> subtree, so the quote it stored can never be
    /// found in the frame it searches: <c>findQuoteRange</c> returns <c>-1</c>, the highlight never draws, and
    /// nothing anywhere reports a problem. The reviewer sees a note that will not light up its own text and has
    /// no way to know why.</para>
    ///
    /// <para>Both halves are asserted, because either alone can pass while the other is broken: the selection
    /// carries no digit, AND the offsets the server stored are a real ordered span. A <c>start</c>/<c>end</c>
    /// pair of <c>null</c> is the SDK saying honestly that it could not locate its own quote — which is the
    /// symptom, made visible.</para>
    /// </summary>
    [SkippableFact]
    public async Task Selecting_an_annotated_block_never_copies_the_badges_digit()
    {
        var planPath = NewPlanPath("badge-copy");
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

            // The fixture's premise: nothing in the PLAN has a digit of its own, so any digit that turns up in
            // a selection below came from SDK chrome.
            //
            // Scoped to the plan's own top-level blocks, and that scoping is not fussiness — a whole-body text
            // scan measures the served page's INLINED SCRIPT, which is the SDK's own source code, comments and
            // all. (It also picks up the panel toggle, which reads "Notes 0".) This is the same trap that makes
            // a naive `DoesNotContain("tabindex", body)` fail on the vendored Mermaid blob.
            var pristine = await page.EvaluateAsync<string>(Probe(
                "const out = [];" +
                "topLevel().forEach(block => (function walk(n) {" +
                "  if (n.nodeType === 1) {" +
                "    if (chrome(n) || n.tagName === 'SCRIPT' || n.tagName === 'STYLE') return;" +
                "    for (const c of n.childNodes) walk(c);" +
                "    return; }" +
                "  if (n.nodeType === 3) out.push(n.nodeValue || ''); })(block));" +
                "return out.join('');"));
            Assert.DoesNotMatch(new Regex(@"\d"), pristine);
            Assert.Contains("A third bullet", pristine, StringComparison.Ordinal);

            // Three blocks that a reviewer really does drag across, one badged in place (the paragraph), one
            // badged in place as a sub-anchor (a list row), and one railed (the table).
            var cases = new[]
            {
                ("body > p:nth-of-type(1)", "the paragraph"),
                ("body > ul > li:nth-child(2)", "a list row"),
                ("body > div.table-scroll > table", "the table"),
            };

            foreach (var (selector, label) in cases)
            {
                var anchorId = await AnchorIdAsync(page, selector);
                await SeedNotesAsync(page, anchorId, 3, "a note on " + label);

                var selected = await page.EvaluateAsync<string>(
                    "(sel) => { const el = document.querySelector(sel);" +
                    "  const r = document.createRange(); r.selectNodeContents(el);" +
                    "  const s = window.getSelection(); s.removeAllRanges(); s.addRange(r);" +
                    "  return String(s); }",
                    selector);

                Assert.False(
                    string.IsNullOrWhiteSpace(selected),
                    "nothing was selected in " + label + ", so this case measured nothing");
                Assert.DoesNotMatch(
                    new Regex(@"\d"),
                    selected);
            }

            // ---- the control: this test can actually SEE a badge digit -------------------------
            //
            // Everything above is an assertion that something is absent, which an empty selection, a missing
            // badge, or a badge that quietly moved out of the block would all satisfy. So the guarantee is
            // suspended for one measurement — the badge is made selectable again, exactly as it was before
            // #164 — and the digit MUST come back. Without this the whole test could be green for reasons
            // that have nothing to do with user-select.
            var relaxed = await page.AddStyleTagAsync(new PageAddStyleTagOptions
            {
                Content = ".charter-annotation-badge { -webkit-user-select: text !important;" +
                          " user-select: text !important; }",
            });

            var leaked = await page.EvaluateAsync<string>(
                "() => { const el = document.querySelector('body > p:nth-of-type(1)');" +
                "  const r = document.createRange(); r.selectNodeContents(el);" +
                "  const s = window.getSelection(); s.removeAllRanges(); s.addRange(r);" +
                "  return String(s); }");
            Assert.Matches(
                new Regex(@"\d"),
                leaked);

            await relaxed.EvaluateAsync("el => { el.remove(); return null; }");

            // The consequence half, on the paragraph: a text-range note taken from a selection made WHILE a
            // badge was present must still carry a real ordered span. Null offsets are the SDK reporting that
            // it could not find its own quote — the exact failure this test exists for.
            await page.EvaluateAsync(
                "() => { const el = document.querySelector('body > p:nth-of-type(1)');" +
                "  const r = document.createRange(); r.selectNodeContents(el);" +
                "  const s = window.getSelection(); s.removeAllRanges(); s.addRange(r);" +
                "  document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true })); return null; }");
            await page.WaitForSelectorAsync(Ui("composer-input"));
            await page.FillAsync(Ui("composer-input"), "a quote taken across the badge");
            await page.ClickAsync(Ui("composer-save"));
            await WaitForEventAsync(page, "submitted");

            var annotations = await ListAnnotationsAsync(server.Address, session.Key.Value);
            var note = FindByNote(annotations, "a quote taken across the badge");
            Assert.Equal("text-range", note.GetProperty("kind").GetString());

            var start = note.GetProperty("start");
            var end = note.GetProperty("end");
            Assert.True(
                start.ValueKind == JsonValueKind.Number && end.ValueKind == JsonValueKind.Number,
                "Charter #164 — the SDK could not locate its own quote inside the block, so it recorded null " +
                    "offsets. That is what a badge digit in the selection does: the quote is stored from a " +
                    "frame that INCLUDES the badge and searched in one that EXCLUDES it, so the highlight " +
                    "silently never draws. quote was: " + note.GetProperty("quote"));
            Assert.True(end.GetInt32() > start.GetInt32());

            // ...and the highlight really does draw when the reviewer opens THAT note. Located by the note's
            // own server-assigned id, not by position: the panel holds ten cards here, and only this one is a
            // text-range — clicking whichever card happens to be first would test an element note, which never
            // draws an overlay whether the fix is in place or not.
            var id = note.GetProperty("id").GetString();
            await page.Locator(Ui("item") + "[data-annotation-id=\"" + id + "\"]")
                .Locator(Ui("item-note")).ClickAsync();
            await WaitForEventAsync(page, "annotation-jumped");
            Assert.True(
                await page.Locator(Ui("overlay-rect")).CountAsync() > 0,
                "Charter #164 — the note's quote could not be found again in the block it was written on, so " +
                    "opening it highlighted nothing. That is what a badge digit inside the selection does: " +
                    "findQuoteRange searches a frame that excludes [data-charter-ui] for a quote captured " +
                    "from one that included it, returns -1, and fails silently. quote was: " +
                    note.GetProperty("quote"));

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- shared assertions ---------------------------------------------------------------------------------

    private static async Task AssertEveryRailedBadgeAsync(
        IPage page,
        ReviewServer server,
        ReviewSession session,
        IReadOnlyDictionary<string, string> anchors,
        int expected)
    {
        var truth = await ListAnnotationsAsync(server.Address, session.Key.Value);

        foreach (var block in RailedBlocks)
        {
            var anchorId = anchors[block.Tag];

            // TRUTH comes from the server's own queue, never from the DOM attribute the badge was rendered
            // from. data-charter-annotation-count is set on denied elements today, before this fix as well as
            // after, so an assertion against it is green while the bug is fully present.
            var actual = truth.EnumerateArray()
                .Count(a => string.Equals(a.GetProperty("anchorId").GetString(), anchorId, StringComparison.Ordinal));
            Assert.Equal(expected, actual);

            var probe = await ProbeBadgeAsync(page, anchorId);
            AssertBadgeIsRealAndPlaced(probe, block.ToString(), expected);

            Assert.True(
                probe.RailPresent,
                "Charter #164 — " + block + " must be badged from a SIBLING rail, because a <button> is not a " +
                    "legal child of it: " + probe);
            Assert.True(
                probe.RailIsPreviousSibling,
                "Charter #164 — " + block + "'s rail is not the previous sibling of the element it names. For " +
                    "a top-level table the rail must sit before div.table-scroll, or the badge rides away " +
                    "with scrollLeft (the #51 lesson): " + probe);
        }
    }

    /// <summary>
    /// The per-badge rules that hold for EVERY badged block, railed or not.
    ///
    /// <para>Note what is deliberately not here: no <c>WaitForSelectorAsync</c>, and no read of
    /// <c>data-charter-annotation-count</c>. Existence appears only as the precondition of the hit test.</para>
    /// </summary>
    private static void AssertBadgeIsRealAndPlaced(BadgeProbe probe, string label, int expected)
    {
        Assert.True(probe.Found, "the anchored block for " + label + " is not in the page at all: " + probe);
        Assert.True(
            probe.HasBadge,
            "Charter #164 — " + label + " carries " + expected + " note(s) and shows no badge: " + probe);

        // (3) the number is the truth, and (4) it TRACKS the truth as notes arrive.
        Assert.Equal(expected.ToString(CultureInfo.InvariantCulture), probe.Text);

        var badge = probe.BadgeRect!;
        var container = probe.ContainerRect!;
        var block = probe.BlockRect!;

        // (2) a real box. 20x19 is what a correct badge measures; the failure mode worth naming is a squashed
        // 20x8, which is what you get when `top` and `bottom` are BOTH set on an absolutely positioned button.
        // Asserting area alone would pass against it, so the height is asserted on its own.
        Assert.True(badge.Width > 0 && badge.Height > 0, label + "'s badge has no box: " + probe);
        Assert.True(
            badge.Height >= 14,
            "Charter #164 — " + label + "'s badge is squashed to " + Round(badge.Height) + "px tall. A correct " +
                "badge measures about 20x19; 20x8 is what you get when `top` and `bottom` are BOTH resolved " +
                "on an absolutely positioned button, which is the live hazard here because the rail computes " +
                "`top` and must pin `bottom` to auto (resolved top:" + probe.CssTop + " bottom:" +
                probe.CssBottom + "): " + probe);

        // Out of flow, which is what lets the rail be zero-height and shift nothing. Asserted; the offsets
        // themselves are only REPORTED, because getComputedStyle resolves `bottom: auto` to a used pixel
        // value on an absolutely positioned box — so reading it back can never distinguish the declaration
        // that is correct from the one that squashes the button. The height above is that distinction,
        // measured where it actually shows.
        Assert.Equal("absolute", probe.CssPosition);

        // The panel would be a legitimate occluder at a narrow width — the product's documented design — so a
        // hit-test failure caused by it must not be reported as this fix regressing.
        Assert.False(
            probe.UnderPanel,
            "the review panel is covering " + label + "'s badge, so the hit test below would fail for a " +
                "reason unrelated to #164. This gate runs at " + BadgeGateWidth + "px precisely so the panel " +
                "reserves its column instead of overlaying it: " + probe);

        // (1) HIT-TESTED, not merely present. A button the content model relocated out of its <ul> still
        // exists and still has a box; what it does not do is answer for its own centre.
        Assert.True(
            probe.HitsSelf,
            "Charter #164 — " + label + "'s badge is not what is painted at its own centre; " + probe.HitPath +
                " is. A badge that exists, has a box, and is not hittable is a badge the reviewer cannot " +
                "click: " + probe);

        // (2) contained. For a table the containing box is .table-scroll rather than the <table>, because a
        // wide table's own rect extends past the reviewer's view and containment within it would be trivially
        // satisfiable — the honest box is the one the reviewer actually sees.
        Assert.True(
            badge.Left >= container.Left - 2 && badge.Right <= container.Right + 2,
            "Charter #164 — " + label + "'s badge sits outside the block it names, horizontally: " + probe);

        if (block.Height >= badge.Height)
        {
            // The top-right corner, as specified.
            Assert.True(
                badge.Top >= block.Top - 4 && badge.Bottom <= block.Bottom + 2,
                "Charter #164 — " + label + "'s badge is not on the block it names, vertically: " + probe);
            Assert.True(
                Math.Abs(badge.Right - container.Right) <= 12,
                "Charter #164 — " + label + "'s badge is not in the block's top-RIGHT corner: " + probe);
        }
        else
        {
            // A zero-height block has no corner to sit in, so the badge is centred ON the rule. Asserted as
            // centring rather than as containment, which cannot hold when the badge is taller than the block.
            var badgeCentre = badge.Top + (badge.Height / 2);
            var blockCentre = block.Top + (block.Height / 2);
            Assert.True(
                Math.Abs(badgeCentre - blockCentre) <= 2,
                "Charter #164 — " + label + " has no height, so its badge must be CENTRED on it rather than " +
                    "hung off a corner that does not exist. Badge centre " + Round(badgeCentre) +
                    ", block centre " + Round(blockCentre) + ": " + probe);
        }

        // (11)'s precondition, asserted structurally so the copy test's failure can be told apart from a
        // selection that simply went wrong.
        Assert.True(
            probe.UserSelect is "none",
            "Charter #164 — " + label + "'s badge is selectable, so its digit lands in the quote of any note " +
                "written by dragging across the block — and blockTextNodes then cannot find that quote " +
                "again, so the highlight silently never draws (computed user-select: " + probe.UserSelect + ")");
    }

    /// <summary>
    /// The accessible NAME, computed by the engine rather than read off <c>aria-label</c> — a raw attribute
    /// read would pass against a name no assistive technology ever announces.
    ///
    /// <para>Four properties, and the fourth is the one an implementation is most likely to miss: the rail
    /// reads BEFORE its block, so the name must point forward ("the following table"), it must carry the count
    /// a sighted reviewer sees, it must say what kind of block is coming, and no two badge names on the page
    /// may be identical — a reviewer hearing "3 review notes on the following list" twice has been told nothing
    /// that distinguishes the two.</para>
    /// </summary>
    private static async Task AssertRailedBadgeNamesAsync(
        IPage page, IReadOnlyDictionary<string, string> anchors, int expected)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var block in RailedBlocks)
        {
            // Located by anchor, named by the accessibility tree.
            var badge = page.Locator(Ui("badge") + "[data-anchor-id=\"" + anchors[block.Tag] + "\"]");
            var snapshot = await badge.AriaSnapshotAsync();
            var name = ButtonNameFrom(snapshot);

            Assert.False(
                string.IsNullOrWhiteSpace(name),
                "Charter #164 — " + block + "'s badge announces as nothing; its aria snapshot was: " + snapshot);

            Assert.Contains(expected.ToString(CultureInfo.InvariantCulture), name, StringComparison.Ordinal);
            Assert.Contains("following", name, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(block.Subject, name, StringComparison.OrdinalIgnoreCase);

            names[block.Tag] = name;
        }

        var collisions = names
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(" and ", group.Select(p => p.Key)) + " both announce as \"" + group.Key + "\"")
            .ToArray();

        Assert.True(
            collisions.Length == 0,
            "Charter #164 — two railed badges on the same page have the SAME accessible name, so a reviewer " +
                "using a screen reader cannot tell which block each one belongs to:\n  " +
                string.Join("\n  ", collisions) +
                "\nThe block-type word is what has to distinguish them (a numbered list is not the same " +
                "thing as a bullet list).");
    }

    /// <summary>
    /// The anti-vacuity half. Neither the list inside <c>:::note</c> nor the table inside
    /// <c>:::comparison</c> is a top-level anchored block, so neither may gain a rail — a note on either
    /// belongs to the callout or the comparison card that contains it, and railing them would put SDK chrome
    /// inside two blocks the anchoring layer must walk cleanly through.
    /// </summary>
    private static async Task AssertNothingNestedGainedARailAsync(IPage page)
    {
        foreach (var selector in NestedNonAnchors)
        {
            var found = await page.EvaluateAsync<string>(
                "(sel) => { const el = document.querySelector(sel);" +
                "  if (!el) return 'MISSING';" +
                "  const prev = el.previousElementSibling;" +
                "  const railed = prev && prev.getAttribute && prev.getAttribute('data-charter-ui') === 'badge-rail';" +
                "  const inside = el.querySelector('[data-charter-ui=\"badge-rail\"]');" +
                "  return (railed ? 'RAILED ' : '') + (inside ? 'CONTAINS-RAIL' : ''); }",
                selector);

            Assert.False(
                string.Equals(found, "MISSING", StringComparison.Ordinal),
                "fixture drift: '" + selector + "' is no longer rendered, so this anti-vacuity control is " +
                    "checking nothing");
            Assert.Equal(string.Empty, found.Trim());
        }
    }

    /// <summary>
    /// The fixture guard, over the RENDERER's structure only.
    ///
    /// <para>Classes the SDK adds at runtime are stripped before matching, and that is not cosmetic: this same
    /// guard runs on an unannotated page and on an annotated one, and <c>.charter-has-annotations</c> lands on
    /// whichever blocks happen to carry notes. Letting it into the signature would make "did the renderer emit
    /// a <c>&lt;ul&gt;</c>" depend on whether anybody had commented on it yet.</para>
    /// </summary>
    private static async Task AssertTheFixtureRenderedEveryShapeAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(Probe(
            "const plain = el => { const cls = String(el.className || '').trim().split(/\\s+/)" +
            "  .filter(c => c && c.indexOf('charter-') !== 0);" +
            "  return el.tagName + (cls.length ? '.' + cls.join('.') : ''); };" +
            "return JSON.stringify(topLevel().map(el =>" +
            "  (el.parentElement ? plain(el.parentElement) : '(root)') + ' > ' + plain(el)));"));
        var rendered = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        var missing = RequiredBadgeGateSignatures.Except(rendered, StringComparer.Ordinal).ToArray();

        Assert.True(
            missing.Length == 0,
            "fixture drift — the badge gate is no longer sweeping every shape it exists to cover, so its " +
                "\"every railed block is badged\" result is worth less than it looks. Missing:\n  " +
                string.Join("\n  ", missing) + "\nRendered:\n  " + string.Join("\n  ", rendered));

        // Exact, for the same reason LayoutRegressionGateTests pins its own count: a block appearing twice
        // where one was expected changes what every rule above is measured over.
        Assert.Equal(11, rendered.Length);
    }

    // ---- the in-page probe ---------------------------------------------------------------------------------

    private sealed record BadgeRect(double Left, double Top, double Right, double Bottom, double Width, double Height)
    {
        public override string ToString()
            => Round(Width) + "x" + Round(Height) + " at (" + Round(Left) + "," + Round(Top) + ")-(" +
               Round(Right) + "," + Round(Bottom) + ")";
    }

    private sealed record BadgeProbe(
        bool Found,
        bool HasBadge,
        string Text,
        BadgeRect? BadgeRect,
        BadgeRect? ContainerRect,
        BadgeRect? BlockRect,
        bool HitsSelf,
        string HitPath,
        bool RailPresent,
        bool RailIsPreviousSibling,
        double RailHeight,
        bool UnderPanel,
        string CssTop,
        string CssBottom,
        string CssPosition,
        string UserSelect,
        string BlockPath,
        string ContainerPath)
    {
        public override string ToString()
            => "block " + BlockPath + " " + BlockRect + "; container " + ContainerPath + " " + ContainerRect +
               "; badge '" + Text + "' " + BadgeRect + " hitsSelf=" + HitsSelf + " (" + HitPath + ")" +
               "; rail=" + RailPresent + " prevSibling=" + RailIsPreviousSibling + " h=" + Round(RailHeight) +
               "; user-select=" + UserSelect;
    }

    private static Task<BadgeProbe> ProbeBadgeAsync(IPage page, string anchorId)
        => page.EvaluateAsync<string>(BadgeProbeScript, anchorId).ContinueWith(
            task => JsonSerializer.Deserialize<BadgeProbe>(
                        task.Result, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("the badge probe returned nothing"),
            TaskContinuationOptions.OnlyOnRanToCompletion);

    private static string BadgeProbeScript => "async (id) => {" + ProbeScript + BadgeProbeBody + "}";

    /// <summary>
    /// One measurement of one badge, taken with the badge SCROLLED INTO VIEW — which is not a convenience.
    /// <c>document.elementFromPoint</c> is viewport-relative, so a badge below the fold reports as un-hittable
    /// however correct it is, and the UX walk that preceded this work hit exactly that and filed a healthy
    /// <c>&lt;ol&gt;</c> as broken.
    ///
    /// <para>The badge is located WITHOUT assuming where it lives — by its anchor id first, then inside the
    /// block, then in the preceding rail — so the probe cannot be the thing that decides the shape it is
    /// supposed to be measuring.</para>
    /// </summary>
    private const string BadgeProbeBody =
        "const frame = () => new Promise(r => requestAnimationFrame(() => r()));" +
        "const rect = r => ({ left: r.left, top: r.top, right: r.right, bottom: r.bottom," +
        "  width: r.width, height: r.height });" +
        "const none = { found: false, hasBadge: false, text: '', badgeRect: null, containerRect: null," +
        "  blockRect: null, hitsSelf: false, hitPath: '', railPresent: false, railIsPreviousSibling: false," +
        "  railHeight: -1, underPanel: false, cssTop: '', cssBottom: '', cssPosition: '', userSelect: ''," +
        "  blockPath: '', containerPath: '' };" +
        "const block = document.getElementById(id) ||" +
        "  document.querySelector('[data-anchor=\"' + id + '\"], [data-charter-anchor=\"' + id + '\"]');" +
        "if (!block) return JSON.stringify(none);" +
        // The box a reviewer actually sees. A wide <table>'s own rect runs past the edge of its scroll region,
        // so containment measured against it would be satisfied by a badge nobody can see.
        "const container = block.closest('.table-scroll') || block;" +
        "let badge = document.querySelector('[data-charter-ui=\"badge\"][data-anchor-id=\"' + id + '\"]');" +
        "if (!badge) badge = block.querySelector('[data-charter-ui=\"badge\"]');" +
        "if (!badge) { const prev = container.previousElementSibling;" +
        "  if (prev) badge = prev.querySelector('[data-charter-ui=\"badge\"]'); }" +
        "if (!badge) return JSON.stringify(Object.assign({}, none, { found: true," +
        "  blockPath: path(block), containerPath: path(container)," +
        "  blockRect: rect(block.getBoundingClientRect())," +
        "  containerRect: rect(container.getBoundingClientRect()) }));" +
        "badge.scrollIntoView({ block: 'center' });" +
        "await frame(); await frame();" +
        "const b = badge.getBoundingClientRect();" +
        "const cx = b.left + (b.width / 2), cy = b.top + (b.height / 2);" +
        "const hit = document.elementFromPoint(cx, cy);" +
        "const rail = badge.closest('[data-charter-ui=\"badge-rail\"]');" +
        "const panel = document.querySelector('[data-charter-ui=\"panel\"]');" +
        "const pr = panel ? panel.getBoundingClientRect() : null;" +
        "const cs = getComputedStyle(badge);" +
        "return JSON.stringify({" +
        "  found: true, hasBadge: true, text: (badge.textContent || '').trim()," +
        "  badgeRect: rect(b), containerRect: rect(container.getBoundingClientRect())," +
        "  blockRect: rect(block.getBoundingClientRect())," +
        "  hitsSelf: !!(hit && (hit === badge || badge.contains(hit)))," +
        "  hitPath: hit ? path(hit) : '(nothing hittable)'," +
        "  railPresent: !!rail," +
        "  railIsPreviousSibling: !!(rail && rail.nextElementSibling === container)," +
        "  railHeight: rail ? rail.getBoundingClientRect().height : -1," +
        "  underPanel: !!(pr && pr.width > 0 && cx >= pr.left && cx <= pr.right && cy >= pr.top && cy <= pr.bottom)," +
        "  cssTop: cs.top, cssBottom: cs.bottom, cssPosition: cs.position," +
        "  userSelect: cs.userSelect || cs.webkitUserSelect || ''," +
        "  blockPath: path(block), containerPath: path(container) });";

    // ---- small shared helpers ------------------------------------------------------------------------------

    private static string NewPlanPath(string prefix)
        => Path.Combine(Path.GetTempPath(), "charter-" + prefix + "-" + Guid.NewGuid().ToString("N") + ".charter.md");

    private static void Cleanup(string planPath)
    {
        try
        {
            if (File.Exists(planPath))
            {
                File.Delete(planPath);
            }
        }
        catch (IOException)
        {
            // A leaked temp file is harmless if the OS still holds a handle during a slow server dispose.
        }
    }

    private static async Task OpenBadgeGateAsync(IPage page, ReviewServer server, ReviewSession session)
    {
        await page.SetViewportSizeAsync(BadgeGateWidth, BadgeGateHeight);
        await page.GotoAsync(
            CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        await WaitForEventAsync(page, "ready");
    }

    /// <summary>The anchor id the SDK would resolve for this element — its <c>id</c>, or its sub-anchor.</summary>
    private static Task<string> AnchorIdAsync(IPage page, string selector)
        => page.EvaluateAsync<string>(
            "(sel) => { const el = document.querySelector(sel);" +
            "  return el ? (el.id || el.getAttribute('data-anchor') || el.getAttribute('data-charter-anchor') || '') : ''; }",
            selector);

    /// <summary>
    /// The same, but it FAILS with an explanation rather than returning empty. A denied block with no anchor at
    /// all cannot be exercised by any rule in this file, and the reason matters: if a block-level anchor were
    /// ever deliberately removed (the list case was discussed), the sweep must be edited and the decision
    /// recorded — not left to fail as an unexplained missing badge.
    /// </summary>
    private static async Task<string> RequireAnchorIdAsync(IPage page, RailedBlock block)
    {
        var anchorId = await AnchorIdAsync(page, block.Selector);
        Assert.False(
            string.IsNullOrEmpty(anchorId),
            "fixture/renderer drift: " + block + " carries no anchor, so it can never be an annotation target " +
                "and the whole railed-badge rule is unassertable for it. If that block-level anchor was " +
                "removed on purpose, delete this entry from " + nameof(RailedBlocks) + " and say why.");
        return anchorId;
    }

    /// <summary>Every anchor the page exposes, ordered, so two readings can be compared exactly.</summary>
    private static async Task<string[]> AnchorIdsAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(Probe(
            "const ids = [];" +
            "topLevel().forEach(el => { if (anchored(el)) ids.push(el.id);" +
            "  el.querySelectorAll('[id],[data-anchor]').forEach(k => {" +
            "    const v = k.id || k.getAttribute('data-anchor');" +
            "    if (v && ANCHOR.test(v)) ids.push(v); }); });" +
            "return JSON.stringify(ids);"));
        return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
    }

    /// <summary>
    /// Submit <paramref name="count"/> whole-block notes through the SDK's own path, so each one crosses the
    /// real browser→server boundary and the panel/markers re-render exactly as they do for a reviewer.
    /// Gated on the <c>submitted</c> event COUNT, never a sleep.
    /// </summary>
    private static async Task SeedNotesAsync(IPage page, string anchorId, int count, string notePrefix)
    {
        for (var i = 0; i < count; i++)
        {
            var before = await CountEventsAsync(page, "submitted");
            await page.EvaluateAsync(
                "([id, note]) => { window.CharterAnnotate.annotate(" +
                "  { anchorId: id, kind: 'element', note: note }); return null; }",
                new[] { anchorId, notePrefix + " #" + (i + 1) });
            await WaitForEventCountAsync(page, "submitted", before + 1);
        }

        // The markers are what every assertion reads, and they are painted by a later render() pass than the
        // one that acknowledged the submit.
        await WaitForEventCountAsync(page, "markers-rendered", 1, atLeast: true);
    }

    private static async Task WaitForEventCountAsync(
        IPage page, string type, int target, bool atLeast = false, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var seen = await CountEventsAsync(page, type);
            if (atLeast ? seen >= target : seen == target)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail(
            "the SDK emitted " + await CountEventsAsync(page, type) + " '" + type + "' event(s) within " +
            timeoutMs + "ms, expected " + (atLeast ? "at least " : "") + target);
    }

    /// <summary>
    /// The real focus order, walked from a blurred document — never <c>tabIndex &gt;= 0</c>, which is true of
    /// plenty of elements Tab can never reach. Bounded, so a focus trap fails rather than hangs.
    /// </summary>
    private static async Task<string[]> TabOrderAsync(IPage page, int maxPresses)
    {
        await page.EvaluateAsync(
            "() => { if (document.activeElement && document.activeElement.blur) document.activeElement.blur();" +
            "  window.scrollTo(0, 0); return null; }");

        var stops = new List<string>();
        for (var i = 0; i < maxPresses; i++)
        {
            await page.Keyboard.PressAsync("Tab");
            stops.Add(await page.EvaluateAsync<string>(
                "() => { const a = document.activeElement;" +
                "  if (!a || a === document.body) return '(body)';" +
                "  const ui = a.getAttribute('data-charter-ui');" +
                "  const cls = String(a.className || '').trim();" +
                "  const anchor = a.getAttribute('data-anchor-id');" +
                "  return a.tagName + (ui ? '[' + ui + ']' : '') +" +
                "    (cls ? '.' + cls.split(/\\s+/).join('.') : '') + (anchor ? '#' + anchor : ''); }"));
        }

        return stops.ToArray();
    }

    /// <summary>
    /// The accessible name out of a Playwright aria snapshot — <c>- button "1 review note on the following
    /// list"</c>. Parsed rather than read from <c>aria-label</c> so what is asserted is the name the
    /// accessibility tree actually exposes.
    /// </summary>
    private static string ButtonNameFrom(string ariaSnapshot)
    {
        var match = Regex.Match(ariaSnapshot ?? string.Empty, "button\\s+\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}
