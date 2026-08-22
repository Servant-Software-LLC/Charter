using System.Globalization;
using System.Net;
using System.Text.Json;
using Charter.Core;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #5 — the narrowed layout regression gate.
///
/// <para>The premise this gate exists to disprove is "typed blocks make clipping structurally impossible".
/// #68 disproved it: a wide table clipped 96px of content behind a 0px overlay scrollbar with no tab stop —
/// a real defect, in a typed block, in a shipped release, found by a human with a screenshot while hundreds
/// of tests passed. #51 then proved the failure mode is broader than clipping: an oversized
/// <c>:::diagram</c> never overflowed at all, because Mermaid's <c>useMaxWidth</c> SHRANK it until the
/// labels were illegible and no scrollbar ever appeared to say so. The class is therefore <em>content the
/// reviewer cannot read, with nothing telling them so</em>, and it has at least two shapes.</para>
///
/// <para>So this gate renders one plan exercising every block type at the widths a plan is actually
/// reviewed at — including with the review panel open, the condition #68 was reported under — and fails on
/// three measured conditions. Everything is asserted against the COMPUTED/MEASURED DOM; nothing here reads
/// markup, and nothing here needs a human to arbitrate.</para>
///
/// <list type="number">
///   <item><b>Clipped with no reachable affordance</b> — an element that clips horizontally
///     (<c>overflow-x</c> is not <c>visible</c>) and overflows (<c>scrollWidth &gt; clientWidth</c>) while
///     offering neither a persistent scrollbar gutter nor a keyboard route in.</item>
///   <item><b>Shrunk below legibility with no affordance</b> — the #51 shape: an SVG rendered narrower than
///     its own <c>viewBox</c> with no zoom control and no tab stop on its block. Diagrams HAVE that
///     affordance today, so this passes now; its value is the day a new block type gains the defect.</item>
///   <item><b>Overlap / occlusion</b> — two blocks whose measured boxes intersect, or a block or a panel
///     control that something else is painted on top of (hit-tested, not eyeballed).</item>
/// </list>
///
/// <para><b>Explicitly out of scope</b>, and deliberately so: aesthetic judgement, a general layout linter,
/// screenshot diffing, and any baseline image. If it cannot fail deterministically in CI it does not belong
/// here. Two narrower boundaries are worth naming, because they are choices rather than oversights: the
/// clipping check is HORIZONTAL only (the page scrolls vertically, so a tall block is reachable by the
/// gesture every reviewer already uses), and content that spills out of an <c>overflow: visible</c> box is
/// not a failure — it is still painted, and the page's own scrollbar still reaches it.</para>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    // ---- the fixture --------------------------------------------------------------------------------------

    /// <summary>
    /// A ~210-character token with no UAX#14 break opportunity anywhere in it, so the browser has no choice
    /// but to overflow its container at ANY plausible font — this is what makes the gate's premise hold on
    /// three operating systems rather than only on the one it was written on. Plan content really does look
    /// like this: a fully-qualified type name, a method signature, a repository-relative path.
    /// </summary>
    private static string Unbreakable(string label)
        => label + string.Concat(Enumerable.Repeat("_no_break_opportunity_here", 8));

    /// <summary>
    /// One plan exercising EVERY member of the block catalog (<c>BlockKind</c>) plus the two diagnostic
    /// surfaces the renderer can emit — an unknown directive and an unparseable <c>:::question</c> — because
    /// a gate that sweeps only the blocks a happy plan uses is blind exactly where a plan degrades.
    ///
    /// <para>Each block that CAN overflow is given content that overflows at both tested widths, and each
    /// block that must stay unharmed (the narrow table, the small diagram) is kept comfortably inside them.
    /// Both halves matter: without the first the gate measures nothing, and without the second it cannot
    /// tell "reachable" from "never had anything to reach".</para>
    /// </summary>
    private static readonly string LayoutGatePlan =
        "# Layout regression gate\n\n" +
        "Prose paragraph, the full-width baseline every other block is measured against.\n\n" +
        "## Every block type, at a reviewing width\n\n" +
        "- A list item.\n" +
        "- A second list item, long enough to need wrapping at a narrow width but carrying no unbreakable\n" +
        "  token, so it wraps rather than overflowing.\n\n" +
        "| Source | Symbol | Guardrail |\n" +
        "| --- | --- | --- |\n" +
        "| `" + Unbreakable("table_source") + "` " +
        "| `" + Unbreakable("table_symbol") + "` " +
        "| `" + Unbreakable("table_guardrail") + "` |\n\n" +
        "A narrow table, the negative control — it must gain no affordance and no decoration at all:\n\n" +
        "| A | B |\n" +
        "| --- | --- |\n" +
        "| 1 | 2 |\n\n" +
        "```csharp\n" +
        "var " + Unbreakable("code_line") + " = 1;\n" +
        "```\n\n" +
        ":::note\n" +
        "A note callout, which wraps and must never clip.\n" +
        ":::\n\n" +
        ":::warn\n" +
        "A warning callout, which wraps and must never clip.\n" +
        ":::\n\n" +
        ":::comparison\n" +
        "- Option A — keep the loopback review server and its durability sidecar exactly as they are today.\n" +
        "- Option B — replace them, and pay for the migration out of the same budget as the feature work.\n" +
        ":::\n\n" +
        ":::diff\n" +
        "+ " + Unbreakable("diff_added") + "\n" +
        "- a removed line\n" +
        "  a context line\n" +
        ":::\n\n" +
        ":::diagram\n" +
        "graph LR\n" +
        "IngressGateway[Public ingress gateway terminating TLS] --> AuthService[Authentication and authorization service]\n" +
        "AuthService --> SessionStore[Session store backed by a Redis cluster]\n" +
        "SessionStore --> PlanRenderer[Charter plan renderer and source map builder]\n" +
        "PlanRenderer --> ReviewServer[Loopback review server and annotation API]\n" +
        "ReviewServer --> HandoffWriter[Guardrails handoff writer and flattener]\n" +
        ":::\n\n" +
        ":::diagram\n" +
        "graph TD\n" +
        "S[Small] --> T[Tiny]\n" +
        ":::\n\n" +
        ":::custom-html\n" +
        "<table><tr><td>" + Unbreakable("custom_html_cell") + "</td></tr></table>\n" +
        ":::\n\n" +
        ":::mystery\n" +
        Unbreakable("unknown_directive_body") + "\n" +
        ":::\n\n" +
        ":::question\n" +
        "{\"id\":\"q-single\",\"title\":\"Pick a colour\",\"mode\":\"single\",\"target\":\"human\"," +
        "\"options\":[\"Red\",\"Green\",\"Blue\"]}\n" +
        ":::\n\n" +
        ":::question\n" +
        "{\"id\":\"q-multi\",\"title\":\"Pick every runtime to support\",\"mode\":\"multi\",\"target\":\"human\"," +
        "\"options\":[\"Windows\",\"macOS\",\"Linux\"],\"answer\":[\"Linux\",\"FreeBSD\"]}\n" +
        ":::\n\n" +
        ":::question\n" +
        "{\"id\":\"q-free\",\"title\":\"Anything else the plan should say\",\"mode\":\"free-text\"," +
        "\"target\":\"human\"}\n" +
        ":::\n\n" +
        ":::question\n" +
        "{\"id\":\"q-number\",\"title\":\"How many days of runway\",\"mode\":\"number\",\"target\":\"human\"}\n" +
        ":::\n\n" +
        ":::question\n" +
        "{\"id\":\"q-bool\",\"title\":\"Ship it behind a flag\",\"mode\":\"bool\",\"target\":\"human\"}\n" +
        ":::\n\n" +
        ":::question\n" +
        "{ this body will not parse\n" +
        ":::\n";

    /// <summary>
    /// The window #68 was reported at. The bundled stylesheet gives <c>body</c> a <c>52rem</c> (832px)
    /// content column, so this is the width at which a reviewer with the notes panel open reads a plan
    /// through the ~634px the panel leaves them.
    /// </summary>
    private const int ReviewingWidth = 1000;

    /// <summary>
    /// A genuinely narrow window: the content column itself shrinks below the panel's own width. This is the
    /// width <c>Wide_table_scrolls_in_its_wrapper_at_a_narrow_viewport_without_breaking_anchoring</c> uses to
    /// stand in for "beside the panel", kept here so the two tests stress the same geometry.
    /// </summary>
    private const int NarrowWidth = 660;

    private const int ViewportHeight = 800;

    /// <summary>
    /// The clip sites that are known to be unreachable and are deliberately not fixed. <b>It is EMPTY, and
    /// that is the point</b> — every block type in the catalog now answers for its own overflow.
    ///
    /// <para>It was not empty when this gate first ran. Its first pass found four sites, all pre-existing on
    /// master, which became Charter #87 and were fixed there: a fenced code block and an unknown directive's
    /// preserved body (a bare <c>&lt;pre&gt;</c> with no author-sized scrollbar and no tab stop — on a
    /// platform with overlay scrollbars, the 0px gutter of #68), a <c>&lt;table&gt;</c> inside
    /// <c>:::custom-html</c> (contained, but by a box that could carry neither affordance), and — the severe
    /// one — a <c>:::diff</c>, whose <c>overflow: hidden</c> offered no user-driven scrolling at all, on any
    /// platform, over exactly the paths and signatures a diff is made of.</para>
    ///
    /// <para>The assertion below matches this set EXACTLY, in both directions, which is what stopped it
    /// becoming a licence to rot and is what drove it to empty:</para>
    /// <list type="bullet">
    ///   <item>a NEW unreachable clip site fails the gate — that is the regression this whole file is
    ///     for;</item>
    ///   <item>fixing one ALSO fails the gate, naming the entry to delete — so the list can only ever shrink
    ///     deliberately, and can never quietly outlive the defect it records.</item>
    /// </list>
    ///
    /// <para>Keep it empty. An entry added here is a defect somebody chose to ship, and it needs an issue
    /// number and a reason in the same commit.</para>
    /// </summary>
    private static readonly string[] KnownUnreachableClipSites = Array.Empty<string>();

    /// <summary>
    /// What the fixture must actually have RENDERED, as the measured DOM reports it — every catalog member,
    /// both diagnostic surfaces, and both halves of each negative control. This is the gate's anti-vacuity
    /// guard, and it is the assertion that stops the whole file quietly becoming a no-op: every rule below
    /// is of the form "no block does X", which a sweep over an empty or truncated block list satisfies
    /// perfectly. A renderer change that stopped emitting, say, <c>.diff</c> would otherwise shrink the sweep
    /// and turn the gate green in the same commit.
    /// </summary>
    private static readonly string[] RequiredBlockSignatures =
    {
        "BODY > H1",                          // heading
        "BODY > H2",
        "BODY > P",                           // prose
        "BODY > UL",                          // list
        "BODY > DIV.table-scroll",            // table — wrapped (#68); both the wide and the narrow one
        "BODY > PRE",                         // fenced code
        "BODY > DIV.note",                    // :::note
        "BODY > DIV.warn",                    // :::warn
        "BODY > DIV.comparison",              // :::comparison
        "BODY > DIV.diff",                    // :::diff
        "BODY > PRE.mermaid.charter-zoomable",// :::diagram — oversized, with the SDK's review-time affordance
        "BODY > PRE.mermaid",                 // :::diagram — fitting, which must gain nothing
        "BODY > DIV.custom-html",             // :::custom-html
        "BODY > DIV.unknown-directive",       // an unrecognised :::foo (#22)
        "BODY > FORM.question",               // :::question — open
        "BODY > FORM.question.answered",      // :::question — resolved
        "BODY > DIV.question-error",          // :::question — unparseable body
    };

    private static void AssertTheSweepCoversEveryBlockType(LayoutSnapshot layout, int width)
    {
        var rendered = layout.Blocks.Select(block => block.Path).ToArray();
        var missing = RequiredBlockSignatures.Except(rendered, StringComparer.Ordinal).ToArray();

        Assert.True(
            missing.Length == 0,
            "fixture drift (at a " + width + "px viewport) — the gate is no longer sweeping every block " +
                "type, so its \"nothing is clipped\" result is worth less than it looks. Missing:\n  " +
                string.Join("\n  ", missing) + "\nRendered:\n  " + string.Join("\n  ", rendered));

        // Exact, because a block appearing TWICE where one was expected (or a stray body child gaining an
        // anchor id) changes what every rule below is measured over.
        Assert.Equal(22, layout.Blocks.Count);
    }

    // ---- check 1 (clipping) + check 2 (shrink below legibility) -------------------------------------------

    /// <summary>
    /// Checks 1 and 2, swept over every block type at both reviewing widths with the notes panel OPEN.
    ///
    /// <para><b>Launched with scrollbars VISIBLE.</b> Playwright passes <c>--hide-scrollbars</c> to headless
    /// Chromium by default, which forces every scrollbar to zero width; a gate that does not opt out measures
    /// the FLAG rather than the stylesheet and passes blind — worse than not existing. That opt-out is not
    /// merely asserted in a comment here: the reference container's DECLARED gutter (its author-sized
    /// <c>::-webkit-scrollbar</c>) is compared against its MEASURED one, which can only agree when a real bar
    /// is being laid out.</para>
    /// </summary>
    [SkippableFact]
    public async Task Every_block_type_is_reachable_when_it_clips_and_legible_when_it_shrinks()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-layout-gate-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, LayoutGatePlan);

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        try
        {
            var launched = await TryLaunchAsync(showScrollbars: true);
            Skip.If(launched is null, "Chromium/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;

            await OpenPlanForReviewAsync(page, server, session);

            var measured = new List<LayoutSnapshot>();
            foreach (var width in new[] { ReviewingWidth, NarrowWidth })
            {
                var layout = await MeasureLayoutAsync(page, width);
                measured.Add(layout);
                AssertTheSweepCoversEveryBlockType(layout, width);
                AssertNothingClipsUnreachably(layout, width);
                AssertNothingShrinksWithoutAnAffordance(layout, width);
            }

            // The two viewports must actually BE two reviewing widths. If the content column ever stopped
            // responding to the window — a fixed width, a min-width floor — both passes above would measure
            // the same layout twice and the gate would silently halve its own coverage.
            Assert.True(
                ReferenceRegion(measured[1]).ClientWidth < ReferenceRegion(measured[0]).ClientWidth - 100,
                "the narrow viewport did not narrow the content column, so both passes measured the same " +
                    "layout:\n  " + ReferenceRegion(measured[0]) + "\n  " + ReferenceRegion(measured[1]));

            // Left until last, because it deliberately mutates the page: proving the affordance WORKS means
            // driving it, and a zoomed diagram is a different layout from the one measured above.
            await AssertTheZoomAffordanceReallyEnlargesAsync(page);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            if (File.Exists(planPath))
            {
                File.Delete(planPath);
            }
        }
    }

    /// <summary>
    /// Check 1. An element FAILS when it clips horizontally, has content wider than its client box, and
    /// offers neither affordance:
    /// <list type="bullet">
    ///   <item>no persistent scrollbar gutter — where "persistent" means the page's own stylesheet SIZED the
    ///     bar (<c>::-webkit-scrollbar</c>), which is what opts a container out of Chromium/WebKit's fading
    ///     overlay bars. Reading the author declaration rather than only the measured gutter is deliberate:
    ///     the measured gutter of an UNSTYLED container is whatever the runner's platform happens to draw
    ///     (26px on this Windows headless build, 0px on the macOS overlay scrollbars #68 was reported
    ///     against), so trusting it alone would make the gate pass or fail by operating system;</item>
    ///   <item>and no keyboard route in (<c>tabindex</c>), so a keyboard-only reviewer has no way to it.</item>
    /// </list>
    /// <c>overflow-x: hidden</c> / <c>clip</c> is unreachable unconditionally: such a box provides no
    /// user-driven scrolling at all, so a tab stop on it would move focus to content that still cannot move.
    /// </summary>
    private static void AssertNothingClipsUnreachably(LayoutSnapshot layout, int width)
    {
        var at = " (at a " + width + "px viewport)";

        // ---- the fixture's own premise, so nothing below can pass vacuously ----
        // The reference container from #68: this is what "reachable" means, and if it ever stops overflowing
        // the whole sweep would go quietly green while measuring nothing.
        var reference = ReferenceRegion(layout);
        Assert.True(
            reference.ScrollWidth > reference.ClientWidth + 100,
            "fixture drift: the wide table barely overflows, so the gate is measuring nothing" + at + ": " +
                reference);

        // The `--hide-scrollbars` proof, measured rather than asserted in prose. Both halves are needed and
        // each catches a different lie: charter.css DECLARES a 10px bar for this container (without which
        // the platform draws its fading overlay — the 0px gutter #68 was reported against), and the browser
        // LAID OUT that much (without which Playwright's default flag is still hiding scrollbars, and every
        // gutter measurement in this gate is meaningless).
        Assert.True(
            reference.DeclaredGutter == 10
                && Math.Abs(reference.MeasuredGutter - reference.DeclaredGutter) <= 1,
            "Charter #68 — the reference scroll region must both declare charter.css's 10px scrollbar and " +
                "have the browser lay out that much gutter" + at + ": " + reference);
        Assert.True(
            reference.TabIndex == 0
                && string.Equals(reference.Role, "region", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(reference.Name),
            "Charter #68 — the reference scroll region must be a named, keyboard-reachable region, or a " +
                "keyboard-only reviewer has no way to the clipped columns" + at + ": " + reference);

        // The affordance must stay ANCHOR-INVISIBLE: the scroll wrapper carries no stable id, so adding it
        // never moved a reviewer's note off the <table> it was written against (invariant 2). A layout gate
        // is exactly where a future wrapper gets added, so the rule is asserted where that would happen.
        // (The other half of the negative control — that a table which FITS never becomes a scroll region —
        // is ReferenceRegion's Assert.Single: only ONE of the fixture's two tables may be a clip site.)
        Assert.DoesNotContain(
            layout.Blocks,
            b => b.Path.Contains("table-scroll", StringComparison.Ordinal) && b.Id.Length > 0);

        // ---- the rule ----
        var unreachable = layout.ClipSites
            .Where(site => !IsReachable(site))
            .Select(site => site.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var appeared = unreachable.Except(KnownUnreachableClipSites, StringComparer.Ordinal).ToArray();
        Assert.True(
            appeared.Length == 0,
            "Charter #5 — content is clipped with no way to reach it" + at + ":\n  " +
                string.Join(
                    "\n  ",
                    layout.ClipSites.Where(s => appeared.Contains(s.Path, StringComparer.Ordinal))));

        var healed = KnownUnreachableClipSites.Except(unreachable, StringComparer.Ordinal).ToArray();
        Assert.True(
            healed.Length == 0,
            "these clip sites are no longer unreachable" + at + " — delete them from " +
                nameof(KnownUnreachableClipSites) + " so the gate keeps guarding the fix:\n  " +
                string.Join("\n  ", healed));

        // A tab stop the page ASKED for must announce as something. Native controls are exempt: they carry
        // their own role and are named by their <label>.
        foreach (var site in layout.ClipSites.Where(s => s.ExplicitTabStop && IsReachable(s)))
        {
            Assert.False(
                string.IsNullOrEmpty(site.Role) || string.IsNullOrEmpty(site.Name),
                "a scrollable region with an explicit tabindex must carry a role AND an accessible name, or " +
                    "it is a tab stop that announces as nothing" + at + ": " + site);
        }
    }

    /// <summary>
    /// Check 2 — the #51 shape. Mermaid renders with <c>useMaxWidth</c>, so an oversized diagram never
    /// overflows: it is SCALED DOWN until its labels cannot be read, and no scrollbar ever appears to signal
    /// it. The measured condition is therefore intrinsic size (an SVG's <c>viewBox</c> width) against
    /// rendered size — not <c>scrollWidth</c>, which check 1 already owns and which stays flat here.
    ///
    /// <para>A shrunk block must be keyboard-reachable, named, and carry at least one usable control. The
    /// converse is asserted too — a block that FITS must gain nothing — because otherwise "put a tabindex on
    /// everything" would satisfy the rule while helping nobody.</para>
    /// </summary>
    private static void AssertNothingShrinksWithoutAnAffordance(LayoutSnapshot layout, int width)
    {
        var at = " (at a " + width + "px viewport)";
        Assert.Equal(2, layout.Diagrams.Count);

        // The fixture's premise, in both directions: one diagram really is being shown smaller than it was
        // drawn, and the other really is not.
        Assert.True(
            IsShrunk(layout.Diagrams[Oversized]),
            "fixture drift: the oversized diagram fits" + at + ": " + layout.Diagrams[Oversized]);
        Assert.False(
            IsShrunk(layout.Diagrams[Fitting]),
            "fixture drift: the 'fitting' diagram does not fit" + at + ": " + layout.Diagrams[Fitting]);

        foreach (var diagram in layout.Diagrams)
        {
            Assert.True(diagram.HasSvg, "a :::diagram never rendered to <svg>" + at + ": " + diagram);

            if (IsShrunk(diagram))
            {
                Assert.True(
                    diagram.TabIndex >= 0 && !string.IsNullOrEmpty(diagram.Name) && diagram.Controls > 0,
                    "Charter #5 — a block is rendered smaller than its own intrinsic size with no way to " +
                        "enlarge it and no tab stop, so its content is unreadable and nothing says so" + at +
                        ": " + diagram);
                Assert.False(
                    string.IsNullOrEmpty(diagram.Role),
                    "a zoomable block's tab stop must announce as something" + at + ": " + diagram);
            }
            else
            {
                Assert.True(
                    diagram.TabIndex < 0 && diagram.Controls == 0,
                    "a block that fits must gain no chrome and no tab stop" + at + ": " + diagram);
            }
        }
    }

    /// <summary>
    /// The half of check 2 that structure alone cannot prove: that the affordance DOES something. Every
    /// visible, enabled control inside the shrunk block is driven in turn until the rendered SVG is
    /// measurably bigger than it was — which is the reviewer's actual goal (read the labels), not merely the
    /// presence of a button.
    ///
    /// <para>Deliberately generic — it looks for <c>&lt;button&gt;</c>, never for a specific
    /// <c>data-charter-ui</c> token — so it keeps meaning the same thing if the zoom bar is ever rebuilt, and
    /// so a NEW block type with a shrink affordance is covered without editing this file.</para>
    /// </summary>
    private static async Task AssertTheZoomAffordanceReallyEnlargesAsync(IPage page)
    {
        var before = await RenderedDiagramWidthAsync(page, Oversized);
        var controls = page.Locator("pre.mermaid").Nth(Oversized).Locator("button");
        var count = await controls.CountAsync();
        Assert.True(count > 0, "the shrunk diagram carries no control at all");

        for (var i = 0; i < count; i++)
        {
            if (!await controls.Nth(i).IsEnabledAsync())
            {
                continue;
            }

            await controls.Nth(i).ClickAsync();
            if (await RenderedDiagramWidthAsync(page, Oversized) > before + 8)
            {
                return;
            }
        }

        Assert.Fail(
            "Charter #5 — the shrunk diagram's " + count + " control(s) are decorative: none of them made it " +
                "any bigger than its fit width of " + before.ToString("0.#", CultureInfo.InvariantCulture) + "px");
    }

    private static Task<double> RenderedDiagramWidthAsync(IPage page, int index)
        => page.EvaluateAsync<double>(
            "i => { const svg = document.querySelectorAll('pre.mermaid')[i].querySelector('svg');" +
            "  return svg ? svg.getBoundingClientRect().width : 0; }",
            index);

    // ---- check 3 (overlap / occlusion) --------------------------------------------------------------------

    /// <summary>
    /// Check 3, at both reviewing widths with the notes panel OPEN — the realistic case, since the panel is a
    /// fixed overlay and is exactly what narrows the reviewer's world.
    ///
    /// <para>Three measured rules, each the strongest form that is true of the product as designed:</para>
    /// <list type="number">
    ///   <item><b>No two blocks intersect.</b> Every block is in normal flow, so an intersection means the
    ///     layout has broken — one block painted over another's text.</item>
    ///   <item><b>No block is occluded where a reviewer reaches it.</b> Hit-tested with
    ///     <c>elementFromPoint</c> at each block's own leading edge after scrolling it into view, so it fails
    ///     on anything painted on top — a full-width panel, a stray overlay that forgot
    ///     <c>pointer-events: none</c>, a mis-stacked banner.</item>
    ///   <item><b>Nothing swallows the panel's own controls.</b> The floating toggle is fixed to the corner
    ///     the open panel's footer occupies and outranks it in z-order, so the SDK hides it while the panel
    ///     is open; if that ever regresses, the reviewer's clicks on "Send to agent" land on the toggle
    ///     instead. Asserted as measured non-intersection PLUS a hit test on every panel control.</item>
    /// </list>
    ///
    /// <para><b>Not asserted, and reported instead:</b> the open panel overlays the content column itself
    /// (~269px of 832 at a 1000px window) because it reserves no space. That is the product's design — the
    /// panel is dismissable and the reviewer toggles it — so a "no overlap with content" rule here would be a
    /// design change asserted as a regression, which is exactly the aesthetic arbitration this gate is
    /// forbidden from making. What IS asserted about it is rule 2: every block keeps its leading edge
    /// visible and hittable, so a panel that grew to cover a block outright still fails.</para>
    /// </summary>
    [SkippableFact]
    public async Task Nothing_overlaps_or_occludes_the_plan_at_a_reviewing_width()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-layout-overlap-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, LayoutGatePlan);

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        try
        {
            var launched = await TryLaunchAsync(showScrollbars: true);
            Skip.If(launched is null, "Chromium/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;

            await OpenPlanForReviewAsync(page, server, session);

            foreach (var width in new[] { ReviewingWidth, NarrowWidth })
            {
                var layout = await MeasureLayoutAsync(page, width);
                AssertTheSweepCoversEveryBlockType(layout, width);
                AssertNoBlockOverlapsAnother(layout, width);
                AssertTheOpenPanelDoesNotSwallowItsOwnControls(layout, width);
                await AssertNothingIsPaintedOverAsync(page, width);
            }

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            if (File.Exists(planPath))
            {
                File.Delete(planPath);
            }
        }
    }

    /// <summary>
    /// Rule 1: no two top-level blocks intersect. Boxes are compared in DOCUMENT coordinates (so the page's
    /// scroll position cannot make two blocks look separate), with a half-pixel tolerance for the sub-pixel
    /// rounding that touching-but-not-overlapping margins produce.
    /// </summary>
    private static void AssertNoBlockOverlapsAnother(LayoutSnapshot layout, int width)
    {
        for (var i = 0; i < layout.Blocks.Count; i++)
        {
            for (var j = i + 1; j < layout.Blocks.Count; j++)
            {
                var a = layout.Blocks[i];
                var b = layout.Blocks[j];
                var overlapX = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                var overlapY = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);

                Assert.False(
                    overlapX > 0.5 && overlapY > 0.5,
                    "Charter #5 — two blocks are painted over each other (at a " + width + "px viewport), " +
                        "overlapping " + overlapX.ToString("0.#", CultureInfo.InvariantCulture) + "x" +
                        overlapY.ToString("0.#", CultureInfo.InvariantCulture) + "px:\n  " + a + "\n  " + b);
            }
        }
    }

    /// <summary>
    /// Rule 3: while the panel is open the floating toggle must not be sitting on top of it. The toggle
    /// outranks the panel in z-order and is fixed to the same corner as the panel's footer, so an overlap
    /// here is not cosmetic — it eats the clicks meant for "Send to agent".
    /// </summary>
    private static void AssertTheOpenPanelDoesNotSwallowItsOwnControls(LayoutSnapshot layout, int width)
    {
        var at = " (at a " + width + "px viewport)";
        var panel = layout.Panel;
        Assert.NotNull(panel);
        Assert.True(panel!.Width > 0 && panel.Height > 0, "the review panel is not open" + at + ": " + panel);

        var toggle = layout.Toggle;
        Assert.NotNull(toggle);
        var overlapX = Math.Min(panel.Right, toggle!.Right) - Math.Max(panel.Left, toggle.Left);
        var overlapY = Math.Min(panel.Bottom, toggle.Bottom) - Math.Max(panel.Top, toggle.Top);
        Assert.False(
            overlapX > 0.5 && overlapY > 0.5,
            "Charter #5 — the floating notes toggle is painted over the open review panel" + at +
                ", where it swallows the clicks meant for the panel's own footer controls:\n  " + panel +
                "\n  " + toggle);
    }

    /// <summary>
    /// Rule 2 + the hit-tested half of rule 3, run in the page because both need the live stacking context:
    /// every block is scrolled into view and probed at its leading edge, and every visible panel control is
    /// probed at its centre. A probe FAILS when the topmost element at that point is neither the element
    /// itself nor one of its descendants — i.e. something is painted over the thing a reviewer must reach.
    /// </summary>
    private static async Task AssertNothingIsPaintedOverAsync(IPage page, int width)
    {
        var json = await page.EvaluateAsync<string>(Probe(OcclusionBody));
        using var document = JsonDocument.Parse(json);
        var occluded = document.RootElement.EnumerateArray().Select(e => e.ToString()).ToArray();

        Assert.True(
            occluded.Length == 0,
            "Charter #5 — something is painted over content or controls a reviewer must reach (at a " +
                width + "px viewport):\n  " + string.Join("\n  ", occluded));

        await page.EvaluateAsync("() => { window.scrollTo(0, 0); return null; }");
    }

    // ---- the measured DOM ---------------------------------------------------------------------------------

    /// <summary>
    /// Set the viewport, wait for the layout to SETTLE (never a fixed sleep), and take one measurement pass.
    ///
    /// <para>Settling is a real condition, not a courtesy: the SDK re-scans every diagram on <c>resize</c> and
    /// can add or remove the zoom affordance as the column crosses a diagram's intrinsic width, so a snapshot
    /// taken mid-resize would assert against chrome that is about to change. It is waited for as two
    /// consecutive identical readings once <c>window.innerWidth</c> reports the requested size, with a bounded
    /// deadline — and through <c>EvaluateAsync</c>, never <c>WaitForFunctionAsync</c>, whose in-page
    /// <c>eval</c> the served page's CSP correctly refuses.</para>
    /// </summary>
    private static async Task<LayoutSnapshot> MeasureLayoutAsync(IPage page, int width)
    {
        await page.SetViewportSizeAsync(width, ViewportHeight);
        await page.EvaluateAsync("() => { window.scrollTo(0, 0); return null; }");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        LayoutSnapshot? previous = null;
        while (DateTime.UtcNow < deadline)
        {
            var current = Parse(await page.EvaluateAsync<string>(Probe(MeasureBody)));
            if (current.InnerWidth == width && previous is not null && Settled(previous, current))
            {
                return current;
            }

            previous = current;
            await Task.Delay(50);
        }

        Assert.Fail("the layout never settled at a " + width + "px viewport");
        return null!;
    }

    /// <summary>
    /// Two readings agree on everything this gate asserts about — so a resize that is still being processed
    /// (a diagram gaining or losing its zoom chrome, a reflow still in flight) is never mistaken for a
    /// finished layout.
    /// </summary>
    private static bool Settled(LayoutSnapshot a, LayoutSnapshot b)
        => a.InnerWidth == b.InnerWidth
           && a.Blocks.Count == b.Blocks.Count
           && a.ClipSites.Count == b.ClipSites.Count
           && a.Diagrams.Count == b.Diagrams.Count
           && a.Diagrams.Zip(b.Diagrams).All(pair =>
               pair.First.Controls == pair.Second.Controls
               && Math.Abs(pair.First.RenderedWidth - pair.Second.RenderedWidth) < 0.5);

    private static LayoutSnapshot Parse(string json)
        => JsonSerializer.Deserialize<LayoutSnapshot>(
               json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
           ?? throw new InvalidOperationException("the layout probe returned nothing");

    /// <summary>
    /// The shared preamble both in-page probes are built on. It fixes, in ONE place, what this gate considers
    /// a block and what it refuses to look at:
    /// <list type="bullet">
    ///   <item>an ANCHORED element is one carrying a Charter stable id (<c>Block.StableId</c>'s shape:
    ///     <c>b</c> + 20 hex, optionally a contextual discriminator). That is what separates the plan's own
    ///     blocks from Mermaid's generated <c>&lt;svg&gt;</c> / <c>g.node</c> ids and from the SDK's chrome,
    ///     which carries no ids at all;</item>
    ///   <item>SDK chrome (<c>[data-charter-ui]</c>) is excluded from the CONTENT sweeps, and not merely
    ///     because it is runtime-only: the panel's own note labels truncate with
    ///     <c>text-overflow: ellipsis</c> ON PURPOSE, which is a designed affordance and would read as a
    ///     clip-with-no-way-out to check 1. Chrome is measured explicitly where a rule is about it
    ///     (the panel, the floating toggle, a shrunk block's controls);</item>
    ///   <item>only HTML-namespace elements are walked. An <c>&lt;svg&gt;</c>'s internals have their own
    ///     overflow model and Mermaid's theme CSS rides in a <c>&lt;style&gt;</c> INSIDE the svg; the shrink
    ///     check owns SVGs, through the <c>viewBox</c>.</item>
    /// </list>
    /// Every query is scoped to an element. Nothing here scans the document text — the rendered body carries
    /// the 3.5MB vendored Mermaid blob, which contains ordinary English tokens (<c>tabindex</c> among them),
    /// so a whole-body scan would measure the third-party bytes rather than Charter.
    /// </summary>
    private static string Probe(string body) => "() => {" + ProbeScript + body + "}";

    private const string ProbeScript =
        "const ANCHOR = /^b[0-9a-f]{20}(-[0-9a-f]{16})?$/;" +
        "const HTML = 'http://www.w3.org/1999/xhtml';" +
        "const anchored = el => !!el.id && ANCHOR.test(el.id);" +
        "const sig = el => el.tagName + (String(el.className || '').trim()" +
        "  ? '.' + String(el.className).trim().split(/\\s+/).join('.') : '');" +
        "const path = el => (el.parentElement ? sig(el.parentElement) : '(root)') + ' > ' + sig(el);" +
        "const chrome = el => el.nodeType === 1 && el.hasAttribute('data-charter-ui');" +
        "const holdsAnchor = el => {" +
        "  if (anchored(el)) return true;" +
        "  const kids = el.querySelectorAll('[id]');" +
        "  for (let i = 0; i < kids.length; i++) { if (anchored(kids[i])) return true; }" +
        "  return false;" +
        "};" +
        "const topLevel = () => {" +
        "  const out = [];" +
        "  const kids = document.body.children;" +
        "  for (let i = 0; i < kids.length; i++) {" +
        "    const el = kids[i];" +
        "    if (chrome(el) || el.tagName === 'SCRIPT' || el.tagName === 'STYLE') continue;" +
        "    if (!holdsAnchor(el)) continue;" +
        "    out.push(el);" +
        "  }" +
        "  return out;" +
        "};";

    /// <summary>
    /// The measurement pass: every top-level block's box, every clip site inside it, and every rendered
    /// diagram's intrinsic-vs-rendered width. Block boxes are returned in DOCUMENT coordinates so they are
    /// independent of where the page happens to be scrolled.
    /// </summary>
    private const string MeasureBody =
        "const blocks = []; const sites = [];" +
        "const visit = el => {" +
        "  if (el.namespaceURI !== HTML || chrome(el)) return;" +
        "  const cs = getComputedStyle(el);" +
        "  const clips = ['auto', 'scroll', 'hidden', 'clip'].indexOf(cs.overflowX) >= 0;" +
        "  if (clips && el.scrollWidth - el.clientWidth > 1) {" +
        "    const bar = getComputedStyle(el, '::-webkit-scrollbar');" +
        "    const declared = parseFloat(bar ? bar.height : '');" +
        "    sites.push({" +
        "      path: path(el), id: el.id || '', overflowX: cs.overflowX," +
        "      scrollWidth: el.scrollWidth, clientWidth: el.clientWidth," +
        "      declaredGutter: isFinite(declared) ? declared : 0," +
        "      measuredGutter: el.offsetHeight - el.clientHeight" +
        "        - parseFloat(cs.borderTopWidth) - parseFloat(cs.borderBottomWidth)," +
        "      tabIndex: el.tabIndex, explicitTabStop: el.hasAttribute('tabindex')," +
        "      role: el.getAttribute('role') || ''," +
        "      name: el.getAttribute('aria-label') || el.getAttribute('aria-labelledby') || ''" +
        "    });" +
        "  }" +
        "  const kids = el.children;" +
        "  for (let i = 0; i < kids.length; i++) visit(kids[i]);" +
        "};" +
        "const box = el => {" +
        "  if (!el) return null;" +
        "  const r = el.getBoundingClientRect();" +
        "  const cs = getComputedStyle(el);" +
        "  return { path: path(el), id: el.id || ''," +
        "    left: r.left + window.scrollX, top: r.top + window.scrollY," +
        "    right: r.right + window.scrollX, bottom: r.bottom + window.scrollY," +
        "    width: r.width, height: r.height, display: cs.display };" +
        "};" +
        "topLevel().forEach(el => { blocks.push(box(el)); visit(el); });" +
        "const diagrams = [];" +
        "document.querySelectorAll('pre.mermaid').forEach((p, i) => {" +
        "  const s = p.querySelector('svg');" +
        "  const vb = s && s.viewBox && s.viewBox.baseVal ? s.viewBox.baseVal.width : 0;" +
        "  let controls = 0;" +
        "  p.querySelectorAll('button').forEach(b => {" +
        "    const br = b.getBoundingClientRect();" +
        "    if (!b.disabled && br.width > 0 && br.height > 0) controls++;" +
        "  });" +
        "  diagrams.push({ index: i, path: path(p), id: p.id || '', hasSvg: !!s," +
        "    intrinsicWidth: vb, renderedWidth: s ? s.getBoundingClientRect().width : 0," +
        "    tabIndex: p.tabIndex, explicitTabStop: p.hasAttribute('tabindex')," +
        "    role: p.getAttribute('role') || '', name: p.getAttribute('aria-label') || ''," +
        "    controls: controls });" +
        "});" +
        "return JSON.stringify({ innerWidth: window.innerWidth, blocks: blocks, clipSites: sites," +
        "  diagrams: diagrams, panel: box(document.querySelector('[data-charter-ui=\"panel\"]'))," +
        "  toggle: box(document.querySelector('[data-charter-ui=\"panel-toggle\"]')) });";

    /// <summary>
    /// The occlusion pass. Each block is scrolled into view and probed at BOTH its horizontal edges, and each
    /// visible panel control is probed at its centre. Probe points are clamped into the viewport so a block
    /// taller than the window is still tested rather than skipped.
    ///
    /// <para><b>The right-edge sample is Charter #164's doing.</b> This pass used to probe only
    /// <c>(left + 4, mid-height)</c> — the LEADING edge — which cannot see anything sitting in a block's
    /// top-RIGHT corner. That was defensible while nothing was ever painted there; the annotation badge is, so
    /// the pass defended nothing on the one side the SDK puts chrome. The right sample is CLAMPED clear of the
    /// review panel, which legitimately overlays the content column at these widths (the panel is dismissable
    /// and reserves its gutter only above 1200px — asserting against that overlay would be turning a design
    /// decision into a regression, which this gate is forbidden from doing).</para>
    ///
    /// <para>A block's OWN annotation badge is not an occluder of it. A marker is meant to sit on the thing it
    /// marks — on a zero-height <c>&lt;hr&gt;</c> it has nowhere else to be — so a hit that resolves to a badge
    /// carrying this block's anchor id is accepted. Any OTHER badge covering it is still a failure, which is
    /// the case that actually matters: chrome belonging to one block painted over another.</para>
    /// </summary>
    private const string OcclusionBody =
        "const bad = [];" +
        "const ownBadge = (el, hit) => {" +
        "  if (!hit || typeof hit.closest !== 'function') return false;" +
        "  const badge = hit.closest('[data-charter-ui=\"badge\"]');" +
        "  if (!badge) return false;" +
        "  const id = badge.getAttribute('data-anchor-id');" +
        "  if (!id) return false;" +
        "  if (el.id === id || el.getAttribute('data-anchor') === id) return true;" +
        "  const kids = el.querySelectorAll('[id],[data-anchor]');" +
        "  for (let i = 0; i < kids.length; i++) {" +
        "    if (kids[i].id === id || kids[i].getAttribute('data-anchor') === id) return true;" +
        "  }" +
        "  return false;" +
        "};" +
        "const probe = (el, x, y, what) => {" +
        "  const hit = document.elementFromPoint(x, y);" +
        "  if (hit && (hit === el || el.contains(hit) || ownBadge(el, hit))) return;" +
        "  bad.push(what + ' at (' + Math.round(x) + ',' + Math.round(y) + ') is covered by ' +" +
        "    (hit ? path(hit) : '(nothing hittable)'));" +
        "};" +
        "const openPanel = document.querySelector('[data-charter-ui=\"panel\"]');" +
        "const panelRect = openPanel ? openPanel.getBoundingClientRect() : null;" +
        "const panelLeft = (panelRect && panelRect.width > 0) ? panelRect.left : Infinity;" +
        "topLevel().forEach(el => {" +
        "  el.scrollIntoView({ block: 'center' });" +
        "  const r = el.getBoundingClientRect();" +
        "  if (r.width <= 0 || r.height <= 0) return;" +
        "  const x = Math.min(Math.max(r.left + 4, 1), window.innerWidth - 2);" +
        "  const y = Math.min(Math.max(r.top + (r.height / 2), 1), window.innerHeight - 2);" +
        "  probe(el, x, y, path(el) + ' (leading edge)');" +
        "  const rx = Math.min(r.right - 4, panelLeft - 4, window.innerWidth - 2);" +
        "  if (rx > x + 1) probe(el, rx, y, path(el) + ' (trailing edge)');" +
        "});" +
        "const panel = document.querySelector('[data-charter-ui=\"panel\"]');" +
        "if (panel) {" +
        "  panel.querySelectorAll('button').forEach(b => {" +
        "    const r = b.getBoundingClientRect();" +
        "    if (r.width <= 0 || r.height <= 0) return;" +
        "    probe(b, r.left + (r.width / 2), r.top + (r.height / 2)," +
        "      'panel control [' + (b.getAttribute('data-charter-ui') || b.textContent) + ']');" +
        "  });" +
        "}" +
        "return JSON.stringify(bad);";

    // ---- shared setup + small helpers ---------------------------------------------------------------------

    /// <summary>
    /// Serve the fixture, wait for everything this gate measures to EXIST (both diagrams rendered to SVG, the
    /// SDK ready and its diagram pass complete), and open the review panel — the condition #68 was reported
    /// under, and the one that makes the reviewer's column narrow in the first place.
    /// </summary>
    private static async Task OpenPlanForReviewAsync(IPage page, ReviewServer server, ReviewSession session)
    {
        await page.SetViewportSizeAsync(ReviewingWidth, ViewportHeight);
        await page.GotoAsync(
            CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });

        await WaitForEventAsync(page, "ready");
        await WaitForDiagramsAsync(page, 2);
        await WaitForEventAsync(page, "diagram-zoomable");

        await page.ClickAsync(Ui("panel-toggle"));
        await WaitForEventAsync(page, "panel-opened");
    }

    /// <summary>
    /// A persistent, non-overlay scrollbar: the page's own stylesheet sized it AND the browser laid out that
    /// much gutter. Requiring both is what makes this cross-platform — the declaration alone could be a dead
    /// rule, and the measurement alone is whatever the runner's platform draws (or zero, under Playwright's
    /// default <c>--hide-scrollbars</c>).
    /// </summary>
    /// <summary>
    /// The #68 scroll container — the ONE element in this plan that is known to clip and known to carry the
    /// full affordance, so it is both the gate's positive control and its yardstick for "reachable". Exactly
    /// one of the fixture's two tables overflows; <see cref="Assert.Single{T}(IEnumerable{T}, Predicate{T})"/>
    /// is doing real work here, since the narrow one becoming a scroll region would be a defect of its own.
    /// </summary>
    private static ClipSite ReferenceRegion(LayoutSnapshot layout)
        => Assert.Single(layout.ClipSites, s => s.Path.Contains("table-scroll", StringComparison.Ordinal));

    private static bool HasPersistentGutter(ClipSite site)
        => site.DeclaredGutter > 0 && Math.Abs(site.MeasuredGutter - site.DeclaredGutter) <= 1;

    private static bool IsReachable(ClipSite site)
        => site.OverflowX is not ("hidden" or "clip")
           && (HasPersistentGutter(site) || site.TabIndex >= 0);

    /// <summary>
    /// Being drawn smaller than it was laid out — the #51 condition — with the same 8px slack the SDK itself
    /// uses to decide a diagram is worth any chrome at all, so the gate and the product cannot disagree about
    /// which diagrams are in scope.
    /// </summary>
    private static bool IsShrunk(DiagramBox diagram)
        => diagram.IntrinsicWidth > diagram.RenderedWidth + 8;

    // ---- the measured shapes (rendered into every failure message) ----------------------------------------

    private sealed record LayoutSnapshot(
        double InnerWidth,
        IReadOnlyList<BlockBox> Blocks,
        IReadOnlyList<ClipSite> ClipSites,
        IReadOnlyList<DiagramBox> Diagrams,
        BlockBox? Panel,
        BlockBox? Toggle);

    private sealed record BlockBox(
        string Path, string Id, double Left, double Top, double Right, double Bottom,
        double Width, double Height, string Display)
    {
        public override string ToString()
            => Path + " [" + Id + "] " + Round(Width) + "x" + Round(Height) + " at (" + Round(Left) + "," +
               Round(Top) + ")-(" + Round(Right) + "," + Round(Bottom) + ") display:" + Display;
    }

    private sealed record ClipSite(
        string Path, string Id, string OverflowX, double ScrollWidth, double ClientWidth,
        double DeclaredGutter, double MeasuredGutter, int TabIndex, bool ExplicitTabStop,
        string Role, string Name)
    {
        public override string ToString()
            => Path + " [" + Id + "] overflow-x:" + OverflowX + " scrollWidth " + Round(ScrollWidth) +
               " > clientWidth " + Round(ClientWidth) + " gutter declared " + Round(DeclaredGutter) +
               "px/measured " + Round(MeasuredGutter) + "px, tabindex " + TabIndex +
               ", role '" + Role + "', name '" + Name + "'";
    }

    private sealed record DiagramBox(
        int Index, string Path, string Id, bool HasSvg, double IntrinsicWidth, double RenderedWidth,
        int TabIndex, bool ExplicitTabStop, string Role, string Name, int Controls)
    {
        public override string ToString()
            => Path + " [" + Id + "] intrinsic " + Round(IntrinsicWidth) + "px rendered " +
               Round(RenderedWidth) + "px, tabindex " + TabIndex + ", role '" + Role + "', name '" + Name +
               "', " + Controls + " control(s)";
    }

    private static string Round(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
