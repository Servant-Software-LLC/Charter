using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// The browser HALF of Charter's review loop (Charter #8): start a real <see cref="ReviewServer"/> on loopback,
/// load the served capability URL in headless Chromium, and assert the BROWSER reality that the C#-string
/// golden tests are blind to. It is the acceptance proof for the render-shell (#38) and Mermaid-under-CSP (#37)
/// fixes: it fails against the fragment/broken-Mermaid code and passes after the fix.
///
/// Runs where Chromium is available (CI installs it — see .github/workflows/ci.yml) and SKIPS cleanly where it
/// is not; the deterministic server-side guards in <c>Charter.Server.Tests.ServedDocumentShellTests</c> cover
/// the same symptoms cheaply on every OS.
/// </summary>
[Trait("Category", "BrowserAcceptance")]
public sealed class ReviewLoopBrowserTests
{
    // A plan exercising every surface the two bugs touch: prose (baseline for the CSS contrast assertion),
    // a note + warn callout (styled), a :::diagram (Mermaid render under CSP — the #37 subject), and a
    // :::question single-select form (the elicitation round-trip).
    private const string Plan =
        "# Browser Acceptance Plan\n\n" +
        "An ordinary prose paragraph that should read as plain body text.\n\n" +
        ":::note\n" +
        "A note callout the bundled stylesheet must visually distinguish from prose.\n" +
        ":::\n\n" +
        ":::warn\n" +
        "A warning callout, distinct again.\n" +
        ":::\n\n" +
        ":::diagram\n" +
        "graph TD\n" +
        "A[Start] --> B[Middle]\n" +
        "B --> C[End]\n" +
        ":::\n\n" +
        ":::question\n" +
        "{\"id\":\"q-color\",\"title\":\"Pick a color\",\"mode\":\"single\",\"target\":\"human\"," +
        "\"options\":[\"Red\",\"Green\",\"Blue\"]}\n" +
        ":::\n";

