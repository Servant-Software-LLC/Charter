using System.Net;
using System.Text.Json;
using Charter.Core;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #87 — the browser half. Four block types clipped their content with no reachable affordance, and
/// none of it is visible to a C#-string test: whether a real wheel gesture MOVES the box, whether the browser
/// lays out the gutter the stylesheet declares, whether Tab reaches it at all. So it is measured here.
///
/// <para>The four: a <c>:::diff</c> (the severe one — <c>overflow: hidden</c> offered no user-driven
/// scrolling of any kind, on any platform), a fenced code block, an unknown directive's preserved body, and a
/// <c>&lt;table&gt;</c> inside <c>:::custom-html</c>. Each is driven at a realistic reviewing width with the
/// notes panel OPEN — the condition #68 was reported under — and then again from the SAVED artifact loaded
/// off <c>file://</c> with no server and no SDK, because a fix that leaned on script would work only while
/// <c>charter review</c> was running (invariant 1).</para>
///
/// <para><b>Launched with scrollbars VISIBLE.</b> Playwright passes <c>--hide-scrollbars</c> to headless
/// Chromium by default, which forces every scrollbar to zero width — a test that does not opt out measures
/// the FLAG rather than the stylesheet and passes blind. The opt-out is proven rather than asserted in prose:
/// each region's DECLARED gutter (its author-sized <c>::-webkit-scrollbar</c>) is compared against its
/// MEASURED one, which can only agree when a real bar is being laid out.</para>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    /// <summary>
    /// A ~180-character token with no UAX#14 break opportunity anywhere in it, so the browser has no choice
    /// but to overflow its container at any plausible font — which is what makes these tests mean the same
    /// thing on three operating systems. Plan content really does look like this.
    /// </summary>
    private static string NoBreak(string label)
        => label + string.Concat(Enumerable.Repeat("_no_break_opportunity_here", 6));

    /// <summary>The added diff line — annotated later, so its exact text is the sub-anchor's source.</summary>
    private static readonly string ClipDiffAddedLine = "+" + NoBreak("src/Charter.Core/CharterRenderer.cs");

    private static readonly string ClipAffordancePlan =
        "# The four blocks that clipped\n\n" +
        "Prose above them, at the content column's full width.\n\n" +
        ":::diff\n" +
        ClipDiffAddedLine + "\n" +
        "-a removed line\n" +
        " a context line\n" +
        ":::\n\n" +
        "```csharp\n" +
        "var " + NoBreak("code_line") + " = 1;\n" +
        "```\n\n" +
        ":::mystery\n" +
        NoBreak("unknown_directive_body") + "\n" +
        ":::\n\n" +
        ":::custom-html\n" +
        "<table><tr><td>" + NoBreak("custom_html_cell") + "</td></tr></table>\n" +
        ":::\n";

    /// <summary>
    /// The four regions, by the selector a reviewer's browser resolves them with, each paired with the
    /// accessible name the renderer gives it. Written as CSS selectors rather than indices because these are
    /// four DIFFERENT elements in four different block types — the point of #87 is that the affordance is now
    /// the same for all of them.
    /// </summary>
    private static readonly (string Selector, string Name)[] ClipRegions =
    {
        (".diff > .diff-scroll", "Diff"),
        ("body > pre:not(.mermaid)", "Code block"),
        (".unknown-directive > pre.unknown-directive-body", "Unknown directive body"),
        (".custom-html > .custom-html-scroll", "Custom HTML"),
    };

    /// <summary>
    /// The headline test. Every one of the four overflows at a reviewing width, SHOWS that it does (a
    /// persistent, author-sized scrollbar rather than the platform's fading overlay), and can be moved by
    /// BOTH routes a reviewer has: a real horizontal wheel and the arrow keys after tabbing to it.
    ///
    /// <para>Then the same four, in the exported offline artifact with <c>window.CharterAnnotate</c>
    /// undefined — which is what proves the fix is markup + CSS and not something the review server props
    /// up.</para>
    /// </summary>
    [SkippableFact]
    public async Task Every_clipping_block_scrolls_and_is_keyboard_reachable_beside_the_review_panel()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-clip-affordance-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, ClipAffordancePlan);

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

            await page.SetViewportSizeAsync(ReviewingWidth, ViewportHeight);
            await page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(page, "ready");

            // The panel open is the realistic reviewing condition and the one that narrows the column.
            await page.ClickAsync(Ui("panel-toggle"));
            await WaitForEventAsync(page, "panel-opened");

            // 660px is what is left of a 1000px window beside the 340px notes panel — #68's own condition.
            await page.SetViewportSizeAsync(NarrowWidth, ViewportHeight);

            foreach (var (selector, name) in ClipRegions)
            {
                await AssertRegionIsReachableAsync(page, selector, name);
            }

            // ---- and the SAVED artifact behaves identically: no server, no SDK, same gestures ----
            var exportPath = Path.ChangeExtension(planPath, ".export.html");
            await File.WriteAllTextAsync(
                exportPath, ArtifactExporter.Export(ClipAffordancePlan, Path.GetDirectoryName(planPath)!));
            try
            {
                await page.GotoAsync(
                    new Uri(exportPath).AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.Load });

                Assert.False(
                    await page.EvaluateAsync<bool>("() => typeof window.CharterAnnotate !== 'undefined'"),
                    "the exported artifact must ship WITHOUT the annotation SDK");

                foreach (var (selector, name) in ClipRegions)
                {
                    await AssertRegionIsReachableAsync(page, selector, name, offline: true);
                }
            }
            finally
            {
                File.Delete(exportPath);
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
    /// One region, driven the way a reviewer drives it. The premise is asserted first (it really does
    /// overflow, or everything below passes vacuously), then the two things #87 was about: that the overflow
    /// is VISIBLE — an author-sized gutter the browser actually laid out — and that it is REACHABLE by mouse
    /// and by keyboard.
    /// </summary>
    private static async Task AssertRegionIsReachableAsync(
        IPage page, string selector, string name, bool offline = false)
    {
        var where = (offline ? "the exported artifact's " : "") + selector;
        var probe = await ScrollProbeAsync(page, selector);

        Assert.True(
            probe.GetProperty("scrollWidth").GetDouble() > probe.GetProperty("clientWidth").GetDouble() + 1,
            where + " does not overflow, so this test proves nothing: " + probe);

        // Not `hidden`/`clip`: such a box provides no user-driven scrolling at all, which is exactly what a
        // :::diff used to be.
        Assert.Equal("auto", probe.GetProperty("overflowX").GetString());

        // The persistent-gutter proof, and the --hide-scrollbars opt-out proof, in one: charter.css DECLARES
        // 10px and the browser LAID OUT that much. Either half alone is satisfiable by a lie — an unstyled
        // container measures whatever the runner's platform draws (26px here, 0px on the macOS overlay
        // scrollbars #68 was reported against), and a declaration alone could be a dead rule.
        Assert.Equal(10, probe.GetProperty("declaredGutter").GetDouble());
        Assert.True(
            Math.Abs(probe.GetProperty("measuredGutter").GetDouble() - 10) <= 1,
            where + " declares a 10px scrollbar the browser did not lay out — either the rule is dead or " +
                "Playwright's default --hide-scrollbars is still hiding it: " + probe);

        // A keyboard-only reviewer needs a tab stop, and a tab stop that announces as nothing is its own
        // defect — so the role and the name are asserted, not just the tabindex.
        Assert.Equal(0, probe.GetProperty("tabIndex").GetInt32());
        Assert.Equal("region", probe.GetProperty("role").GetString());
        Assert.Equal(name, probe.GetProperty("ariaLabel").GetString());

        // ---- the defect itself: a real horizontal wheel over it must MOVE it ----
        Assert.True(
            await WheelOverSelectorAsync(page, selector) > 0,
            "Charter #87: mouse.wheel(500, 0) over " + where + " left scrollLeft at 0 — the clipped text is " +
                "still unreachable.");

        // ---- and so must the keyboard ----
        await SetScrollLeftAsync(page, selector, 0);
        await page.Locator(selector).First.FocusAsync();
        Assert.True(
            await page.EvaluateAsync<bool>(
                "s => document.activeElement === document.querySelector(s)", selector),
            where + " cannot take focus, so a keyboard-only reviewer has no way into it");

        for (var i = 0; i < 12; i++)
        {
            await page.Keyboard.PressAsync("ArrowRight");
        }

        Assert.True(
            await PollScrollLeftForSelectorAsync(page, selector) > 0,
            "ArrowRight on the focused " + where + " did not move it");
    }

    /// <summary>
    /// Charter #87's anchoring stake, proven end to end rather than structurally. A <c>:::diff</c> carries
    /// per-LINE sub-anchors — a reviewer annotates one line, not the hunk — so the scroll region the fix
    /// introduces has to be invisible to the SDK's <c>closestAnchored</c> walk. If it were not, the note
    /// would still post and still look fine in the panel; it would simply reach the agent pointing at the
    /// whole block, or at the wrong line.
    ///
    /// <para>So: scroll the diff first (the wrapper is doing its job), then Alt+click the added LINE, and
    /// assert what actually reached the server — the <c>anchorId</c> is that line's own content-derived
    /// sub-anchor, NOT the block's, and it resolves to that line's markdown source line.</para>
    /// </summary>
    [SkippableFact]
    public async Task Diff_line_annotation_still_posts_that_lines_own_sub_anchor_after_the_wrapper()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-diff-subanchor-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, ClipAffordancePlan);

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

            await page.SetViewportSizeAsync(NarrowWidth, ViewportHeight);
            await page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(page, "ready");

            // The block's own id and the LINE's sub-anchor, computed the way the renderer derives them.
            var blockId = BlockDocument.Parse(ClipAffordancePlan).Blocks
                .First(b => b.Kind == BlockKind.Diff).Id;
            var lineAnchor = Block.StableId(ClipDiffAddedLine);
            Assert.NotEqual(blockId, lineAnchor);

            // Scroll the region first — an annotation written after the reviewer has moved the content is the
            // realistic gesture, and it is where a wrapper's geometry could plausibly go wrong.
            Assert.True(await WheelOverSelectorAsync(page, ".diff > .diff-scroll") > 0);

            await page.Locator("#" + CssEscapeId(lineAnchor))
                .ClickAsync(new LocatorClickOptions { Modifiers = new[] { KeyboardModifier.Alt } });
            await page.WaitForSelectorAsync(Ui("composer"));
            await page.FillAsync(Ui("composer-input"), "This path is the one that was unreachable.");
            await page.ClickAsync(Ui("composer-save"));
            await WaitForEventAsync(page, "submitted");

            // ---- what actually reached the server ----
            var listed = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(1, listed.GetArrayLength());
            AssertBoundToTheDiffLine(listed[0], lineAnchor, blockId);

            // ---- and what the agent is handed on the drain, which is the payload that matters ----
            AssertBoundToTheDiffLine(await DrainOneAsync(server.Address, session.Key.Value), lineAnchor, blockId);

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
    /// The annotation is bound to the diff LINE: its own sub-anchor (never the block's id), resolved to that
    /// line's 1-based markdown source line. A null <c>sourceLine</c> would mean the agent was handed a note
    /// with no idea what it points at — the #48 failure mode, one block type over.
    /// </summary>
    private static void AssertBoundToTheDiffLine(JsonElement annotation, string lineAnchor, string blockId)
    {
        var anchorId = annotation.GetProperty("anchorId").GetString();
        Assert.Equal(lineAnchor, anchorId);
        Assert.NotEqual(blockId, anchorId);

        var line = annotation.GetProperty("sourceLine");
        Assert.True(
            line.ValueKind == JsonValueKind.Number,
            "the diff-line annotation reached the agent with no sourceLine (anchorId '" + anchorId + "')");
        Assert.Equal("resolved", annotation.GetProperty("anchorStatus").GetString());

        // …and that line really is the one the reviewer clicked.
        Assert.Equal(
            ClipDiffAddedLine,
            ClipAffordancePlan.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')[line.GetInt32() - 1]);
    }

    /// <summary>
    /// Drain <c>GET /api/poll?wait=0</c> once and return the single reported annotation — the payload the
    /// AGENT is handed, whose <c>sourceLine</c> is resolved at this moment against the plan as it is now
    /// (Charter #49), not at submit time.
    /// </summary>
    private static async Task<JsonElement> DrainOneAsync(Uri address, string key)
    {
        using var client = new HttpClient();
        var uri = new Uri(address, "api/poll?wait=0&key=" + Uri.EscapeDataString(key));
        using var document = JsonDocument.Parse(await client.GetStringAsync(uri));

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(1, document.RootElement.GetArrayLength());
        return document.RootElement[0].Clone();
    }

    // ---- probes, over an arbitrary selector ------------------------------------------------------------

    /// <summary>
    /// One region's scroll geometry and affordance, as the browser reports it. Every query is scoped to the
    /// element — nothing here scans the document — and the declared gutter is read from the element's own
    /// <c>::-webkit-scrollbar</c> pseudo-element, which is what distinguishes a bar the STYLESHEET sized
    /// (persistent) from whatever the platform would have drawn (an overlay, on macOS).
    /// </summary>
    private static async Task<JsonElement> ScrollProbeAsync(IPage page, string selector)
    {
        var json = await page.EvaluateAsync<string>(
            "s => { const el = document.querySelector(s);" +
            "  if (!el) return JSON.stringify({ missing: s });" +
            "  const cs = getComputedStyle(el);" +
            "  const bar = getComputedStyle(el, '::-webkit-scrollbar');" +
            "  const declared = parseFloat(bar ? bar.height : '');" +
            "  return JSON.stringify({" +
            "    selector: s, tag: el.tagName, id: el.id || ''," +
            "    overflowX: cs.overflowX," +
            "    scrollWidth: el.scrollWidth, clientWidth: el.clientWidth, scrollLeft: el.scrollLeft," +
            "    declaredGutter: isFinite(declared) ? declared : 0," +
            "    measuredGutter: el.offsetHeight - el.clientHeight" +
            "      - parseFloat(cs.borderTopWidth) - parseFloat(cs.borderBottomWidth)," +
            "    tabIndex: el.tabIndex," +
            "    role: el.getAttribute('role') || ''," +
            "    ariaLabel: el.getAttribute('aria-label') || ''" +
            "  }); }",
            selector);

        using var document = JsonDocument.Parse(json!);
        var probe = document.RootElement.Clone();
        Assert.False(
            probe.TryGetProperty("missing", out _), "no element matched '" + selector + "' on the page");
        return probe;
    }

    /// <summary>
    /// Reset the region to its left edge, put the REAL mouse pointer over it, and send a real horizontal
    /// wheel — then poll for the resulting <c>scrollLeft</c>. The pointer goes in the region's left quarter so
    /// it cannot land under the fixed review-notes panel and scroll that instead.
    /// </summary>
    private static async Task<double> WheelOverSelectorAsync(IPage page, string selector)
    {
        await SetScrollLeftAsync(page, selector, 0);

        var target = page.Locator(selector).First;
        await target.ScrollIntoViewIfNeededAsync();
        var box = await target.BoundingBoxAsync();
        Assert.NotNull(box);

        await page.Mouse.MoveAsync(box!.X + (box.Width / 4), box.Y + (box.Height / 2));
        await page.Mouse.WheelAsync(500, 0);

        return await PollScrollLeftForSelectorAsync(page, selector);
    }

    private static Task SetScrollLeftAsync(IPage page, string selector, int value)
        => page.EvaluateAsync(
            "a => { const el = document.querySelector(a[0]); if (el) el.scrollLeft = a[1]; return null; }",
            new object[] { selector, value });

    /// <summary>
    /// Poll <c>scrollLeft</c> until it moves off zero, or give up. Wheel and key scrolling are applied by the
    /// compositor, so neither can be read back synchronously — and it must be a bounded <c>EvaluateAsync</c>
    /// poll, never <c>WaitForFunctionAsync</c>, whose in-page <c>eval</c> the served-page CSP correctly
    /// refuses.
    /// </summary>
    private static async Task<double> PollScrollLeftForSelectorAsync(
        IPage page, string selector, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        double scrollLeft = 0;
        while (DateTime.UtcNow < deadline)
        {
            scrollLeft = await page.EvaluateAsync<double>(
                "s => { const el = document.querySelector(s); return el ? el.scrollLeft : -1; }", selector);
            if (scrollLeft > 0)
            {
                return scrollLeft;
            }

            await Task.Delay(50);
        }

        return scrollLeft;
    }

    /// <summary>
    /// A Charter stable id is <c>b</c> + hex (plus an optional <c>-</c> discriminator), so it is already a
    /// valid CSS identifier — but escaping it keeps the selector honest if the id shape ever widens.
    /// </summary>
    private static string CssEscapeId(string id) => id.Replace("\\", "\\\\", StringComparison.Ordinal);
}
