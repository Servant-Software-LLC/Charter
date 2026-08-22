using System.Net;
using System.Text.Json;
using Charter.Core;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #165 · #167 · #168 · #169 · #170 — the review-time marker and its disclosure.
///
/// <para>Five reports from one review of #164, and one shape between them: the reviewer is shown a marker that
/// is <b>there but unreachable</b>, or an absence with <b>nothing to explain it</b>. #164 fixed the blocks that
/// could not host a badge at all; these are the ones where the badge or the bar exists and still fails to do
/// its job.</para>
///
/// <para><b>What every measurement here refuses to do.</b> Three reads are forbidden in this file, each because
/// it is green while the defect is fully present:</para>
/// <list type="bullet">
///   <item><c>ClickAsync</c> as a way to reach a badge. Playwright calls <c>scrollIntoViewIfNeeded</c> first,
///     which UNDOES the very scroll that carries a badge out of view — so #165 is driven by setting
///     <c>scrollLeft</c> from script and re-measuring the viewport rect.</item>
///   <item><c>data-charter-annotation-count</c>, and existence checks generally. The count attribute is set on
///     blocks whose marker is invisible; a <c>.charter-has-annotations</c> class is present on a table whose
///     bar has scrolled four thousand pixels off-screen. What is asserted is where the bar is PAINTED and
///     whether anything paints over it.</item>
///   <item><c>tabIndex &gt;= 0</c> for #168. Focus is walked with real <c>Tab</c> presses from a blurred
///     document, and the assertion is on <c>document.activeElement</c> after a real <c>Enter</c>.</item>
/// </list>
///
/// <para>Everything runs on whichever engine <c>CHARTER_BROWSER</c> selects; nothing here is engine-gated.</para>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    // ---- locating the accent bar ------------------------------------------------------------------------
    //
    // The bar is a box-shadow, so it has no element of its own to query and no hit-test to run: the only
    // honest measurement is WHERE IT IS PAINTED, derived from the box that paints it. This walks outward from
    // the anchored block to <body> looking for the marker, so it finds the bar wherever the SDK chose to put
    // it — the probe must not be the thing that decides the answer it is measuring.
    //
    // Two shadow shapes are understood, and the difference is the whole of #167's second half: an INSET shadow
    // paints on the element's own background layer, [left, left+dx], underneath every descendant; an OUTER one
    // is clipped to outside the border box, [left+dx, left], where nothing inside the block can reach it.
    private const string BarProbeBody =
        "const shadowOf = el => {" +
        "  const cs = getComputedStyle(el);" +
        "  const m = /(-?[\\d.]+)px\\s+(-?[\\d.]+)px\\s+(-?[\\d.]+)px\\s+(-?[\\d.]+)px(\\s+inset)?/.exec(cs.boxShadow || '');" +
        "  if (!m) return null;" +
        "  const dx = parseFloat(m[1]);" +
        "  if (!dx) return null;" +
        "  return { dx: dx, inset: !!m[5], raw: cs.boxShadow };" +
        "};" +
        "const barOf = block => {" +
        "  let node = block;" +
        "  while (node && node !== document.body) {" +
        "    if (node.classList && node.classList.contains('charter-has-annotations')) {" +
        "      const s = shadowOf(node);" +
        "      if (s) {" +
        "        const r = node.getBoundingClientRect();" +
        "        return {" +
        "          painted: true," +
        "          on: node.tagName + (node.className ? '.' + String(node.className).trim().split(/\\s+/).join('.') : '')," +
        "          inset: s.inset, shadow: s.raw," +
        "          left: s.inset ? r.left : r.left + s.dx," +
        "          right: s.inset ? r.left + s.dx : r.left," +
        "          top: r.top, bottom: r.bottom" +
        "        };" +
        "      }" +
        "    }" +
        "    node = node.parentElement;" +
        "  }" +
        "  return { painted: false, on: '', inset: false, shadow: '', left: 0, right: 0, top: 0, bottom: 0 };" +
        "};";

    private sealed record Bar(
        bool Painted, string On, bool Inset, string Shadow,
        double Left, double Right, double Top, double Bottom)
    {
        public double Width => Right - Left;

        public override string ToString()
            => Painted
                ? "bar on " + On + " x=[" + Round(Left) + "," + Round(Right) + "] y=[" + Round(Top) + "," +
                  Round(Bottom) + "] (" + Shadow + ")"
                : "(no accent bar is painted anywhere from the block outward)";
    }

    private static async Task<Bar> BarForAsync(IPage page, string blockSelector)
    {
        var json = await page.EvaluateAsync<string>(
            "(sel) => { " + BarProbeBody +
            "  const block = document.querySelector(sel);" +
            "  if (!block) return JSON.stringify({ painted: false, on: 'MISSING', inset: false, shadow: ''," +
            "    left: 0, right: 0, top: 0, bottom: 0 });" +
            "  return JSON.stringify(barOf(block)); }",
            blockSelector);

        return JsonSerializer.Deserialize<Bar>(
                   json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("the accent-bar probe returned nothing");
    }

    // ---- #165: a code block's badge -----------------------------------------------------------------------

    /// <summary>
    /// Charter #165 — the badge on a horizontally scrolled code block must not ride away with it.
    ///
    /// <para>A <c>&lt;pre&gt;</c> can legally hold a <c>&lt;button&gt;</c>, so it was badged in place; and
    /// <c>charter.css</c> makes it <c>overflow: auto</c> while <c>.charter-has-annotations</c> makes it
    /// <c>position: relative</c>, so the block is at once the badge's containing block and the scroll box it
    /// lives in. Measured on the served page before the fix: at <c>scrollLeft</c> 2543 a badge that started at
    /// x=863 finished at x=-1680 — off-screen, and answering for nothing under its own centre. Exactly the
    /// #51 shape, with no <c>pinDiagramChrome</c> compensating.</para>
    ///
    /// <para>The measurement is taken WITHOUT scrolling the badge into view, which
    /// <see cref="ProbeBadgeAsync"/> deliberately does and which would here scroll the <c>&lt;pre&gt;</c> back
    /// to the left and erase the thing being measured. Same trap as <c>ClickAsync</c>, one level down.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_code_blocks_badge_does_not_ride_away_with_its_own_horizontal_scroll()
    {
        var planPath = NewPlanPath("pre-badge-scroll");
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

            var code = RailedBlocks.Single(b => b.Tag == "PRE");
            var anchorId = await RequireAnchorIdAsync(page, code);
            await SeedNotesAsync(page, anchorId, 2, "a note on the code block");

            // Brought into view ONCE, before either measurement, and never again: elementFromPoint is
            // viewport-relative, so a badge below the fold hit-tests as a miss however correct it is — but
            // re-centring it BETWEEN the two readings is what would hide the defect, because scrolling the
            // badge into view also scrolls the <pre> back to column one.
            await page.EvaluateAsync(
                "(sel) => { document.querySelector(sel).scrollIntoView({ block: 'center' }); return null; }",
                code.Selector);

            var before = await MeasureUnscrolledBadgeAsync(page, anchorId, code.Selector);
            Assert.True(
                before.HasBadge,
                "Charter #165 — the code block carries 2 notes and shows no badge at all: " + before);
            Assert.True(before.HitsSelf, "before scrolling, the badge's own centre resolves to " + before.HitPath);

            // The fixture's premise: the block must genuinely have somewhere to scroll, or "the rect did not
            // change" is true because nothing moved.
            var scrolled = await page.EvaluateAsync<double>(
                "(sel) => { const s = document.querySelector(sel);" +
                "  s.scrollLeft = s.scrollWidth - s.clientWidth; return s.scrollLeft; }",
                code.Selector);
            Assert.True(
                scrolled > 50,
                "fixture drift: the code block does not scroll far enough for this to measure anything " +
                    "(scrollLeft reached " + Round(scrolled) + "px)");

            var after = await MeasureUnscrolledBadgeAsync(page, anchorId, code.Selector);

            Assert.True(
                after.HasBadge && Math.Abs(after.Left - before.Left) <= 0.5
                    && Math.Abs(after.Top - before.Top) <= 0.5,
                "Charter #165 — the code block's badge moved when the block scrolled, so a reviewer reading to " +
                    "the end of a long line loses the marker telling them it carries notes. A <pre> has no " +
                    "wrapper to hoist past, so the badge belongs on a rail before the block:\n  before " +
                    before + "\n  after  " + after);

            // ...and it is still what is painted under its own centre, not merely still at the same
            // coordinates with the code now drawn over it.
            Assert.True(
                after.HitsSelf,
                "Charter #165 — after scrolling, the code block's badge centre resolves to " + after.HitPath);

            // The other half of the same marker. An inset box-shadow decorates the element's OWN border box,
            // so the accent bar on a <pre> never scrolled — asserted rather than assumed, because a fix that
            // moved the badge out while letting the bar travel would leave #167's defect on a new block.
            var bar = await BarForAsync(page, code.Selector);
            var box = await ViewportRectAsync(page, code.Selector);
            Assert.True(
                bar.Painted && bar.Left >= box.Left - 4 && bar.Right <= box.Right,
                "Charter #165 — the code block's accent bar is not against the block a reviewer can see while " +
                    "it is scrolled to " + Round(scrolled) + "px: " + bar + " against block x=[" +
                    Round(box.Left) + "," + Round(box.Right) + "]");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- #167: the table's accent bar ---------------------------------------------------------------------

    /// <summary>
    /// Charter #167 — the accent bar on a wide table must stay in the reviewer's view when the table is
    /// scrolled right.
    ///
    /// <para><c>.charter-has-annotations</c> used to be applied to the <c>&lt;table&gt;</c>, which lives INSIDE
    /// <c>div.table-scroll</c>. Measured before the fix: at <c>scrollLeft</c> 4377 the table's left edge — and
    /// therefore its bar — sat at x=-4323 against a scroll box whose visible left edge was x=54, while #164's
    /// railed badge stayed pinned. Two halves of one signal in two coordinate spaces, with the half that says
    /// "this block has feedback" being the half that disappears.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_tables_accent_bar_stays_in_view_when_the_table_is_scrolled_right()
    {
        var planPath = NewPlanPath("table-bar-scroll");
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
            await SeedNotesAsync(page, await RequireAnchorIdAsync(page, table), 1, "a note on the table");

            var atRest = await BarForAsync(page, table.Selector);
            Assert.True(
                atRest.Painted && Math.Abs(atRest.Width) >= 1,
                "the annotated table paints no accent bar at all, so this rule is measuring nothing: " + atRest);

            var scrolled = await page.EvaluateAsync<double>(
                "() => { const s = document.querySelector('body > div.table-scroll');" +
                "  s.scrollLeft = s.scrollWidth - s.clientWidth; return s.scrollLeft; }");
            Assert.True(
                scrolled > 50,
                "fixture drift: the wide table does not scroll far enough for this to measure anything " +
                    "(scrollLeft reached " + Round(scrolled) + "px)");

            var bar = await BarForAsync(page, table.Selector);
            var region = await ViewportRectAsync(page, "body > div.table-scroll");

            Assert.True(
                bar.Painted,
                "Charter #167 — the annotated table paints no accent bar once it is scrolled: " + bar);
            Assert.True(
                bar.Left >= region.Left - 8 && bar.Right <= region.Right,
                "Charter #167 — the table's accent bar left the reviewer's view when the table scrolled " +
                    Round(scrolled) + "px. The badge stays pinned on its rail, so the bar has to be painted on " +
                    "a box that is not inside the horizontal scroll container either:\n  " + bar +
                    "\n  visible region x=[" + Round(region.Left) + "," + Round(region.Right) + "]");
            Assert.True(
                bar.Left >= 0,
                "Charter #167 — the table's accent bar is off the left of the viewport entirely: " + bar);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    /// <summary>
    /// Charter #167, second half — nothing inside the table may paint over its accent bar.
    ///
    /// <para>Recorded in the same report and measured during the #164 walkthrough: with
    /// <c>border-collapse: collapse</c> the inset shadow came out as <b>four disconnected segments, one per
    /// body row and none on the header</b>, which reads as decorative row striping rather than as an annotation
    /// marker. The mechanism is that an inset box-shadow paints on the element's own background layer,
    /// underneath every descendant — and a table's descendants paint there: <c>th</c> carries an opaque
    /// background across the whole header, and the collapsed cell borders chop what is left into per-row
    /// dashes.</para>
    ///
    /// <para>So the rule is stated as the mechanism rather than as a picture: <b>no descendant of the block
    /// that paints anything may overlap the bar's own column.</b> That is red against either way of hiding the
    /// bar — a header background or a row border — and it needs no pixel comparison to say so.</para>
    /// </summary>
    [SkippableFact]
    public async Task Nothing_inside_the_table_paints_over_its_accent_bar()
    {
        var planPath = NewPlanPath("table-bar-paint");
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
            await SeedNotesAsync(page, await RequireAnchorIdAsync(page, table), 1, "a note on the table");

            var bar = await BarForAsync(page, table.Selector);
            Assert.True(
                bar.Painted && Math.Abs(bar.Width) >= 1,
                "the annotated table paints no accent bar at all, so this rule is measuring nothing: " + bar);

            // The fixture's premise: the table really does have opaque, painting descendants. Without it a
            // table of nothing but bare text would satisfy this rule while proving nothing.
            var painters = await CoveringPaintersAsync(page, table.Selector, bar);

            Assert.True(
                painters.Total > 0,
                "fixture drift: nothing in the table paints a background or a border at all, so 'nothing " +
                    "covers the bar' is vacuously true");

            Assert.True(
                painters.Covering.Length == 0,
                "Charter #167 — " + painters.Covering.Length + " painted box(es) inside the table overlap the " +
                    "accent bar's own column, so the bar is drawn underneath them: the header's opaque " +
                    "background hides it outright and the collapsed row borders chop the rest into one dash " +
                    "per row, which reads as striping rather than a marker.\n  " + bar + "\n  covered by:\n    " +
                    string.Join("\n    ", painters.Covering));

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- #168: focus on opening the panel ------------------------------------------------------------------

    /// <summary>
    /// Charter #168 — opening the notes panel must put focus INSIDE it, and closing it must give focus back.
    ///
    /// <para>The mechanism is worth naming because it makes the defect inevitable rather than incidental: the
    /// floating toggle <b>hides itself</b> when the panel opens, so the control the reviewer just activated
    /// stops being focusable and the browser resets focus to <c>&lt;body&gt;</c>. Measured before the fix, on
    /// this fixture: <c>Enter</c> on the toggle left <c>document.activeElement</c> as <c>BODY</c>, and reaching
    /// the first note's Jump then cost a full re-traverse of the document (the UX walk measured ~20 keystrokes
    /// on a six-note plan). Closing had the mirror defect, for the mirror reason.</para>
    ///
    /// <para>Walked with real <c>Tab</c> and <c>Enter</c> presses from a blurred document — never
    /// <c>tabIndex &gt;= 0</c>, which is true of plenty of things Tab never reaches. The third assertion is the
    /// anti-overreach control: the panel opens itself when a note is saved, and an automatic open that stole
    /// the caret out of the document would be a worse bug than the one being fixed.</para>
    /// </summary>
    [SkippableFact]
    public async Task Opening_the_notes_panel_puts_focus_inside_it_and_closing_gives_it_back()
    {
        var planPath = NewPlanPath("panel-focus");
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
            await SeedNotesAsync(page, await AnchorIdAsync(page, "body > p:nth-of-type(1)"), 2, "a note");

            // ---- opening moves focus into what was disclosed -------------------------------------
            // The panel opened itself when the notes were saved; shut it so the reviewer's own gesture is what
            // opens it below.
            await page.Locator(Ui("panel-close")).ClickAsync();
            await TabUntilAsync(page, "panel-toggle", maxPresses: 40);
            await page.Keyboard.PressAsync("Enter");

            var landed = await FocusDescriptionAsync(page);
            Assert.True(
                landed == "DIV[panel]",
                "Charter #168 — activating the notes toggle left focus on " + landed + ". The toggle hides " +
                    "itself as the panel opens, so focus has to be moved deliberately or the browser drops it " +
                    "to <body> and a keyboard reviewer must re-traverse the whole document to reach the notes " +
                    "they just asked for.");

            // ...and it STAYS inside. Asserted as "every stop from here to the first note is in the panel"
            // rather than as a keystroke count, which would pin one fixture's furniture rather than the rule.
            var stops = new List<string>();
            var reachedJump = false;
            for (var i = 0; i < 12 && !reachedJump; i++)
            {
                await page.Keyboard.PressAsync("Tab");
                var at = await FocusDescriptionAsync(page);
                stops.Add(at);
                Assert.True(
                    await FocusIsInsideThePanelAsync(page),
                    "Charter #168 — tabbing forward from the opened panel left it at " + at + " before " +
                        "reaching the first note, so the reviewer is walking the document again:\n  " +
                        string.Join("\n  ", stops));
                reachedJump = at == "BUTTON[item-jump]";
            }

            Assert.True(
                reachedJump,
                "Charter #168 — the first note's Jump control was not reached within 12 presses of opening " +
                    "the panel:\n  " + string.Join("\n  ", stops));

            // ---- closing returns focus to the control that replaces the panel --------------------
            // The mirror of the same defect: the panel becomes display: none, so focus still inside it would
            // be dropped to <body> exactly as opening used to drop it. The toggle is what reappears in its
            // place, so that is where the reviewer is put back.
            await page.Locator(Ui("panel-close")).ClickAsync();
            var returned = await FocusDescriptionAsync(page);
            Assert.True(
                returned == "BUTTON[panel-toggle]",
                "Charter #168 — hiding the notes panel left focus on " + returned + " rather than on the " +
                    "toggle that replaced it, so a keyboard reviewer is dropped at the top of the document on " +
                    "the way out as well as on the way in.");

            // ---- and an AUTOMATIC open never takes the caret out of the document -----------------
            await page.EvaluateAsync(
                "() => { document.querySelector('body > div.table-scroll').focus(); return null; }");
            Assert.Equal("DIV[]", await FocusDescriptionAsync(page));

            await SeedNotesAsync(page, await AnchorIdAsync(page, "body > ul"), 1, "a note that reopens it");

            Assert.True(
                await page.EvaluateAsync<bool>(
                    "() => document.activeElement === document.querySelector('body > div.table-scroll')"),
                "Charter #168 — saving a note reopened the panel AND took focus with it. The panel opens " +
                    "itself when a note is saved, when a round is handed off, and once for a quarantined " +
                    "queue; none of those is the reviewer asking for it, and pulling the caret out of the " +
                    "block they are reading would be worse than the defect this fixes.");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- #169: the <hr> as a pointer target ----------------------------------------------------------------

    /// <summary>
    /// Charter #169 — a top-level <c>&lt;hr&gt;</c> must be a pointer target a reviewer can actually hit.
    ///
    /// <para>A thematic break IS a real anchor: it carries a stable id and the composer opens on it reading
    /// "the hr block". But <c>charter.css</c> had no <c>hr</c> rule, so the UA default applied —
    /// <c>height: 0; overflow: hidden</c> with 1px borders, a 2px box. Measured before the fix:
    /// <c>elementFromPoint</c> returned <c>HR</c> at the rule's centre and 1px above it and <c>BODY</c>
    /// everywhere else, giving a usable band of about 2px. So rules acquired notes only by other routes — a
    /// re-anchor after an edit, an agent-authored note, a folded <c>:::question</c> — and never by a reviewer
    /// pointing at one.</para>
    ///
    /// <para>Two assertions, because either alone is weak. The BAND is measured by sweeping the hit test, which
    /// is what makes this red against a 2px box. The real Alt+click is aimed deliberately OFF the painted
    /// hairline — 4px above it — because a click that only works if you land on the 1px line is the defect
    /// wearing a smaller number, and because clicking dead centre passes against the old box too.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_horizontal_rule_is_a_pointer_target_a_reviewer_can_actually_hit()
    {
        var planPath = NewPlanPath("hr-target");
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
            var anchorId = await RequireAnchorIdAsync(page, RailedBlocks.Single(b => b.Tag == "HR"));

            // Sweep the hit test through the rule and take the contiguous run that includes its centre. The
            // vertical band is the whole of this defect: horizontally an <hr> already spans the column.
            var band = await page.EvaluateAsync<double>(
                "() => { const hr = document.querySelector('body > hr');" +
                "  hr.scrollIntoView({ block: 'center' });" +
                "  const r = hr.getBoundingClientRect();" +
                "  const x = r.left + r.width / 2, cy = r.top + r.height / 2;" +
                "  const isHr = dy => document.elementFromPoint(x, cy + dy) === hr;" +
                "  if (!isHr(0)) return 0;" +
                "  let up = 0, down = 0;" +
                "  while (up < 40 && isHr(-(up + 0.5))) up += 0.5;" +
                "  while (down < 40 && isHr(down + 0.5)) down += 0.5;" +
                "  return up + down + 0.5; }");

            Assert.True(
                band >= 8,
                "Charter #169 — a reviewer pointing at the horizontal rule has a band of only " + Round(band) +
                    "px to hit. charter.css had no `hr` rule, so the UA default gives `height: 0; " +
                    "overflow: hidden` and a 2px box; a real click essentially never lands it, so a rule can " +
                    "only acquire notes by routes that do not involve pointing at it.");

            // ...and a real Alt+click, aimed deliberately off the painted line, opens the composer on the rule
            // and round-trips to the rule's own source line.
            var aim = await page.EvaluateAsync<string>(
                "() => { const hr = document.querySelector('body > hr');" +
                "  hr.scrollIntoView({ block: 'center' });" +
                "  const r = hr.getBoundingClientRect();" +
                "  return JSON.stringify([r.left + r.width / 2, r.top + r.height / 2 - 4]); }");
            var point = JsonSerializer.Deserialize<double[]>(aim)!;

            await page.Keyboard.DownAsync("Alt");
            await page.Mouse.ClickAsync((float)point[0], (float)point[1]);
            await page.Keyboard.UpAsync("Alt");

            await page.WaitForSelectorAsync(
                Ui("composer-input"), new PageWaitForSelectorOptions { Timeout = 5_000 });
            await page.FillAsync(Ui("composer-input"), "a note written by pointing at the rule");
            await page.ClickAsync(Ui("composer-save"));
            await WaitForEventAsync(page, "submitted");

            var note = FindByNote(
                await ListAnnotationsAsync(server.Address, session.Key.Value),
                "a note written by pointing at the rule");
            Assert.Equal(anchorId, note.GetProperty("anchorId").GetString());
            Assert.Equal("resolved", note.GetProperty("anchorStatus").GetString());
            Assert.Equal(map.LineForAnchor(anchorId)!.Value, note.GetProperty("sourceLine").GetInt32());

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- #170: a retraction that clears the last marker ----------------------------------------------------

    /// <summary>
    /// Charter #170 — retracting a block's last live note must say why its marker went away.
    ///
    /// <para><c>renderMarkers</c> skips retracted annotations, so withdrawing the last open note on a block
    /// removes both the accent bar and the count while the panel keeps the card. That behaviour is CORRECT —
    /// a withdrawn comment must not keep marking a block — and it was entirely undisclosed, which is the same
    /// "absent reads as broken" shape #164 was filed for. Charter has one vocabulary for <i>annotated</i> and
    /// one for <i>how many</i>, and none for "annotated, but not shown here", so the absence is unsignalled by
    /// construction unless something says it.</para>
    ///
    /// <para>Disclosed the way <c>renderItem</c>'s orphan handling already does it — the neutral fact, in the
    /// card — rather than by inventing a third on-page marker vocabulary. Both directions are asserted: the
    /// sentence appears when the marker really has gone, and it stays away while another live note is still
    /// marking the block, which is what stops it becoming noise on every retraction.</para>
    /// </summary>
    [SkippableFact]
    public async Task Retracting_a_blocks_last_note_says_why_its_marker_went_away()
    {
        var directory = Path.Combine(Path.GetTempPath(), "charter-unmarked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var planPath = Path.Combine(directory, "plan.charter.md");
        await File.WriteAllTextAsync(planPath, BadgeGatePlan);

        var writer = new ReviewLogWriter(planPath, new ReviewAuthor("Alice Ng", "alice@example.com"));
        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(session, new ReviewServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            ReviewLog = writer,
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

            const string block = "body > p:nth-of-type(1)";
            await SeedNotesAsync(page, await AnchorIdAsync(page, block), 2, "a note on the paragraph");

            // Both notes are committed to the log, which is what makes a delete a RETRACT (the card survives)
            // rather than a removal from the pending queue (the card vanishes and there is nothing to say).
            await page.WaitForSelectorAsync(Ui("item-delete") + " >> nth=1");

            // ---- one retraction, one note still marking the block --------------------------------
            await page.Locator(Ui("item-delete")).First.ClickAsync();
            await page.WaitForSelectorAsync(Ui("item") + "[data-charter-status=\"retracted\"]");

            var stillMarked = await BarForAsync(page, block);
            Assert.True(
                stillMarked.Painted,
                "the block lost its marker while a live note was still on it, so the control below is " +
                    "measuring the wrong state: " + stillMarked);
            Assert.DoesNotContain(
                "no longer shows a marker",
                await PanelTextAsync(page),
                StringComparison.Ordinal);

            // ---- and now the last one ------------------------------------------------------------
            await page.Locator(Ui("item-delete")).First.ClickAsync();
            await page.WaitForSelectorAsync(
                Ui("item") + "[data-charter-status=\"retracted\"] >> nth=1");

            var unmarked = await BarForAsync(page, block);
            Assert.False(
                unmarked.Painted,
                "the block still carries an accent bar after its last note was withdrawn, so the disclosure " +
                    "below would be describing something that did not happen: " + unmarked);

            Assert.Contains(
                "no longer shows a marker",
                await PanelTextAsync(page),
                StringComparison.Ordinal);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            TryDeleteTree(directory);
        }
    }

    // ---- probes -------------------------------------------------------------------------------------------

    /// <summary>
    /// One badge measurement taken WITHOUT scrolling anything — the difference between this and
    /// <see cref="ProbeBadgeAsync"/>, which centres the badge first and would therefore undo the scroll #165
    /// exists to measure.
    /// </summary>
    private sealed record UnscrolledBadge(
        bool HasBadge, string Parent, double Left, double Top, bool HitsSelf, string HitPath)
    {
        public override string ToString()
            => HasBadge
                ? "badge in " + Parent + " at (" + Round(Left) + "," + Round(Top) + ") hitsSelf=" + HitsSelf +
                  " (" + HitPath + ")"
                : "(no badge for that anchor)";
    }

    private static async Task<UnscrolledBadge> MeasureUnscrolledBadgeAsync(
        IPage page, string anchorId, string blockSelector)
    {
        var json = await page.EvaluateAsync<string>(
            "([id, sel]) => {" +
            "  const sig = el => el.tagName + (String(el.className || '').trim()" +
            "    ? '.' + String(el.className).trim().split(/\\s+/).join('.') : '');" +
            "  let badge = document.querySelector('[data-charter-ui=\"badge\"][data-anchor-id=\"' + id + '\"]');" +
            "  if (!badge) { const b = document.querySelector(sel);" +
            "    if (b) badge = b.querySelector('[data-charter-ui=\"badge\"]'); }" +
            "  if (!badge) return JSON.stringify({ hasBadge: false, parent: '', left: 0, top: 0," +
            "    hitsSelf: false, hitPath: '' });" +
            "  const r = badge.getBoundingClientRect();" +
            "  const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);" +
            "  return JSON.stringify({ hasBadge: true, parent: sig(badge.parentElement)," +
            "    left: r.left, top: r.top," +
            "    hitsSelf: !!(hit && (hit === badge || badge.contains(hit)))," +
            "    hitPath: hit ? sig(hit) : '(nothing hittable)' }); }",
            new[] { anchorId, blockSelector });

        return JsonSerializer.Deserialize<UnscrolledBadge>(
                   json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("the badge probe returned nothing");
    }

    private sealed record ViewportRect(double Left, double Top, double Right, double Bottom);

    private static async Task<ViewportRect> ViewportRectAsync(IPage page, string selector)
    {
        var json = await page.EvaluateAsync<string>(
            "(sel) => { const r = document.querySelector(sel).getBoundingClientRect();" +
            "  return JSON.stringify({ left: r.left, top: r.top, right: r.right, bottom: r.bottom }); }",
            selector);

        return JsonSerializer.Deserialize<ViewportRect>(
                   json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("the rect probe returned nothing");
    }

    /// <summary>
    /// Every descendant of the block that PAINTS something — an opaque background, or a border with width and
    /// colour — split into those whose box overlaps the accent bar's column and those that do not. The second
    /// number is the anti-vacuity half: a block with nothing painted in it satisfies "nothing covers the bar"
    /// for free.
    /// </summary>
    private sealed record Painters(int Total, string[] Covering);

    private static async Task<Painters> CoveringPaintersAsync(IPage page, string blockSelector, Bar bar)
    {
        var json = await page.EvaluateAsync<string>(
            "([sel, left, right]) => {" +
            "  const opaque = c => !!c && c !== 'transparent' && !/rgba\\(\\s*0,\\s*0,\\s*0,\\s*0\\s*\\)/.test(c);" +
            "  const sig = el => el.tagName + (String(el.className || '').trim()" +
            "    ? '.' + String(el.className).trim().split(/\\s+/).join('.') : '');" +
            "  const block = document.querySelector(sel);" +
            "  const all = block.querySelectorAll('*');" +
            "  let total = 0; const covering = [];" +
            "  for (const el of all) {" +
            "    const cs = getComputedStyle(el);" +
            "    const paints = opaque(cs.backgroundColor) ||" +
            "      ((parseFloat(cs.borderLeftWidth) > 0 || parseFloat(cs.borderTopWidth) > 0) &&" +
            "        opaque(cs.borderTopColor));" +
            "    if (!paints) continue;" +
            "    total++;" +
            "    const r = el.getBoundingClientRect();" +
            "    if (r.width <= 0 || r.height <= 0) continue;" +
            "    if (r.left < right - 0.5 && r.right > left + 0.5) {" +
            "      covering.push(sig(el) + ' x=[' + Math.round(r.left) + ',' + Math.round(r.right) + ']');" +
            "    }" +
            "  }" +
            "  return JSON.stringify({ total: total, covering: covering.slice(0, 8) }); }",
            new object[] { blockSelector, bar.Left, bar.Right });

        return JsonSerializer.Deserialize<Painters>(
                   json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("the painter probe returned nothing");
    }

    /// <summary>What currently has focus, as <c>TAG[data-charter-ui]</c> — <c>BODY</c> when focus was dropped.</summary>
    private static Task<string> FocusDescriptionAsync(IPage page)
        => page.EvaluateAsync<string>(
            "() => { const a = document.activeElement;" +
            "  if (!a) return '(null)';" +
            "  if (a === document.body) return 'BODY';" +
            "  return a.tagName + '[' + (a.getAttribute('data-charter-ui') || '') + ']'; }");

    private static Task<bool> FocusIsInsideThePanelAsync(IPage page)
        => page.EvaluateAsync<bool>(
            "() => { const panel = document.querySelector('[data-charter-ui=\"panel\"]');" +
            "  const a = document.activeElement;" +
            "  return !!(panel && a && panel.contains(a)); }");

    /// <summary>Press Tab from a blurred document until the named piece of SDK chrome has focus.</summary>
    private static async Task TabUntilAsync(IPage page, string uiName, int maxPresses)
    {
        await page.EvaluateAsync(
            "() => { if (document.activeElement && document.activeElement.blur) document.activeElement.blur();" +
            "  window.scrollTo(0, 0); return null; }");

        var stops = new List<string>();
        for (var i = 0; i < maxPresses; i++)
        {
            await page.Keyboard.PressAsync("Tab");
            var at = await FocusDescriptionAsync(page);
            stops.Add(at);
            if (at.EndsWith("[" + uiName + "]", StringComparison.Ordinal))
            {
                return;
            }
        }

        Assert.Fail(
            "'" + uiName + "' was not reachable by Tab within " + maxPresses + " presses:\n  " +
            string.Join("\n  ", stops));
    }

    /// <summary>Everything the review panel currently READS AS — the reviewer's own view of it.</summary>
    private static Task<string> PanelTextAsync(IPage page)
        => page.Locator(Ui("panel-list")).InnerTextAsync();
}