    [SkippableFact]
    public async Task Served_review_page_is_complete_styled_mermaid_renders_and_sdk_round_trips()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-browser-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, Plan);

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        try
        {
            IPlaywright playwright;
            IBrowser browser;
            try
            {
                playwright = await Playwright.CreateAsync();
                browser = await playwright.Chromium.LaunchAsync(
                    new BrowserTypeLaunchOptions { Headless = true });
            }
            catch (Exception ex)
            {
                // No Chromium / no Playwright driver on this host — skip cleanly (never fail). The deterministic
                // server-side guards still assert the same symptoms on this OS.
                Skip.If(true, "Chromium/Playwright unavailable on this host: " + ex.Message);
                return;
            }

            await using (browser)
            {
                var context = await browser.NewContextAsync();
                var page = await context.NewPageAsync();

                // Collect the browser's own error channels — the direct #37 guard is that both stay EMPTY.
                var consoleErrors = new List<string>();
                var pageErrors = new List<string>();
                page.Console += (_, msg) =>
                {
                    if (string.Equals(msg.Type, "error", StringComparison.Ordinal))
                    {
                        consoleErrors.Add(msg.Text);
                    }
                };
                page.PageError += (_, err) => pageErrors.Add(err);

                // Observe the SDK's postMessage events by listening BEFORE any page script runs: init() emits
                // 'ready' when the in-place-annotation UI initializes.
                await page.AddInitScriptAsync(
                    "window.__charterEvents = [];" +
                    "window.addEventListener('message', function (e) {" +
                    "  if (e && e.data && e.data.channel === 'charter-annotate') {" +
                    "    window.__charterEvents.push(e.data.type);" +
                    "  }" +
                    "});");

                var url = new UriBuilder(server.Address) { Query = "key=" + session.Key.Value }.Uri.ToString();
                // Wait on the `load` event, NOT network-idle: the SDK opens a long-lived SSE /events stream, so
                // the page is never network-idle. Every readiness check below waits on a concrete selector/event
                // instead of a timer (no arbitrary sleeps).
                await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load });

                // ---- #38: the served document is COMPLETE ----
                Assert.True(await page.EvaluateAsync<bool>("() => !!document.doctype"), "served page must have a <!doctype>");
                Assert.Equal("HTML", await page.EvaluateAsync<string>("() => document.documentElement.tagName"));
                Assert.True(await page.EvaluateAsync<bool>("() => !!document.head"), "served page must have a <head>");
                Assert.True(await page.EvaluateAsync<bool>("() => !!document.body"), "served page must have a <body>");
                Assert.True(await page.EvaluateAsync<bool>("() => !!document.querySelector('style')"),
                    "served page must carry the bundled inline <style>");

                // ---- #38: the bundled CSS is actually APPLIED (a computed style only the stylesheet produces) ----
                // A note callout must be visually distinguished from plain prose: the stylesheet gives it a
                // left accent border and its own background. Plain prose has neither.
                var noteBorder = await page.EvaluateAsync<double>(
                    "() => parseFloat(getComputedStyle(document.querySelector('.note')).borderLeftWidth) || 0");
                Assert.True(noteBorder > 0, "the .note callout must carry a stylesheet-applied left border (CSS not applied)");
                var noteBg = await page.EvaluateAsync<string>(
                    "() => getComputedStyle(document.querySelector('.note')).backgroundColor");
                var proseBg = await page.EvaluateAsync<string>(
                    "() => getComputedStyle(document.querySelector('body > p')).backgroundColor");
                Assert.NotEqual(proseBg, noteBg);

                // ---- #37: the :::diagram renders to a real <svg> (Mermaid ran), not raw source text ----
                try
                {
                    await page.WaitForSelectorAsync(".mermaid svg", new PageWaitForSelectorOptions { Timeout = 20_000 });
                }
                catch (TimeoutException)
                {
                    var bodyText = await page.EvaluateAsync<string>("() => document.body.innerText");
                    Assert.Fail(
                        "Mermaid did not render the :::diagram to <svg> (#37).\n" +
                        "console errors:\n  " + string.Join("\n  ", consoleErrors) + "\n" +
                        "page errors:\n  " + string.Join("\n  ", pageErrors) + "\n" +
                        "visible body text:\n" + bodyText);
                }

                Assert.True(await page.EvaluateAsync<bool>("() => !!document.querySelector('.mermaid svg')"),
                    "the :::diagram must render an inline <svg>");

                // No minified template-literal leak into the VISIBLE page (innerText excludes <script> bodies,
                // so the vendored library's own `${...}` source is correctly not counted — only a real leak is).
                Assert.False(await page.EvaluateAsync<bool>("() => document.body.innerText.includes('${')"),
                    "a `${...}` template literal leaked into the visible page (#37)");

                // The direct #37 guard: the browser reported NO errors while loading + rendering.
                Assert.True(consoleErrors.Count == 0, "console errors present:\n  " + string.Join("\n  ", consoleErrors));
                Assert.True(pageErrors.Count == 0, "page errors present:\n  " + string.Join("\n  ", pageErrors));

                // ---- the annotation SDK executed and its UI initialized ----
                Assert.True(await page.EvaluateAsync<bool>(
                    "() => typeof window.CharterAnnotate === 'object' && typeof window.CharterAnnotate.init === 'function'"),
                    "the annotation SDK (data-charter-sdk) must have defined window.CharterAnnotate");
                // The SDK auto-inits on DOMContentLoaded and emits 'ready'; wait on that event (no arbitrary sleep).
                await page.WaitForFunctionAsync("() => (window.__charterEvents || []).includes('ready')",
                    null, new PageWaitForFunctionOptions { Timeout = 10_000 });

                // ---- #8: a :::question is a native <form>, and answering it round-trips to the server ----
                Assert.Equal("FORM", await page.EvaluateAsync<string>(
                    "() => { const f = document.querySelector('form.question'); return f ? f.tagName : 'none'; }"));

                await page.CheckAsync("form.question input[type=radio][value=\"Red\"]");
                // Submit through the real form-submit path the SDK intercepts (postAnswer -> POST /api/{key}/answers).
                await page.EvalOnSelectorAsync("form.question", "f => f.requestSubmit()");

                var answer = await PollForAnswerAsync(server.Address, session.Key.Value);
                Assert.NotNull(answer);
                Assert.Equal("q-color", answer!.Value.GetProperty("questionId").GetString());
                var values = answer.Value.GetProperty("values").EnumerateArray().Select(v => v.GetString()).ToList();
                Assert.Contains("Red", values);
            }
        }
        finally
        {
            if (File.Exists(planPath))
            {
                File.Delete(planPath);
            }
        }
    }

    // ---- Charter #41 (no native prompt) + #42 (view / edit / delete the pending notes) -------------------

    /// <summary>
    /// The in-page annotation UI, end to end in a real browser: annotate with the styled composer (never
    /// <c>window.prompt</c> — #41), see the note in the review panel and as an on-block marker, edit it, and
    /// delete it (#42), with every step verified against the server's own pre-drain queue
    /// (<c>GET /api/annotations</c>). Also pins the anchoring self-guard: the SDK's own chrome carries no
    /// <c>id</c> and can never itself become an annotation target.
    /// </summary>
    [SkippableFact]
    public async Task Annotation_ui_composes_lists_edits_and_deletes_without_a_native_prompt()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-annotate-ui-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, Plan);

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        try
        {
            var launched = await TryLaunchAsync();
            Skip.If(launched is null, "Chromium/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;
            var consoleErrors = instrumented.ConsoleErrors;
            var pageErrors = instrumented.PageErrors;

            await page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(page, "ready");
            await WaitForEventAsync(page, "list-loaded");

            // ---- the empty state: the toggle is always there, the panel starts with nothing to show ----
            await page.ClickAsync(Ui("panel-toggle"));
            await page.WaitForSelectorAsync(Ui("panel-empty"));

            // ---- #41: Alt+click opens the STYLED composer, and window.prompt is never involved ----
            var anchorId = await page.EvaluateAsync<string>("() => document.querySelector('body > p').id");
            Assert.False(string.IsNullOrEmpty(anchorId), "the renderer must stamp a stable block id on the prose block");

            await page.ClickAsync("body > p", new PageClickOptions { Modifiers = new[] { KeyboardModifier.Alt } });
            await page.WaitForSelectorAsync(Ui("composer"));

            // The context line is HUMAN-READABLE — the block's own words, never the raw anchor id.
            var context = await page.InnerTextAsync(Ui("composer-context"));
            Assert.Contains("An ordinary prose paragraph", context, StringComparison.Ordinal);
            Assert.DoesNotContain(anchorId, context, StringComparison.Ordinal);

            // Save stays disabled until the note has content.
            Assert.True(await page.IsDisabledAsync(Ui("composer-save")), "Save must be disabled on an empty note");
            await page.FillAsync(Ui("composer-input"), "This paragraph needs a concrete example.");
            Assert.True(await page.IsEnabledAsync(Ui("composer-save")), "Save must enable once the note is non-empty");

            await page.ClickAsync(Ui("composer-save"));
            await WaitForEventAsync(page, "submitted");
            Assert.Equal(0, await page.Locator(Ui("composer")).CountAsync());

            // ---- #42: the note is listed in the panel, marks its block, and reached the server ----
            await page.WaitForSelectorAsync(Ui("item"));
            Assert.Equal(
                "This paragraph needs a concrete example.",
                (await page.InnerTextAsync(Ui("item-note"))).Trim());
            Assert.True(
                await page.EvaluateAsync<bool>(
                    "() => document.querySelector('body > p').classList.contains('charter-has-annotations')"),
                "the annotated block must carry the on-page marker class");
            await page.WaitForSelectorAsync(".charter-annotation-badge");

            var listed = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(1, listed.GetArrayLength());
            var createdId = listed[0].GetProperty("id").GetString();
            Assert.Equal("This paragraph needs a concrete example.", listed[0].GetProperty("note").GetString());
            Assert.Equal(anchorId, listed[0].GetProperty("anchorId").GetString());

            // ---- a text-range note highlights WITHOUT mutating the block (no <mark>) ----
            // Wrapping the quote would split the block's text nodes and corrupt the selection offsets every
            // later text-range annotation in that block is measured against. The highlight is instead painted
            // as pointer-transparent overlay rectangles from the Range's own client rects.
            var childNodesBefore = await page.EvaluateAsync<int>(
                "() => document.querySelector('body > p').childNodes.length");
            await page.EvaluateAsync(
                "() => {" +
                "  const p = document.querySelector('body > p');" +
                "  const range = document.createRange();" +
                "  range.setStart(p.firstChild, 0); range.setEnd(p.firstChild, 22);" +
                "  const sel = window.getSelection();" +
                "  sel.removeAllRanges(); sel.addRange(range);" +
                "  document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));" +
                "}");
            await page.WaitForSelectorAsync(Ui("composer"));
            Assert.Contains(
                "An ordinary prose para",
                await page.InnerTextAsync(Ui("composer-context")),
                StringComparison.Ordinal);
            Assert.True(
                await page.Locator(Ui("overlay-rect")).CountAsync() > 0,
                "a text-range target must be highlighted by overlay rectangles");
            Assert.Equal(0, await page.Locator("mark").CountAsync());
            Assert.Equal(
                childNodesBefore,
                await page.EvaluateAsync<int>("() => document.querySelector('body > p').childNodes.length"));
            await page.ClickAsync(Ui("composer-cancel"));
            Assert.Equal(0, await page.Locator(Ui("overlay-rect")).CountAsync());

            // ---- the self-guard: SDK chrome is never annotatable, and never carries an id ----
            // The count badge lives INSIDE the annotated block, so without the [data-charter-ui] guard at the
            // anchoring layer an Alt+click on it would resolve the block as an anchor and post a bogus note.
            Assert.True(
                await page.EvaluateAsync<bool>(
                    "() => Array.prototype.every.call(document.querySelectorAll('[data-charter-ui]')," +
                    " function (el) { return !el.hasAttribute('id'); })"),
                "no SDK-owned element may carry an id — the renderer anchors blocks BY id");

            await page.ClickAsync(Ui("panel-close"));
            var composersOpened = await CountEventsAsync(page, "composer-opened");
            await page.ClickAsync(
                ".charter-annotation-badge", new PageClickOptions { Modifiers = new[] { KeyboardModifier.Alt } });
            Assert.Equal(composersOpened, await CountEventsAsync(page, "composer-opened"));
            Assert.Equal(0, await page.Locator(Ui("composer")).CountAsync());
            await page.WaitForSelectorAsync(Ui("item"));   // the badge opened the panel instead

            // A text selection made entirely inside the panel must not anchor either.
            await page.EvaluateAsync(
                "() => {" +
                "  const note = document.querySelector('[data-charter-ui=\"item-note\"]');" +
                "  const range = document.createRange();" +
                "  range.selectNodeContents(note);" +
                "  const sel = window.getSelection();" +
                "  sel.removeAllRanges(); sel.addRange(range);" +
                "  document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));" +
                "}");
            Assert.Equal(composersOpened, await CountEventsAsync(page, "composer-opened"));
            Assert.Equal(1, (await ListAnnotationsAsync(server.Address, session.Key.Value)).GetArrayLength());

            // ---- edit: the panel entry AND the server both carry the new text ----
            await page.ClickAsync(Ui("item-edit"));
            await page.WaitForSelectorAsync(Ui("composer"));
            Assert.Equal(
                "This paragraph needs a concrete example.",
                await page.InputValueAsync(Ui("composer-input")));

            await page.FillAsync(Ui("composer-input"), "Rewritten: give a worked example here.");
            await page.ClickAsync(Ui("composer-save"));
            await WaitForEventAsync(page, "annotation-updated");

            Assert.Equal(
                "Rewritten: give a worked example here.",
                (await page.InnerTextAsync(Ui("item-note"))).Trim());
            var afterEdit = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(1, afterEdit.GetArrayLength());
            Assert.Equal(createdId, afterEdit[0].GetProperty("id").GetString());
            Assert.Equal("Rewritten: give a worked example here.", afterEdit[0].GetProperty("note").GetString());

            // ---- delete: gone from the panel, gone from the block, gone from the server ----
            await page.ClickAsync(Ui("item-delete"));
            await WaitForEventAsync(page, "annotation-deleted");
            await page.WaitForSelectorAsync(Ui("panel-empty"));
            Assert.False(
                await page.EvaluateAsync<bool>(
                    "() => document.querySelector('body > p').classList.contains('charter-has-annotations')"),
                "deleting the last note must clear the block's marker");
            Assert.Equal(0, (await ListAnnotationsAsync(server.Address, session.Key.Value)).GetArrayLength());

            // ---- an ORPHANED note (its block edited out of the plan) is still listed, never dropped ----
            await page.EvaluateAsync(
                "() => window.postMessage({ channel: 'charter-annotate', type: 'annotate', detail: {" +
                "  kind: 'element', anchorId: document.querySelector('body > p').id," +
                "  note: 'a note whose block the agent then deletes' } }, window.location.origin);");
            await page.WaitForSelectorAsync(Ui("item"));
            await page.EvaluateAsync(
                "() => { const p = document.querySelector('body > p'); p.parentNode.removeChild(p); }");
            await page.EvaluateAsync("() => window.CharterAnnotate.list()");

            var orphan = page.Locator(Ui("item")).First;
            Assert.Equal(1, await page.Locator(Ui("item")).CountAsync());
            Assert.Equal("true", await orphan.GetAttributeAsync("data-charter-orphan"));
            Assert.True(
                await orphan.Locator(Ui("item-jump")).IsDisabledAsync(),
                "Jump must be disabled for an anchor that is no longer in the document");
            Assert.Equal(
                "a note whose block the agent then deletes",
                (await orphan.Locator(Ui("item-note")).InnerTextAsync()).Trim());

            // ---- #41's headline assertion, and the browser's own error channels ----
            Assert.Equal(0, await page.EvaluateAsync<int>("() => (window.__promptCalls || []).length"));
            Assert.True(consoleErrors.Count == 0, "console errors present:\n  " + string.Join("\n  ", consoleErrors));
            Assert.True(pageErrors.Count == 0, "page errors present:\n  " + string.Join("\n  ", pageErrors));
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
    /// A live-reload arriving while the reviewer is mid-note must NOT discard what they typed. The old
    /// <c>window.prompt</c> blocked the JS thread and protected drafts by accident; the non-modal composer does
    /// not, so the SDK defers the reload and offers it as a banner instead.
    /// </summary>
    [SkippableFact]
    public async Task Annotation_draft_survives_a_live_reload_of_the_plan()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-annotate-draft-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, Plan);

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        using var stopNudger = new CancellationTokenSource();
        Task? nudger = null;
        try
        {
            var launched = await TryLaunchAsync();
            Skip.If(launched is null, "Chromium/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;
            var consoleErrors = instrumented.ConsoleErrors;
            var pageErrors = instrumented.PageErrors;

            await page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(page, "ready");

            // Start a note but do not save it.
            await page.ClickAsync("body > p", new PageClickOptions { Modifiers = new[] { KeyboardModifier.Alt } });
            await page.WaitForSelectorAsync(Ui("composer-input"));
            await page.FillAsync(Ui("composer-input"), "half-typed thought the reviewer must not lose");

            // A sentinel that only survives if the document is NEVER navigated.
            await page.EvaluateAsync("() => { window.__charterNotReloaded = true; }");

            // The agent edits the plan underneath the reviewer. Re-touch on a cadence so the test never depends
            // on a single write landing after the server's FileSystemWatcher is armed.
            nudger = Task.Run(async () =>
            {
                var edit = 0;
                while (!stopNudger.Token.IsCancellationRequested)
                {
                    try
                    {
                        await File.WriteAllTextAsync(
                            planPath, Plan + "\nEdit " + (++edit) + " by the agent.\n", stopNudger.Token);
                    }
                    catch (IOException)
                    {
                        // A transient sharing conflict with the server's per-request read is harmless — retry.
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(150), stopNudger.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });

            await WaitForEventAsync(page, "reload-deferred", 30_000);
            stopNudger.Cancel();

            // The draft is intact, the page never navigated, and the reviewer is offered the reload.
            Assert.Equal(
                "half-typed thought the reviewer must not lose",
                await page.InputValueAsync(Ui("composer-input")));
            Assert.True(
                await page.EvaluateAsync<bool>("() => window.__charterNotReloaded === true"),
                "the SDK must NOT navigate while a draft note is open");
            await page.WaitForSelectorAsync(Ui("reload-banner"));

            Assert.True(consoleErrors.Count == 0, "console errors present:\n  " + string.Join("\n  ", consoleErrors));
            Assert.True(pageErrors.Count == 0, "page errors present:\n  " + string.Join("\n  ", pageErrors));
        }
        finally
        {
            stopNudger.Cancel();
            if (nudger is not null)
            {
                try
                {
                    await nudger;
                }
                catch (Exception)
                {
                    // The nudger only writes the temp plan / awaits a cancellable delay; nothing to surface.
                }
            }

            if (File.Exists(planPath))
            {
                File.Delete(planPath);
            }
        }
    }

    // ---- shared browser plumbing -------------------------------------------------------------------------

    /// <summary>A launched Playwright + browser pair, or <see langword="null"/> where Chromium is absent.</summary>
    private sealed record Launched(IPlaywright Playwright, IBrowser Browser);

    private static async Task<Launched?> TryLaunchAsync()
    {
        try
        {
            var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            return new Launched(playwright, browser);
        }
        catch (Exception)
        {
            // No Chromium / no Playwright driver on this host — the caller skips cleanly (never fails). The
            // deterministic server-side guards still assert the same contracts on this OS.
            return null;
        }
    }

    /// <summary>A page plus the browser error channels collected for it.</summary>
    private sealed record Instrumented(IPage Page, List<string> ConsoleErrors, List<string> PageErrors);

    /// <summary>
    /// A fresh page with the SDK's postMessage tap installed BEFORE any page script runs, a
    /// <c>window.prompt</c> spy (issue #41's headline assertion is that it is never called), and the browser's
    /// own error channels collected.
    /// </summary>
    private static async Task<Instrumented> NewInstrumentedPageAsync(Launched launched)
    {
        var context = await launched.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var console = new List<string>();
        var errors = new List<string>();
        page.Console += (_, msg) =>
        {
            if (string.Equals(msg.Type, "error", StringComparison.Ordinal))
            {
                console.Add(msg.Text);
            }
        };
        page.PageError += (_, err) => errors.Add(err);

        await page.AddInitScriptAsync(
            "window.__charterEvents = [];" +
            "window.addEventListener('message', function (e) {" +
            "  if (e && e.data && e.data.channel === 'charter-annotate') {" +
            "    window.__charterEvents.push(e.data.type);" +
            "  }" +
            "});" +
            "window.__promptCalls = [];" +
            "window.prompt = function () { window.__promptCalls.push(1); return null; };");

        return new Instrumented(page, console, errors);
    }

    private static string CapabilityUrl(ReviewServer server, ReviewSession session)
        => new UriBuilder(server.Address) { Query = "key=" + session.Key.Value }.Uri.ToString();

    /// <summary>A selector for one of the SDK's own elements — it has no ids, only <c>data-charter-ui</c>.</summary>
    private static string Ui(string name) => "[data-charter-ui=\"" + name + "\"]";

    /// <summary>
    /// Wait until the SDK has emitted <paramref name="type"/> across the postMessage boundary. Polls with
    /// <c>page.evaluate</c> rather than <c>page.waitForFunction</c>: waitForFunction's polling loop
    /// <c>eval</c>s its predicate inside the page, which the served-page CSP (<c>script-src 'unsafe-inline'</c>,
    /// deliberately NO <c>'unsafe-eval'</c>) correctly refuses. Same bounded-deadline shape as
    /// <see cref="PollForAnswerAsync"/> — never a fixed sleep, and it fails loudly rather than hanging.
    /// </summary>
    private static async Task WaitForEventAsync(IPage page, string type, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await CountEventsAsync(page, type) > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("the SDK never emitted the '" + type + "' event within " + timeoutMs + "ms");
    }

    private static Task<int> CountEventsAsync(IPage page, string type)
        => page.EvaluateAsync<int>(
            "() => (window.__charterEvents || []).filter(function (t) { return t === '" + type + "'; }).length");

    /// <summary>
    /// The server's own PRE-DRAIN queue (<c>GET /api/annotations?key=…</c>) — the read the review panel is a
    /// front end for. Asserting against it proves each UI action crossed the whole browser→server boundary.
    /// </summary>
    private static async Task<JsonElement> ListAnnotationsAsync(Uri address, string key)
    {
        using var client = new HttpClient();
        var url = new UriBuilder(address) { Path = "api/annotations", Query = "key=" + key }.Uri;
        using var doc = JsonDocument.Parse(await client.GetStringAsync(url));
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Poll the loopback server's non-destructive answers peek (<c>GET /api/answers?key=…</c>) until the
    /// browser's submitted answer arrives, or time out. Proves the answer crossed the whole browser→server
    /// boundary, not merely that the form was styled.
    /// </summary>
    private static async Task<JsonElement?> PollForAnswerAsync(Uri address, string key)
    {
        using var client = new HttpClient();
        var peekUrl = new UriBuilder(address) { Path = "api/answers", Query = "key=" + key }.Uri;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var json = await client.GetStringAsync(peekUrl);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                return doc.RootElement[0].Clone();
            }

            await Task.Delay(150);
        }

        return null;
    }
}
