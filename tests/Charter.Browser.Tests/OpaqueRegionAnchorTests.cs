using System.Net;
using System.Text.Json;
using Charter.Core;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #166 — the anchor walk, and the two regions Charter does not own the insides of.
///
/// <para><b>The rule under test.</b> An element is an anchor iff it carries an <c>id</c> /
/// <c>data-anchor</c> / <c>data-charter-anchor</c> AND no ancestor of it is an OPAQUE REGION —
/// <c>.custom-html-scroll</c> (an author's verbatim markup) or <c>pre.mermaid</c> (Mermaid's generated
/// markup). Where the test fails the walk CONTINUES OUTWARD: it does not stop on the region, and it does not
/// give up. That distinction is the whole design, and both halves of it are asserted here — that a note taken
/// inside a region lands on the enclosing block, and that it lands on a REAL one with a real
/// <c>sourceLine</c> even when the region's own wrapper carries no id (a <c>:::custom-html</c> or
/// <c>:::diagram</c> nested inside a <c>::::note</c> or a list item, which <c>RenderBody</c>'s top-level-only
/// anchor pass leaves unstamped).</para>
///
/// <para><b>The read path is the more serious half.</b> <c>anchorElement</c> resolves by
/// <c>document.getElementById</c>, so two <c>:::custom-html</c> blocks carrying the same author id — a
/// copy-paste, which is exactly what an escape hatch invites — made a note taken in the second one jump to,
/// and highlight, the FIRST. That is misattribution, not orphaning, and it is what
/// <c>AnchorAssignment</c>'s duplicate discriminator exists to make impossible.</para>
///
/// <para><b>And the residue.</b> Author markup that breaks out of both wrappers escapes the region entirely —
/// balancing it would mean parsing it — so the predicate cannot see it. That case is not silently accepted:
/// the review panel now reads the <c>anchorStatus</c> the server already computed for a PENDING note, so an
/// unmappable anchor is stated as an orphan instead of being drawn as healthy.</para>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    /// <summary>
    /// A <c>:::custom-html</c> and a <c>:::diagram</c> nested one level down, plus a <c>:::custom-html</c>
    /// inside a list item. <c>RenderBody</c>'s anchor pass iterates TOP-LEVEL nodes only, so all three inner
    /// blocks render with no id of their own — which is precisely why the walk must climb past them rather
    /// than stop on them.
    /// </summary>
    private const string NestedRegionPlan =
        "# Nested regions\n" +
        "\n" +
        "::::note\n" +
        "A callout that carries an escape hatch and a diagram.\n" +
        "\n" +
        ":::custom-html\n" +
        "<table id=\"nested-raw\"><tr><td>nested cell</td></tr></table>\n" +
        ":::\n" +
        "\n" +
        ":::diagram\n" +
        "graph TD; Alpha-->Beta;\n" +
        ":::\n" +
        "::::\n" +
        "\n" +
        "- a plain first item\n" +
        "- an item that carries an escape hatch\n" +
        "\n" +
        "  :::custom-html\n" +
        "  <p id=\"item-raw\">a paragraph the author wrote</p>\n" +
        "  :::\n";

    /// <summary>
    /// Two escape hatches carrying the SAME author id, which is what a copy-paste produces. Their bodies
    /// differ, so the two blocks have distinct content-derived Charter ids and "which one did the note land
    /// on?" has a checkable answer.
    /// </summary>
    private const string DuplicateAuthorIdPlan =
        "# Two hatches, one id\n" +
        "\n" +
        ":::custom-html\n" +
        "<table id=\"raw-t\"><tr><td>the first copy of the table</td></tr></table>\n" +
        ":::\n" +
        "\n" +
        "Prose between them, so neither block is the other's neighbour.\n" +
        "\n" +
        ":::custom-html\n" +
        "<table id=\"raw-t\"><tr><td>the second copy of the table</td></tr></table>\n" +
        ":::\n";

    /// <summary>
    /// An author forging both region classes inside their own body. The forgery can only ever ADD an ancestor
    /// match — the real region encloses the whole body — so the predicate is monotone: this makes MORE of the
    /// body unanchorable, never less, and unanchorable degrades outward to the enclosing block.
    /// </summary>
    private const string ForgedRegionPlan =
        "# Forged regions\n" +
        "\n" +
        ":::custom-html\n" +
        "<div class=\"custom-html-scroll\"><p id=\"forged-scroll\">inside a forged scroll box</p></div>\n" +
        "<pre class=\"mermaid\" id=\"forged-diagram\">inside a forged diagram</pre>\n" +
        ":::\n";

    /// <summary>
    /// The accepted residue: a body that closes both wrappers and then opens its own element, which the HTML
    /// parser hoists out to a direct child of <c>&lt;body&gt;</c>. It is a real element with a real id and no
    /// opaque ancestor, so the walk anchors to it — and <c>SourceMap</c> has never heard of it.
    /// </summary>
    private const string BreakoutPlan =
        "# A body that breaks out\n" +
        "\n" +
        ":::custom-html\n" +
        "</div></div><div id=\"escaped\">markup that escaped the region</div>\n" +
        ":::\n";

    /// <summary>
    /// The walk climbs OUT of both region kinds and lands on the enclosing block that really is an anchor —
    /// asserted on the payload, not the DOM.
    ///
    /// <para><b>The <c>:::diagram</c> half is a repair, not a re-use of precedent.</b> <c>closestAnchored</c>
    /// resolved <c>pre.mermaid</c> unconditionally, and a nested diagram's <c>&lt;pre&gt;</c> carries no id —
    /// so <c>onClick</c>'s <c>!anchorIdOf(block)</c> guard returned, and a nested diagram was UN-ANNOTATABLE,
    /// silently: no composer, no error, nothing. Under the predicate the same walk continues past the id-less
    /// <c>&lt;pre&gt;</c> and lands on the callout.</para>
    ///
    /// <para>A nested diagram stays UNRENDERED — <c>RenderBody</c> decides whether to inline the Mermaid
    /// runtime from the top-level nodes only, so the block shows its Mermaid source as text. That is a
    /// separate gap and not what this asserts; the gesture is the same either way, because the anchor walk
    /// starts from whatever the reviewer clicked.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_note_inside_a_nested_region_anchors_to_the_block_that_encloses_it()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-nested-region-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, NestedRegionPlan);

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

            var noteId = BlockDocument.Parse(NestedRegionPlan).Blocks.First(b => b.Kind == BlockKind.Note).Id;
            var itemAnchor = await page.Locator("li").Nth(1).GetAttributeAsync("data-anchor");
            Assert.False(string.IsNullOrEmpty(itemAnchor), "the list item carries no sub-anchor to climb to");

            // The premise: both inner blocks really are id-less, so neither can answer for itself.
            Assert.Equal(
                0, await page.Locator(".note > .custom-html[id], .note > pre.mermaid[id]").CountAsync());

            // (1) inside the nested escape hatch, on the author's own <table id="nested-raw">
            await AnnotateAsync(page.Locator("#nested-raw td"), "The nested table needs a caption.");

            // (2) inside the nested diagram — un-annotatable before the predicate, and silently so
            await AnnotateAsync(page.Locator(".note > pre.mermaid"), "This diagram belongs above the callout.");

            // (3) inside the escape hatch in a LIST ITEM, whose sub-anchor is the nearest real one
            await AnnotateAsync(page.Locator("#item-raw"), "This item is doing two jobs.");

            var listed = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(3, listed.GetArrayLength());
            AssertResolvedTo(listed[0], noteId, NestedRegionPlan);
            AssertResolvedTo(listed[1], noteId, NestedRegionPlan);
            AssertResolvedTo(listed[2], itemAnchor!, NestedRegionPlan);

            // And the same three on the drain, which is the payload that matters.
            var drained = await DrainAllAsync(server.Address, session.Key.Value, 3);
            AssertResolvedTo(drained[0], noteId, NestedRegionPlan);
            AssertResolvedTo(drained[1], noteId, NestedRegionPlan);
            AssertResolvedTo(drained[2], itemAnchor!, NestedRegionPlan);

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
    /// Charter #166's read half. Two escape hatches share an author id; the reviewer annotates the SECOND.
    /// The note must carry the second block's own Charter id — and the page must MARK the second block, not
    /// the first.
    ///
    /// <para><c>document.getElementById</c> answers with the first element in document order, so before the
    /// fix the note posted <c>anchorId: "raw-t"</c> (unmappable, <c>sourceLine: null</c>) and every read of it
    /// — Jump, the accent bar, the composer's own context line — resolved to the OTHER block. A reviewer
    /// commenting on one table watched a different table light up.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_duplicated_author_id_cannot_pull_a_note_onto_the_wrong_block()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-duplicate-author-id-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, DuplicateAuthorIdPlan);

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

            var hatches = BlockDocument.Parse(DuplicateAuthorIdPlan).Blocks
                .Where(b => b.Kind == BlockKind.CustomHtml).ToList();
            Assert.Equal(2, hatches.Count);
            Assert.NotEqual(hatches[0].Id, hatches[1].Id);

            // The premise: the id really is duplicated, and getElementById really does answer with the first.
            Assert.Equal(2, await page.Locator("#raw-t").CountAsync());
            Assert.Contains(
                "first copy",
                await page.EvaluateAsync<string>("() => document.getElementById('raw-t').textContent"),
                StringComparison.Ordinal);

            await AnnotateAsync(
                page.Locator(".custom-html").Nth(1).Locator("#raw-t td"), "This second table is stale.");

            var listed = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(1, listed.GetArrayLength());
            AssertResolvedTo(listed[0], hatches[1].Id, DuplicateAuthorIdPlan);

            // The READ path, which is where the misattribution was visible: exactly one block is marked, and
            // it is the one the reviewer was looking at.
            Assert.Equal(1, await page.Locator(".charter-has-annotations").CountAsync());
            var marked = await page.EvaluateAsync<string>(
                "() => { const m = document.querySelector('.charter-has-annotations');" +
                "  return m.id + '|' + m.textContent; }");
            Assert.StartsWith(hatches[1].Id + "|", marked, StringComparison.Ordinal);
            Assert.Contains("second copy", marked, StringComparison.Ordinal);

            // ---- and the note ALREADY TAKEN on the author id, which no write-path fix can reach ----
            //
            // Every note recorded before this — and every committed one in a teammate's log — still carries
            // `anchorId: "raw-t"`. The write path never sees those again; only `anchorElement` does. So the
            // read path is gated by the same predicate, and this is where that is proven: an unmappable
            // anchor must resolve to NOTHING, not to whichever element `getElementById` answered with.
            await page.EvaluateAsync(
                "() => window.postMessage({ channel: 'charter-annotate', type: 'annotate', detail: {" +
                "  kind: 'element', anchorId: 'raw-t'," +
                "  note: 'a note recorded against the author id' } }, window.location.origin);");
            await page.WaitForSelectorAsync(Ui("item") + "[data-charter-orphan='true']");

            var legacy = page.Locator(Ui("item") + "[data-charter-orphan='true']");
            Assert.True(
                await legacy.Locator(Ui("item-jump")).IsDisabledAsync(),
                "Jump is offered for an anchor Charter cannot place");

            // Selecting it must not light up a block the reviewer never commented on.
            await legacy.Locator(Ui("item-note")).ClickAsync();
            Assert.Equal(0, await page.Locator(".charter-anchor-selected").CountAsync());

            // Still exactly one marked block, still the right one: the unmappable note marks nothing.
            Assert.Equal(1, await page.Locator(".charter-has-annotations").CountAsync());
            Assert.Equal(
                1, await page.Locator("#" + hatches[1].Id + ".charter-has-annotations").CountAsync());

            // ---- and both, on the drain ----
            var drained = await DrainAllAsync(server.Address, session.Key.Value, 2);
            AssertResolvedTo(drained[0], hatches[1].Id, DuplicateAuthorIdPlan);
            Assert.Equal("raw-t", drained[1].GetProperty("anchorId").GetString());
            Assert.Equal(JsonValueKind.Null, drained[1].GetProperty("sourceLine").ValueKind);
            Assert.Equal("orphaned", drained[1].GetProperty("anchorStatus").GetString());

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
    /// Forgery cannot defeat the predicate, because the predicate is MONOTONE. An author writing
    /// <c>class="custom-html-scroll"</c> or <c>class="mermaid"</c> inside their own body only ever adds an
    /// opaque ancestor to elements that already had one, so both forged elements — and everything inside them
    /// — degrade outward to the real block. No "outermost region" computation is needed, and the failure
    /// direction is unanchorable, never mis-anchored.
    /// </summary>
    [SkippableFact]
    public async Task A_forged_region_class_cannot_manufacture_an_anchor()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-forged-region-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, ForgedRegionPlan);

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

            var blockId = BlockDocument.Parse(ForgedRegionPlan).Blocks
                .First(b => b.Kind == BlockKind.CustomHtml).Id;

            // The premise: the forged elements really are in the render, carrying ids of their own.
            Assert.Equal(1, await page.Locator("#forged-scroll").CountAsync());
            Assert.Equal(1, await page.Locator("pre.mermaid#forged-diagram").CountAsync());

            await AnnotateAsync(page.Locator("#forged-scroll"), "The forged scroll box is not an anchor.");
            await AnnotateAsync(page.Locator("#forged-diagram"), "The forged diagram is not an anchor either.");

            var listed = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(2, listed.GetArrayLength());
            AssertResolvedTo(listed[0], blockId, ForgedRegionPlan);
            AssertResolvedTo(listed[1], blockId, ForgedRegionPlan);

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
    /// Charter #166, step 1 — the panel's orphan blindness, and why the predicate alone is not enough.
    ///
    /// <para>Author markup that closes both wrappers escapes the opaque region, so the predicate cannot see
    /// it: <c>div#escaped</c> is a body-level element with an id and no opaque ancestor, and the walk anchors
    /// to it correctly. <c>SourceMap</c> has still never heard of it, so the server resolves it as an orphan —
    /// and the panel used to DISCARD that verdict, hardcoding <c>anchorStatus: null</c> on every pending
    /// record and falling back to "is the element on the page?". The element IS on the page, so the card was
    /// drawn as healthy while the agent was being handed <c>sourceLine: null</c>.</para>
    ///
    /// <para>Both routes are read here, because a panel that agrees with the server about the LINE and
    /// disagrees with it about the STATUS is the #78 divergence wearing a different field name.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_pending_note_the_server_calls_orphaned_is_shown_as_an_orphan_in_the_panel()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-breakout-orphan-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, BreakoutPlan);

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

            // The premise: the author's div really did escape both wrappers and is a body-level element.
            Assert.True(
                await page.EvaluateAsync<bool>(
                    "() => { const el = document.getElementById('escaped');" +
                    "  return !!el && el.parentElement === document.body; }"),
                "the fixture's markup did not break out of the region, so this test proves nothing");

            await AnnotateAsync(page.Locator("#escaped"), "This is not a block Charter can find.");

            // The server's verdict: an id it never assigned resolves to no line at all.
            var listed = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(1, listed.GetArrayLength());
            Assert.Equal("escaped", listed[0].GetProperty("anchorId").GetString());
            Assert.Equal(JsonValueKind.Null, listed[0].GetProperty("sourceLine").ValueKind);
            Assert.Equal("orphaned", listed[0].GetProperty("anchorStatus").GetString());

            // ...and the panel says the same thing, in the reviewer's language.
            var card = page.Locator(Ui("item")).First;
            await page.WaitForSelectorAsync(Ui("item") + "[data-charter-orphan='true']");
            Assert.Equal("orphaned", await card.GetAttributeAsync("data-charter-anchor-status"));

            // The EARNED sentence. "not in the plan" would be a plain falsehood — the block is right there on
            // the page, and the reviewer can see it — and "the plan has changed" is a claim nothing backs.
            var orphanNote = await card.Locator(Ui("item-orphan-note")).InnerTextAsync();
            Assert.DoesNotContain("The plan has changed", orphanNote, StringComparison.Ordinal);
            Assert.DoesNotContain("is not in the plan", orphanNote, StringComparison.Ordinal);
            Assert.Contains("cannot trace", orphanNote, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Alt+click <paramref name="target"/>, write <paramref name="note"/>, save, and wait for it.</summary>
    private static async Task AnnotateAsync(ILocator target, string note)
    {
        var page = target.Page;
        await target.First.ClickAsync(new LocatorClickOptions { Modifiers = new[] { KeyboardModifier.Alt } });
        await page.WaitForSelectorAsync(Ui("composer"));
        await page.FillAsync(Ui("composer-input"), note);
        await SaveComposerAsync(page);
    }

    /// <summary>
    /// The annotation resolved to <paramref name="expectedAnchor"/> and carries a real markdown line — the
    /// two facts that separate a note the agent can act on from one it can only guess at.
    /// </summary>
    private static void AssertResolvedTo(JsonElement annotation, string expectedAnchor, string plan)
    {
        var anchorId = annotation.GetProperty("anchorId").GetString();
        Assert.Equal(expectedAnchor, anchorId);

        var line = annotation.GetProperty("sourceLine");
        Assert.True(
            line.ValueKind == JsonValueKind.Number,
            "the annotation reached the agent with no sourceLine (anchorId '" + anchorId + "')");
        Assert.Equal("resolved", annotation.GetProperty("anchorStatus").GetString());

        var lines = plan.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Assert.InRange(line.GetInt32(), 1, lines.Length);
    }

    /// <summary>
    /// Drain <c>GET /api/poll?wait=0</c> once and return all <paramref name="expected"/> reported
    /// annotations, in the order the server reports them.
    /// </summary>
    private static async Task<JsonElement[]> DrainAllAsync(Uri address, string key, int expected)
    {
        using var client = new HttpClient();
        var uri = new Uri(address, "api/poll?wait=0&key=" + Uri.EscapeDataString(key));
        using var document = JsonDocument.Parse(await client.GetStringAsync(uri));

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(expected, document.RootElement.GetArrayLength());
        return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }
}
