using System.Globalization;
using System.Net;
using System.Text.Json;
using Charter.Core;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #164 — the ANNOTATED layout regression gate.
///
/// <para><b>Why this file had to exist before the fix could land.</b> <c>LayoutRegressionGateTests</c> measures
/// one document at two reviewing widths and asserts that nothing clips unreachably, nothing shrinks silently
/// and nothing is painted over anything else. It is a good gate, and it has a blind spot its own doc-comment
/// names vacuity as the enemy of: <b>it never annotates anything</b>. Every measurement it takes is of a page
/// with no markers, no badges and no rails on it — so "the layout gate passes" has always meant "the layout
/// gate has never seen the chrome". #164 inserts a new element into the plan's own sibling flow, which is
/// exactly the change that gate was built to catch and exactly the change it could not see.</para>
///
/// <para>So this is the same discipline applied to a page a reviewer has actually worked on: notes already on a
/// list and on a wide table, measured at both reviewing widths with the panel open, and measured again after
/// <c>dispose()</c>. Four conditions, each a real way the rail could go wrong:</para>
/// <list type="number">
///   <item><b>Zero layout shift on rail insertion.</b> Asserted against a SECOND, unannotated page of the same
///     plan at the same viewport — not against the same page before and after, which cannot distinguish "the
///     rail took no space" from "the measurement ran before the rail arrived".</item>
///   <item><b>No badge-over-header occlusion.</b> A badge in a table's top-right corner is a badge sitting on
///     the header row, and a header the reviewer cannot read is the #68 defect wearing new clothes.</item>
///   <item><b><c>.table-scroll</c>'s <c>scrollLeft</c> and its focus survive a note arriving.</b>
///     <c>render()</c> runs on every SSE frame, so a teammate's note landing by pull re-runs
///     <c>renderMarkers</c> over a table the reviewer has scrolled and focused. A rail that WRAPPED the block
///     would reparent it and destroy both; a sibling does not, and this is what proves the difference.</item>
///   <item><b>The DOM returns to the renderer's exact output after <c>dispose()</c></b> (invariant 1), compared
///     element for element against the unannotated control page rather than against a remembered string.</item>
/// </list>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    /// <summary>
    /// The two widths <c>LayoutRegressionGateTests</c> already measures at, reused deliberately so the
    /// annotated and unannotated gates stress the same geometry. Both are below the SDK's
    /// <c>RESERVE_MIN_VIEWPORT</c>, so on both the panel OVERLAYS the content column rather than reserving a
    /// gutter — which is the product's documented design, and which is why the badge hit-tests in
    /// <see cref="Every_railed_block_shows_a_hit_testable_badge_whose_number_is_the_truth"/> run wider.
    /// </summary>
    private static readonly int[] AnnotatedGateWidths = { ReviewingWidth, NarrowWidth };

    [SkippableFact]
    public async Task An_annotated_plan_lays_out_exactly_as_the_unannotated_one_and_returns_to_it_on_dispose()
    {
        var annotatedPath = NewPlanPath("annotated-layout");
        var controlPath = NewPlanPath("annotated-control");
        await File.WriteAllTextAsync(annotatedPath, BadgeGatePlan);
        await File.WriteAllTextAsync(controlPath, BadgeGatePlan);

        var annotatedSession = ReviewSession.Create(annotatedPath);
        var controlSession = ReviewSession.Create(controlPath);
        using var annotatedServer = ReviewServer.Start(
            annotatedSession, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        using var controlServer = ReviewServer.Start(
            controlSession, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        try
        {
            var launched = await TryLaunchAsync(showScrollbars: true);
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;

            var annotated = await NewInstrumentedPageAsync(launched);
            var control = await NewInstrumentedPageAsync(launched);

            await OpenAnnotatedGateAsync(annotated.Page, annotatedServer, annotatedSession);
            await OpenAnnotatedGateAsync(control.Page, controlServer, controlSession);

            // The control must stay clean for the whole test, or every comparison below is against a moving
            // target. Asserted, not assumed: the two pages talk to two different servers, and a session mix-up
            // would otherwise show up as a mysterious layout difference.
            Assert.Equal(0, await control.Page.Locator(Ui("badge")).CountAsync());
            Assert.Equal(0, await control.Page.Locator(Ui("badge-rail")).CountAsync());

            // ---- notes on the two blocks that gain a rail and collect the most feedback -----------
            var listAnchor = await AnchorIdAsync(annotated.Page, "body > ul");
            var tableAnchor = await AnchorIdAsync(annotated.Page, "body > div.table-scroll > table");
            Assert.False(string.IsNullOrEmpty(listAnchor) || string.IsNullOrEmpty(tableAnchor));

            await SeedNotesAsync(annotated.Page, listAnchor, 2, "a note on the list");
            await SeedNotesAsync(annotated.Page, tableAnchor, 3, "a note on the table");

            // The premise: the rails really are in the annotated page. Without this the whole comparison
            // below is "two identical pages are identical".
            Assert.Equal(2, await annotated.Page.Locator(Ui("badge-rail")).CountAsync());

            foreach (var width in AnnotatedGateWidths)
            {
                var withNotes = await MeasureLayoutAsync(annotated.Page, width);
                var withoutNotes = await MeasureLayoutAsync(control.Page, width);

                await AssertTheFixtureRenderedEveryShapeAsync(annotated.Page);
                AssertRailInsertionShiftedNothing(withNotes, withoutNotes, width);

                // The existing gate's own rules, now over a page that has been annotated — including the
                // right-edge occlusion sample this fix made necessary.
                AssertNoBlockOverlapsAnother(withNotes, width);
                AssertTheOpenPanelDoesNotSwallowItsOwnControls(withNotes, width);
                await AssertNothingIsPaintedOverAsync(annotated.Page, width);

                await AssertTheBadgeDoesNotSwallowTheTableHeaderAsync(annotated.Page, width);
            }

            // ---- a note arriving must not disturb a reviewer mid-table ---------------------------
            await AssertAScrolledFocusedTableSurvivesANoteArrivingAsync(annotated.Page, tableAnchor);

            // ---- invariant 1: dispose() gives the renderer's document back -----------------------
            await AssertDisposeRestoresTheRenderersDocumentAsync(annotated.Page, control.Page);

            AssertNoBrowserErrors(annotated);
            AssertNoBrowserErrors(control);
        }
        finally
        {
            Cleanup(annotatedPath);
            Cleanup(controlPath);
        }
    }

    /// <summary>
    /// Condition 1. The rail is a zero-height, zero-margin sibling, so an annotated plan must lay out
    /// PIXEL-IDENTICALLY to the same plan with no notes on it.
    ///
    /// <para>Measured against a separate unannotated page rather than against this page before the notes
    /// arrived, because the second reading cannot tell a rail that took no space from a measurement taken
    /// before the rail was inserted — and the whole failure mode here is content moving under a reviewer at the
    /// instant they finish annotating, which is precisely when they are looking at it.</para>
    /// </summary>
    private static void AssertRailInsertionShiftedNothing(
        LayoutSnapshot withNotes, LayoutSnapshot withoutNotes, int width)
    {
        var at = " (at a " + width + "px viewport)";

        Assert.True(
            withNotes.Blocks.Count == withoutNotes.Blocks.Count,
            "Charter #164 — annotating the plan changed how many top-level blocks it has" + at + ": " +
                withNotes.Blocks.Count + " vs " + withoutNotes.Blocks.Count + ". A rail must be SDK chrome " +
                "excluded from the content sweep, never a block of its own:\n  " +
                string.Join("\n  ", withNotes.Blocks.Select(b => b.Path)));

        for (var i = 0; i < withNotes.Blocks.Count; i++)
        {
            var a = withNotes.Blocks[i];
            var b = withoutNotes.Blocks[i];

            // Paired by ANCHOR ID, not by the rendered signature: an annotated block carries
            // `.charter-has-annotations`, so a path comparison would report the marker the SDK is supposed to
            // add as a structural difference. The id is what identifies the block in either state, and it is
            // also the thing that must not have changed (a moved id orphans every note on it).
            Assert.True(
                string.Equals(a.Id, b.Id, StringComparison.Ordinal),
                "Charter #164 — the annotated page's blocks do not line up with the unannotated one's" + at +
                    ":\n  " + a + "\n  " + b);

            // Half a pixel, the same tolerance the overlap rule uses for sub-pixel rounding. Anything larger
            // is real movement: the reviewer's document jumped when their note landed.
            Assert.True(
                Math.Abs(a.Left - b.Left) <= 0.5 && Math.Abs(a.Top - b.Top) <= 0.5
                    && Math.Abs(a.Width - b.Width) <= 0.5 && Math.Abs(a.Height - b.Height) <= 0.5,
                "Charter #164 — inserting the badge rail moved the plan" + at + ". The rail must have zero " +
                    "height and zero margin so it collapses through between its neighbours; a reviewer whose " +
                    "document jumps when their own note lands has just lost the block they were reading:\n" +
                    "  annotated:   " + a + "\n  unannotated: " + b);
        }
    }

    /// <summary>
    /// Condition 2. A badge pinned to a table's top-right corner is a badge sitting ON the header row, so the
    /// header must survive it.
    ///
    /// <para>Two measured rules rather than one, because each catches what the other cannot: the leading header
    /// cell must still answer for its own centre (a badge that grew, or that landed on the wrong side, fails
    /// this), and the badge may cover only a small fraction of the header row's area (a badge that is correctly
    /// positioned but far too large fails this while still hit-testing clean at the left).</para>
    ///
    /// <para>The LEADING cell specifically, not the trailing one: at these widths the review panel legitimately
    /// overlays the content column's right-hand side, so a probe there would report the product's documented
    /// design as a regression. The badge's own hit test — where the panel is not in the way — lives in
    /// <see cref="Every_railed_block_shows_a_hit_testable_badge_whose_number_is_the_truth"/>.</para>
    ///
    /// <para><b>Charter #198 — the badge is re-resolved after the yield, and here that matters MORE than it
    /// does in the badge probe.</b> Every SDK <c>render()</c> begins by removing the rail, taking its badge
    /// with it, and later renders arrive on their own schedule after a note is saved. A rail badge captured
    /// before the two animation frames could therefore be measured detached — reporting a <c>0x0</c> box, an
    /// overlap area of ZERO, and this rule passing while proving nothing. That is the failure direction #198
    /// warned about: the badge probe's version of the same bug goes red in the safe direction, this one would
    /// have gone green in the wrong one.</para>
    /// </summary>
    private static async Task AssertTheBadgeDoesNotSwallowTheTableHeaderAsync(IPage page, int width)
    {
        var at = " (at a " + width + "px viewport)";

        var json = await page.EvaluateAsync<string>(
            "async () => {" +
            "  const frame = () => new Promise(r => requestAnimationFrame(() => r()));" +
            // Resolved from the LIVE document with no await inside, and called again after the yield: the
            // <table>, the region and the header cells are the renderer's own DOM and survive a re-render,
            // but the rail and its badge are SDK chrome that every render() destroys and rebuilds.
            "  const locate = () => {" +
            "    const table = document.querySelector('body > div.table-scroll > table');" +
            "    const region = document.querySelector('body > div.table-scroll');" +
            "    const head = table ? table.querySelector('thead tr') : null;" +
            "    const first = table ? table.querySelector('thead th') : null;" +
            "    const rail = region ? region.previousElementSibling : null;" +
            "    const badge = rail ? rail.querySelector('[data-charter-ui=\"badge\"]') : null;" +
            "    if (!table || !region || !head || !first || !badge) {" +
            "      return { ok: false, why: 'missing ' + (!table ? 'table' : !region ? 'scroll region'" +
            "        : !head ? 'thead row' : !first ? 'th' : 'railed badge') };" +
            "    }" +
            "    return { ok: true, table: table, region: region, head: head, first: first, badge: badge };" +
            "  };" +
            "  const before = locate();" +
            "  if (!before.ok) return JSON.stringify({ ok: false, why: before.why });" +
            "  before.region.scrollLeft = 0;" +
            "  before.table.scrollIntoView({ block: 'center' });" +
            "  await frame(); await frame();" +
            // ---- from here to the return there is no await: one synchronous read of live elements ----
            "  const live = locate();" +
            "  if (!live.ok) return JSON.stringify({ ok: false, why: live.why });" +
            "  const first = live.first, region = live.region;" +
            "  const h = live.head.getBoundingClientRect();" +
            "  const b = live.badge.getBoundingClientRect();" +
            "  const f = first.getBoundingClientRect();" +
            "  const g = region.getBoundingClientRect();" +
            // The header row is far WIDER than the region that shows it — a plan's table columns are file
            // paths and signatures — so the fraction that matters is of the header a reviewer can actually
            // SEE, not of the whole overflowing row, which would divide any real occlusion down to nothing.
            "  const visW = Math.max(0, Math.min(h.right, g.right) - Math.max(h.left, g.left));" +
            "  const visH = Math.max(0, Math.min(h.bottom, g.bottom) - Math.max(h.top, g.top));" +
            "  const overlapW = Math.max(0, Math.min(h.right, b.right) - Math.max(h.left, b.left));" +
            "  const overlapH = Math.max(0, Math.min(h.bottom, b.bottom) - Math.max(h.top, b.top));" +
            // Probed where the cell is READ from, not at its centre. The leading header cell is wider than the
            // window here, so its midpoint lands out in the clipped remainder — and, at these widths, under
            // the review panel: the first run of this rule reported the panel's own furniture as the occluder.
            "  const fx = Math.min(Math.max(f.left + 8, 1), window.innerWidth - 2);" +
            "  const fy = Math.min(Math.max(f.top + (f.height / 2), 1), window.innerHeight - 2);" +
            "  const hit = document.elementFromPoint(fx, fy);" +
            "  return JSON.stringify({ ok: true, probedAt: Math.round(fx) + ',' + Math.round(fy)," +
            "    headerArea: visW * visH, overlapArea: overlapW * overlapH, badgeArea: b.width * b.height," +
            "    firstCellHit: !!(hit && (hit === first || first.contains(hit)))," +
            "    hitTag: hit ? hit.tagName + '.' + String(hit.className || '') : '(nothing)' });" +
            "}");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(
            root.GetProperty("ok").GetBoolean(),
            "fixture drift" + at + " — " + (root.TryGetProperty("why", out var why) ? why.GetString() : "") +
                ", so the header-occlusion rule is measuring nothing");

        Assert.True(
            root.GetProperty("firstCellHit").GetBoolean(),
            "Charter #164 — the table's leading header cell is covered by " +
                root.GetProperty("hitTag").GetString() + " at (" + root.GetProperty("probedAt").GetString() +
                ")" + at + ", so the reviewer cannot read the column it names");

        var headerArea = root.GetProperty("headerArea").GetDouble();
        var overlap = root.GetProperty("overlapArea").GetDouble();
        Assert.True(headerArea > 0, "the table header has no area" + at);

        // ANTI-VACUITY (Charter #198). "The badge covers little of the header" is trivially satisfied by a
        // badge with NO BOX — which is exactly what a detached rail badge measures as, and what this rule
        // reported as a pass before the locate above was moved after the yield. A badge with area is the
        // premise of the ratio below, so it is asserted rather than assumed.
        var badgeArea = root.GetProperty("badgeArea").GetDouble();
        Assert.True(
            badgeArea > 0,
            "Charter #198 — the table's rail badge measured as a zero-area box" + at + ", so the occlusion " +
                "ratio below would be 0% for a badge nobody can see rather than for a badge that behaves");

        Assert.True(
            overlap / headerArea <= 0.2,
            "Charter #164 — the badge covers " +
                (100 * overlap / headerArea).ToString("0.#", CultureInfo.InvariantCulture) + "% of the " +
                "table's header row" + at + ". A corner marker may clip a corner; it may not eat the header.");
    }

    /// <summary>
    /// Condition 3. A teammate's note arriving must not throw the reviewer out of a table they are reading.
    ///
    /// <para><c>render()</c> runs on every <c>review-log</c> frame, and <c>renderMarkers</c> clears and rebuilds
    /// every rail on each pass. If the rail WRAPPED its block, each of those passes would reparent
    /// <c>.table-scroll</c> — resetting its <c>scrollLeft</c> to zero and dropping focus out of it — so a
    /// reviewer scrolled to the far columns of a wide table would be silently snapped back to the left every
    /// time anyone else committed a comment. The rail is a sibling precisely so that cannot happen, and this is
    /// the assertion that keeps it one.</para>
    /// </summary>
    private static async Task AssertAScrolledFocusedTableSurvivesANoteArrivingAsync(IPage page, string tableAnchor)
    {
        var scrolled = await page.EvaluateAsync<double>(
            "() => { const s = document.querySelector('body > div.table-scroll');" +
            "  s.focus(); s.scrollLeft = s.scrollWidth - s.clientWidth; return s.scrollLeft; }");

        Assert.True(
            scrolled > 50,
            "fixture drift: the table does not scroll far enough for this to measure anything (reached " +
                Round(scrolled) + "px)");
        Assert.True(
            await page.EvaluateAsync<bool>(
                "() => document.activeElement === document.querySelector('body > div.table-scroll')"),
            "the scroll region did not take focus, so the focus half of this rule is unassertable");

        // A note lands — the same code path a teammate's pulled log takes: a full render(), which clears and
        // rebuilds every rail on the page.
        await SeedNotesAsync(page, tableAnchor, 1, "a note arriving mid-read");

        var after = await page.EvaluateAsync<string>(
            "() => { const s = document.querySelector('body > div.table-scroll');" +
            "  return s.scrollLeft + '|' + (document.activeElement === s); }");
        var parts = after.Split('|');

        Assert.True(
            Math.Abs(double.Parse(parts[0], CultureInfo.InvariantCulture) - scrolled) <= 1,
            "Charter #164 — a note arriving reset the table's horizontal scroll from " + Round(scrolled) +
                "px to " + parts[0] + "px. The badge rail must be a SIBLING of the scroll region: reparenting " +
                "it on every render throws a reviewer back to column one whenever anyone comments.");
        Assert.Equal("true", parts[1].ToLowerInvariant());
    }

    /// <summary>
    /// Condition 4, and invariant 1. After <c>dispose()</c> the plan's own DOM must be indistinguishable from
    /// the renderer's output — the exported artifact carries none of this chrome, so a disposed SDK must leave
    /// nothing behind that the artifact would not have.
    ///
    /// <para>Compared element for element against the CONTROL page rather than against a string captured
    /// earlier from this one. A remembered string would be captured from a page the SDK had already touched,
    /// so it would happily agree with a disposal that left the same residue both times.</para>
    /// </summary>
    private static async Task AssertDisposeRestoresTheRenderersDocumentAsync(IPage annotated, IPage control)
    {
        await annotated.EvaluateAsync("() => window.CharterAnnotate.dispose()");

        // Nothing of the SDK's survives, and nothing it wrote onto the plan's own elements survives either.
        Assert.Equal(0, await annotated.Locator("[data-charter-ui]").CountAsync());
        Assert.Equal(0, await annotated.Locator(".charter-badge-rail").CountAsync());
        Assert.Equal(0, await annotated.Locator(".charter-has-annotations").CountAsync());
        Assert.Equal(0, await annotated.Locator("[data-charter-annotation-count]").CountAsync());

        var after = await PlanMarkupAsync(annotated);
        var pristine = await PlanMarkupAsync(control);

        Assert.True(
            after.Length == pristine.Length,
            "Charter #164 — after dispose() the annotated page has " + after.Length + " top-level blocks and " +
                "the unannotated one has " + pristine.Length + "; a disposed SDK must leave the renderer's " +
                "document exactly as it found it");

        for (var i = 0; i < after.Length; i++)
        {
            Assert.True(
                string.Equals(after[i], pristine[i], StringComparison.Ordinal),
                "Charter #164 — block " + (i + 1) + " did not return to the renderer's markup after " +
                    "dispose(), so the served page and the exported artifact have diverged (invariant 1).\n" +
                    "  after dispose: " + Truncate(after[i]) + "\n  renderer:      " + Truncate(pristine[i]) +
                    "\nIf the difference is a leftover empty `class=\"\"`, that is clearMarkers(): " +
                    "classList.remove() empties the attribute but does not delete it, so every block that " +
                    "ever carried a note keeps an attribute the renderer never wrote. It is one line — drop " +
                    "the attribute when the class list is left empty — and it is worth doing rather than " +
                    "tolerating, because 'indistinguishable from the exported artifact' is a claim the SDK " +
                    "makes about itself in three separate comments.");
        }
    }

    /// <summary>The plan's own top-level blocks as markup, SDK chrome excluded — the renderer's output.</summary>
    private static async Task<string[]> PlanMarkupAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(Probe(
            "return JSON.stringify(Array.prototype.filter.call(document.body.children, el =>" +
            "  !chrome(el) && el.tagName !== 'SCRIPT' && el.tagName !== 'STYLE')" +
            "  .map(el => el.outerHTML));"));
        return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
    }

    private static string Truncate(string markup)
        => markup.Length <= 240 ? markup : markup[..240] + "…";

    private static async Task OpenAnnotatedGateAsync(IPage page, ReviewServer server, ReviewSession session)
    {
        await page.SetViewportSizeAsync(ReviewingWidth, ViewportHeight);
        await page.GotoAsync(
            CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        await WaitForEventAsync(page, "ready");

        // Open on BOTH pages, so the annotated page and its control differ in exactly one thing: the notes.
        // The panel is a fixed overlay at these widths, but it also adds `charter-reserved` to <html>, and a
        // comparison where one side had it and the other did not would measure the panel rather than the rail.
        await page.ClickAsync(Ui("panel-toggle"));
        await WaitForEventAsync(page, "panel-opened");
    }
}
