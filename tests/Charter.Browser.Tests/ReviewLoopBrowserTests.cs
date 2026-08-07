using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Charter.Core;
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
/// <remarks>
/// <c>partial</c> so the layout regression gate (Charter #5) can live in its own file —
/// <c>LayoutRegressionGateTests.cs</c> — while REUSING this suite's browser plumbing rather than forking it.
/// That reuse is load-bearing, not tidiness: <see cref="TryLaunchAsync"/> owns the <c>--hide-scrollbars</c>
/// opt-out and <see cref="NewContextAsync"/> owns the single navigation timeout, and a gate that rebuilt
/// either would measure the flag instead of the stylesheet (#68) or reintroduce the #66 flake.
/// </remarks>
[Trait("Category", "BrowserAcceptance")]
public sealed partial class ReviewLoopBrowserTests
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
                browser = await BrowserEngine.For(playwright).LaunchAsync(
                    new BrowserTypeLaunchOptions { Headless = true });
            }
            catch (Exception ex)
            {
                // No Chromium / no Playwright driver on this host — skip cleanly (never fail). The deterministic
                // server-side guards still assert the same symptoms on this OS.
                Skip.If(true, $"{BrowserEngine.Name}/Playwright unavailable on this host: " + ex.Message);
                return;
            }

            await using (browser)
            {
                var context = await NewContextAsync(browser);
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
                // The SDK auto-inits on DOMContentLoaded and emits 'ready'; wait on that event (no arbitrary
                // sleep). Via the bounded EvaluateAsync poll, NOT WaitForFunctionAsync: the latter's polling
                // loop `eval`s its predicate in the page, which the served CSP (no 'unsafe-eval') refuses — it
                // only ever appeared to work here because 'ready' is usually already true on the first check.
                await WaitForEventAsync(page, "ready");

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

    // ---- Charter #56: a HUMAN can answer every question mode, in a browser, with the mouse -------------

    // One question per mode. The matrix exists because the two #56 defects were mode-shaped: the missing
    // submit control broke ALL modes for a human, and the non-array `values` broke exactly free-text and
    // number (bool escaped only because mode inference mislabelled it a single). A per-mode round-trip is the
    // only shape of test that would have caught both.
    private const string ModesPlan =
        "# Every question mode\n\n" +
        "A plan whose only content is one question of each mode.\n\n" +
        ":::question\n" +
        "{\"id\":\"q-single\",\"title\":\"Pick one colour\",\"mode\":\"single\",\"target\":\"human\"," +
        "\"options\":[\"Red\",\"Green\",\"Blue\"]}\n" +
        ":::\n\n" +
        ":::question\n" +
        "{\"id\":\"q-multi\",\"title\":\"Pick the channels\",\"mode\":\"multi\",\"target\":\"human\"," +
        "\"options\":[\"Email\",\"Slack\",\"Webhook\"]}\n" +
        ":::\n\n" +
        ":::question\n" +
        "{\"id\":\"q-free\",\"title\":\"Say why\",\"mode\":\"free-text\",\"target\":\"human\"}\n" +
        ":::\n\n" +
        ":::question\n" +
        "{\"id\":\"q-bool\",\"title\":\"Ship it?\",\"mode\":\"bool\",\"target\":\"human\"}\n" +
        ":::\n\n" +
        ":::question\n" +
        "{\"id\":\"q-number\",\"title\":\"How many replicas?\",\"mode\":\"number\",\"target\":\"human\"}\n" +
        ":::\n";

    /// <summary>
    /// Charter #56, both halves, for EVERY mode: a human-realistic interaction (click a control / type a value,
    /// then CLICK THE SAVE BUTTON — never a scripted <c>requestSubmit()</c>) reaches the server with an answer
    /// whose <c>values</c> is an array carrying exactly what was chosen.
    ///
    /// P0 is guarded by clicking a real control: before the fix the form had no submit control at all, so no
    /// submit event could be produced by any human action and this test cannot pass by accident.
    /// P1 is guarded by the free-text / number / bool legs plus the zero-console-errors assertion: the old SDK
    /// posted a bare string / boolean, the server rejected it 400, and Chromium logs the failed request as a
    /// console error.
    /// </summary>
    [SkippableFact]
    public async Task Every_question_mode_answers_through_its_save_button_and_reaches_the_server()
    {
        // #111 — known WebKit defects, quarantined so the WebKit leg stays BLOCKING for everything else:
        // a new engine regression must fail CI immediately rather than hide behind these two.
        Skip.If(BrowserEngine.Name == "webkit", "Known WebKit defect - see issue #111.");

        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-question-modes-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, ModesPlan);

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

            // ---- single: click one radio ----
            await AssertSaveDisabledAsync(page, "q-single");
            await page.CheckAsync(Control("q-single", "input[type=radio][value=\"Red\"]"));
            await SaveAsync(page, "q-single");
            Assert.Equal(
                new[] { "Red" },
                await WaitForAnswerValuesAsync(server, session, "q-single"));

            // ---- multi: check two boxes; BOTH must ride the array ----
            await AssertSaveDisabledAsync(page, "q-multi");
            await page.CheckAsync(Control("q-multi", "input[type=checkbox][value=\"Email\"]"));
            await page.CheckAsync(Control("q-multi", "input[type=checkbox][value=\"Webhook\"]"));
            await SaveAsync(page, "q-multi");
            Assert.Equal(
                new[] { "Email", "Webhook" },
                await WaitForAnswerValuesAsync(server, session, "q-multi"));

            // ---- free-text: type into the textarea (the P1 400 lived here) ----
            await AssertSaveDisabledAsync(page, "q-free");
            await page.FillAsync(Control("q-free", "textarea"), "Because the read path stays Postgres-only.");
            await SaveAsync(page, "q-free");
            Assert.Equal(
                new[] { "Because the read path stays Postgres-only." },
                await WaitForAnswerValuesAsync(server, session, "q-free"));

            // ---- bool: the Yes radio. Charter #43 made this two radios; the SDK must now collect it AS a
            // bool (mode "bool" on the wire), not as an accidentally-inferred single.
            await AssertSaveDisabledAsync(page, "q-bool");
            await page.CheckAsync(Control("q-bool", "input[type=radio][value=\"true\"]"));
            await SaveAsync(page, "q-bool");
            var boolAnswer = await WaitForAnswerAsync(server, session, "q-bool");
            Assert.Equal(new[] { "true" }, ReadValues(boolAnswer));
            Assert.Equal("bool", boolAnswer.GetProperty("mode").GetString());

            // ---- number: type a number (the other P1 400) ----
            await AssertSaveDisabledAsync(page, "q-number");
            await page.FillAsync(Control("q-number", "input[type=number]"), "3");
            await SaveAsync(page, "q-number");
            Assert.Equal(
                new[] { "3" },
                await WaitForAnswerValuesAsync(server, session, "q-number"));

            // Every mode reported its own mode over the wire — the field the headless handoff routes on.
            foreach (var (questionId, mode) in new[]
                     {
                         ("q-single", "single"), ("q-multi", "multi"), ("q-free", "free-text"),
                         ("q-bool", "bool"), ("q-number", "number"),
                     })
            {
                Assert.Equal(
                    mode,
                    (await WaitForAnswerAsync(server, session, questionId)).GetProperty("mode").GetString());
            }

            // The whole point of asserting this here: a 400 on ANY leg above shows up as a console error, so
            // this single assertion is what makes the P1 regression impossible to reintroduce quietly.
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
    /// A RESOLVED question must still be revisable: a review round exists to change decisions. The Save button
    /// starts disabled (the recorded answer is already what is selected — there is nothing to submit), enables
    /// the moment the reviewer picks something different, and re-submitting posts the NEW value.
    /// </summary>
    [SkippableFact]
    public async Task Answered_question_can_be_re_answered_and_save_tracks_the_change()
    {
        const string plan =
            "# A settled decision\n\n" +
            ":::question\n" +
            "{\"id\":\"q-settled\",\"title\":\"Which store?\",\"mode\":\"single\",\"target\":\"human\"," +
            "\"options\":[\"Postgres\",\"DynamoDB\"],\"answer\":[\"Postgres\"]}\n" +
            ":::\n";

        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-question-answered-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, plan);

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

            // The answered surface still reads as answered (Charter #48) — and it still offers Save.
            Assert.True(await page.EvaluateAsync<bool>(
                "() => document.querySelector('form[data-question-id=\"q-settled\"]')" +
                ".classList.contains('answered')"));
            Assert.Equal("true", await page.GetAttributeAsync(Question("q-settled"), "data-answered"));
            await page.WaitForSelectorAsync(Question("q-settled") + " .question-status");
            Assert.True(await page.IsCheckedAsync(Control("q-settled", "input[type=radio][value=\"Postgres\"]")));

            // Nothing has changed yet, so there is nothing to submit.
            await AssertSaveDisabledAsync(page, "q-settled");

            // Revise the decision — Save enables, and the NEW value is what reaches the server.
            await page.CheckAsync(Control("q-settled", "input[type=radio][value=\"DynamoDB\"]"));
            await SaveAsync(page, "q-settled");
            Assert.Equal(
                new[] { "DynamoDB" },
                await WaitForAnswerValuesAsync(server, session, "q-settled"));

            // Having landed, Save settles back to "nothing to submit" against the newly recorded answer.
            await AssertSaveDisabledAsync(page, "q-settled");

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
    /// Enter must submit where the control makes that natural, WITHOUT costing a free-text answer its
    /// newlines: a <c>&lt;textarea&gt;</c> keeps Enter as a newline and submits on Ctrl/⌘+Enter.
    /// </summary>
    [SkippableFact]
    public async Task Free_text_keeps_enter_as_a_newline_and_submits_on_ctrl_enter()
    {
        const string plan =
            "# Free text\n\n" +
            ":::question\n" +
            "{\"id\":\"q-why\",\"title\":\"Say why\",\"mode\":\"free-text\",\"target\":\"human\"}\n" +
            ":::\n";

        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-question-ctrlenter-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, plan);

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

            var textarea = Control("q-why", "textarea");
            await page.ClickAsync(textarea);
            await page.Keyboard.TypeAsync("first line");
            await page.Keyboard.PressAsync("Enter");
            await page.Keyboard.TypeAsync("second line");

            // Enter did NOT submit — it inserted a newline, which is the only sane behaviour in a textarea.
            Assert.Equal("first line\nsecond line", await page.InputValueAsync(textarea));
            Assert.Equal(
                0, (await ListAnswersAsync(server.Address, session.Key.Value)).GetArrayLength());

            // Ctrl+Enter submits, newlines intact.
            await page.Keyboard.PressAsync("Control+Enter");
            Assert.Equal(
                new[] { "first line\nsecond line" },
                await WaitForAnswerValuesAsync(server, session, "q-why"));

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

    // ---- Charter #68: a wide table must be REACHABLE, not clipped ----------------------------------------

    /// <summary>
    /// A plan whose first table is deliberately wider than any realistic review column — three columns of long
    /// unbreakable identifier tokens (underscores, so UAX#14 gives the browser no break opportunity inside a
    /// cell and the table's MIN-content width genuinely exceeds the container). The second table is narrow and
    /// must stay visually unharmed. The paragraph is the width baseline the narrow table is measured against.
    /// </summary>
    private const string WideTablePlan =
        "# A plan with a wide table\n\n" +
        "Prose above the table, at the content column's full width.\n\n" +
        "| Source | Symbol | Guardrail |\n" +
        "| --- | --- | --- |\n" +
        "| `src_Charter_Core_CharterContainerRenderer_cs` " +
        "| `WriteQuestionControls_HtmlRenderer_QuestionSpec` " +
        "| `Renderer_Wraps_Every_Table_In_A_Scroll_Container` |\n" +
        "| `src_Charter_Server_AnchorResolution_cs` " +
        "| `ResolveAtDrainTime_PollEnvelope_AnchorStatus` " +
        "| `Server_Drain_Rebinds_Every_Anchor_To_The_Current_Plan` |\n\n" +
        "A narrow table, which must stay visually unharmed:\n\n" +
        "| A | B |\n" +
        "| --- | --- |\n" +
        "| 1 | 2 |\n";

    /// <summary>
    /// Charter #68, reproduced exactly as the reporter measured it and then proven fixed.
    ///
    /// The issue's probe at a 1000px viewport found the table declaring itself scrollable
    /// (<c>scrollWidth 928 &gt; clientWidth 832</c>, <c>canScroll: true</c>) while a REAL
    /// <c>page.mouse.wheel(500, 0)</c> over it left <c>scrollLeft</c> at <c>0</c> — 96px of content a reviewer
    /// could see was cut off but could not reach. That happened because <c>overflow-x</c> sat on the
    /// <c>&lt;table&gt;</c> element itself; the fix moves it to a wrapping <c>.table-scroll</c> container.
    ///
    /// This test re-runs that probe and asserts the wheel now MOVES it — at the issue's own 1000px viewport,
    /// and again at 660px, which is what is left of a 1000px window beside the 340px review-notes panel (the
    /// issue's "the sidebar makes it worse" condition, and the realistic reviewing width). It also pins the
    /// two things a wrapper could quietly break: the annotation anchor (a note on a table cell must still
    /// resolve to the TABLE's stable id, all the way through to the server's pre-drain queue) and a narrow
    /// table's layout.
    /// </summary>
    [SkippableFact]
    public async Task Wide_table_scrolls_in_its_wrapper_at_a_narrow_viewport_without_breaking_anchoring()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-wide-table-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, WideTablePlan);

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        try
        {
            // Scrollbars VISIBLE: the discoverability half of this fix is a persistent scrollbar, and
            // Playwright's default --hide-scrollbars would zero every one of them.
            var launched = await TryLaunchAsync(showScrollbars: true);
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;

            // The issue's own viewport. body is `max-width: 52rem` (832px) content-box, so the content column
            // is exactly the 832px the reporter measured.
            await page.SetViewportSizeAsync(1000, 800);
            await page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(page, "ready");

            // ---- the markup shape: a wrapping scroll container, with the anchor still on the table ----
            Assert.Equal(2, await page.Locator(ScrollContainer).CountAsync());

            var shape = await ProbeAsync(page, Wide);
            Assert.Equal("DIV", shape.GetProperty("tag").GetString());
            Assert.Equal("auto", shape.GetProperty("overflowX").GetString());

            // The wrapper is ANCHOR-INVISIBLE: the stable id is on the <table>, never on the container.
            var tableId = shape.GetProperty("tableId").GetString();
            Assert.False(string.IsNullOrEmpty(tableId), "the renderer must stamp a stable block id on the table");
            Assert.Equal(string.Empty, shape.GetProperty("containerId").GetString());

            // Keyboard reachability: a scroll region only a mouse can enter still hides the columns.
            Assert.Equal(0, shape.GetProperty("tabIndex").GetInt32());
            Assert.Equal("region", shape.GetProperty("role").GetString());
            Assert.False(
                string.IsNullOrEmpty(shape.GetProperty("ariaLabel").GetString()),
                "the scroll region needs an accessible name or it announces as nothing");

            // ---- the issue's probe, at the issue's viewport ----
            Assert.True(
                shape.GetProperty("canScroll").GetBoolean(),
                "the wide table must genuinely overflow at 1000px — otherwise this test proves nothing: " + shape);
            Assert.Equal(0, shape.GetProperty("scrollLeft").GetDouble());

            // ---- discoverability: an overflowing region shows a PERSISTENT scrollbar ----
            // A silently-scrollable region is nearly as bad as a clipped one. The gutter is asserted at the
            // exact 10px the stylesheet declares, not merely "> 0": the platform default in this environment
            // is 26px, so an exact match is what attributes the affordance to charter.css's
            // ::-webkit-scrollbar rule rather than to whatever the browser would have drawn anyway.
            Assert.Equal(10, shape.GetProperty("gutterPx").GetInt32());

            // ---- the defect itself: a real horizontal wheel gesture over it must MOVE it ----
            var afterWheel = await WheelOverAsync(page, Wide);
            Assert.True(
                afterWheel > 0,
                "Charter #68: mouse.wheel(500, 0) over the table left scrollLeft at " + afterWheel +
                " — the clipped columns are still unreachable.");

            // ---- and the keyboard path reaches them too ----
            await ResetScrollAsync(page, Wide);
            await page.Locator(ScrollContainer).Nth(Wide).FocusAsync();
            Assert.True(
                await page.EvaluateAsync<bool>(
                    "i => document.activeElement === document.querySelectorAll('.table-scroll')[i]", Wide),
                "the scroll container must be focusable (tabindex) or keyboard-only reviewers cannot reach it");

            for (var i = 0; i < 12; i++)
            {
                await page.Keyboard.PressAsync("ArrowRight");
            }

            Assert.True(
                await PollScrollLeftAsync(page, Wide) > 0,
                "ArrowRight on the focused scroll container did not move it");

            // ---- the anchor still resolves to the TABLE, end to end through the server ----
            await page.Locator(ScrollContainer).Nth(Wide).Locator("td").First
                .ClickAsync(new LocatorClickOptions { Modifiers = new[] { KeyboardModifier.Alt } });
            await page.WaitForSelectorAsync(Ui("composer"));
            await page.FillAsync(Ui("composer-input"), "This column is the one that was unreachable.");
            await page.ClickAsync(Ui("composer-save"));
            await WaitForEventAsync(page, "submitted");

            var listed = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(1, listed.GetArrayLength());
            Assert.Equal(tableId, listed[0].GetProperty("anchorId").GetString());

            // ---- the narrow table is not visually harmed: it neither scrolls nor shifts ----
            var narrow = await ProbeAsync(page, Narrow);
            Assert.False(
                narrow.GetProperty("canScroll").GetBoolean(),
                "a table that fits must not become a scroll region: " + narrow);

            // No scrollbar, no gutter, no false "there is more here" signal on a table that fits.
            Assert.Equal(0, narrow.GetProperty("gutterPx").GetInt32());
            Assert.True(
                Math.Abs(narrow.GetProperty("tableLeft").GetDouble() - narrow.GetProperty("proseLeft").GetDouble()) <= 1,
                "the wrapper must not indent the table: " + narrow);
            Assert.True(
                Math.Abs(narrow.GetProperty("tableWidth").GetDouble() - narrow.GetProperty("proseWidth").GetDouble()) <= 2,
                "the wrapper must not narrow the table: " + narrow);

            // ---- the sidebar condition: 660px is what is left beside the 340px review-notes panel ----
            await page.SetViewportSizeAsync(660, 800);
            var narrowed = await ProbeAsync(page, Wide);
            Assert.True(
                narrowed.GetProperty("canScroll").GetBoolean(),
                "the wide table must still overflow once the content column is narrowed: " + narrowed);
            Assert.True(
                await WheelOverAsync(page, Wide) > 0,
                "Charter #68 at the realistic reviewing width: the table still does not scroll.");

            // ---- the OFFLINE artifact behaves identically: no server, no SDK, still scrollable ----
            // The saved/exported file carries no annotation SDK (invariant 1) and a strict CSP, so a fix that
            // leaned on script would work only while `charter review` was running. Loaded straight off disk
            // over file://, the same gesture must still reach the same columns.
            var exportPath = Path.ChangeExtension(planPath, ".export.html");
            await File.WriteAllTextAsync(
                exportPath, ArtifactExporter.Export(WideTablePlan, Path.GetDirectoryName(planPath)!));
            try
            {
                await page.GotoAsync(
                    new Uri(exportPath).AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.Load });

                Assert.False(
                    await page.EvaluateAsync<bool>("() => typeof window.CharterAnnotate !== 'undefined'"),
                    "the exported artifact must ship WITHOUT the annotation SDK");

                var offline = await ProbeAsync(page, Wide);
                Assert.True(
                    offline.GetProperty("canScroll").GetBoolean(),
                    "the exported artifact's wide table must still overflow: " + offline);
                Assert.True(
                    await WheelOverAsync(page, Wide) > 0,
                    "the exported artifact does not scroll — the fix depends on the review server.");
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
    /// Every table scroll container, in document order. Addressed by INDEX rather than by a positional CSS
    /// pseudo-class: the SDK appends its own <c>&lt;div&gt;</c> chrome to <c>&lt;body&gt;</c> at runtime, so
    /// <c>:last-of-type</c> would silently stop matching a table once the review panel exists.
    /// </summary>
    private const string ScrollContainer = ".table-scroll";

    /// <summary>The deliberately over-wide table, and the narrow one that must stay unharmed.</summary>
    private const int Wide = 0;

    private const int Narrow = 1;

    /// <summary>An arrow function over the <paramref name="body"/>-th table scroll container.</summary>
    private static string ForContainer(string body)
        => "i => { const el = document.querySelectorAll('.table-scroll')[i]; if (!el) return null; " + body + " }";

    /// <summary>
    /// The issue's probe, verbatim in spirit: the container's own scroll geometry plus the structural facts a
    /// wrapper could break. Returned as JSON so the whole shape lands in any assertion message.
    /// </summary>
    private static async Task<JsonElement> ProbeAsync(IPage page, int index)
    {
        var json = await page.EvaluateAsync<string>(
            ForContainer(
                "const t = el.querySelector('table');" +
                "const p = document.querySelector('body > p');" +
                "const tr = t.getBoundingClientRect(); const pr = p.getBoundingClientRect();" +
                "return JSON.stringify({" +
                "  tag: el.tagName," +
                "  containerId: el.id," +
                "  tableId: t.id," +
                "  tabIndex: el.tabIndex," +
                "  role: el.getAttribute('role')," +
                "  ariaLabel: el.getAttribute('aria-label')," +
                "  overflowX: getComputedStyle(el).overflowX," +
                // The scrollbar's own layout footprint: 0 means no bar is drawn at all.
                "  gutterPx: el.offsetHeight - el.clientHeight," +
                "  scrollWidth: el.scrollWidth," +
                "  clientWidth: el.clientWidth," +
                "  canScroll: el.scrollWidth > el.clientWidth," +
                "  scrollLeft: el.scrollLeft," +
                "  tableLeft: tr.left, tableWidth: tr.width," +
                "  proseLeft: pr.left, proseWidth: pr.width" +
                "});"),
            index);

        using var doc = JsonDocument.Parse(json!);
        return doc.RootElement.Clone();
    }

    private static Task ResetScrollAsync(IPage page, int index)
        => page.EvaluateAsync(ForContainer("el.scrollLeft = 0; return null;"), index);

    /// <summary>
    /// Reset the container to its left edge, put the real mouse pointer over it, and send a real horizontal
    /// wheel — the exact gesture from the issue (<c>page.mouse.wheel(500, 0)</c>) — then poll for the
    /// resulting <c>scrollLeft</c>. The pointer is placed in the container's LEFT quarter so the
    /// narrowed-viewport leg cannot land under the fixed review-notes panel and scroll that instead.
    /// </summary>
    private static async Task<double> WheelOverAsync(IPage page, int index)
    {
        await ResetScrollAsync(page, index);

        var box = await page.Locator(ScrollContainer).Nth(index).BoundingBoxAsync();
        Assert.NotNull(box);
        await page.Mouse.MoveAsync(box!.X + (box.Width / 4), box.Y + (box.Height / 2));
        await page.Mouse.WheelAsync(500, 0);

        return await PollScrollLeftAsync(page, index);
    }

    /// <summary>
    /// Poll <c>scrollLeft</c> until it moves off zero, or give up. Wheel scrolling is asynchronous (the
    /// compositor applies it), so it cannot be read back synchronously — and it must be a bounded
    /// <c>EvaluateAsync</c> poll, never <c>WaitForFunctionAsync</c>, whose in-page <c>eval</c> the served-page
    /// CSP correctly refuses.
    /// </summary>
    private static async Task<double> PollScrollLeftAsync(IPage page, int index, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        double scrollLeft = 0;
        while (DateTime.UtcNow < deadline)
        {
            scrollLeft = await page.EvaluateAsync<double>(
                "i => { const el = document.querySelectorAll('.table-scroll')[i]; return el ? el.scrollLeft : -1; }",
                index);
            if (scrollLeft > 0)
            {
                return scrollLeft;
            }

            await Task.Delay(50);
        }

        return scrollLeft;
    }

    // ---- question-form helpers ---------------------------------------------------------------------------

    /// <summary>The rendered <c>&lt;form&gt;</c> for <paramref name="questionId"/>.</summary>
    private static string Question(string questionId)
        => "form[data-question-id=\"" + questionId + "\"]";

    /// <summary>A control inside <paramref name="questionId"/>'s form.</summary>
    private static string Control(string questionId, string selector)
        => Question(questionId) + " " + selector;

    /// <summary>The Save button the renderer emits in every question form (Charter #56 / P0).</summary>
    private static string SaveButton(string questionId)
        => Control(questionId, "button[type=submit]");

    /// <summary>
    /// The Save button exists but is DISABLED — "there is nothing to submit". Asserting the button EXISTS is
    /// the direct P0 guard; asserting it is disabled is the enabled-state rule.
    /// </summary>
    private static async Task AssertSaveDisabledAsync(IPage page, string questionId)
    {
        await page.WaitForSelectorAsync(SaveButton(questionId));
        Assert.True(
            await page.IsDisabledAsync(SaveButton(questionId)),
            questionId + ": Save must be disabled while the form matches the recorded answer");
    }

    /// <summary>
    /// Answer by CLICKING the Save button, the way a human does. Playwright's actionability check refuses to
    /// click a disabled button, so this also asserts the button ENABLED once the answer changed.
    /// </summary>
    private static async Task SaveAsync(IPage page, string questionId)
    {
        Assert.True(
            await page.IsEnabledAsync(SaveButton(questionId)),
            questionId + ": Save must enable once the reviewer's answer differs from the recorded one");
        await page.ClickAsync(SaveButton(questionId));
    }

    /// <summary>
    /// Poll the non-destructive answers peek until <paramref name="questionId"/>'s answer arrives. Bounded
    /// <c>EvaluateAsync</c>-free HTTP polling — never <c>WaitForFunctionAsync</c>, whose in-page
    /// <c>eval</c> the served CSP correctly refuses.
    /// </summary>
    private static async Task<JsonElement> WaitForAnswerAsync(
        ReviewServer server, ReviewSession session, string questionId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var answers = await ListAnswersAsync(server.Address, session.Key.Value);
            JsonElement? latest = null;
            foreach (var answer in answers.EnumerateArray())
            {
                if (string.Equals(
                        answer.GetProperty("questionId").GetString(), questionId, StringComparison.Ordinal))
                {
                    // The LAST match: re-answering a question queues a second answer, and the newest is the
                    // reviewer's current decision.
                    latest = answer.Clone();
                }
            }

            if (latest.HasValue)
            {
                return latest.Value;
            }

            await Task.Delay(100);
        }

        Assert.Fail("no answer for '" + questionId + "' reached the server within 15s");
        throw new InvalidOperationException("unreachable");
    }

    private static async Task<string[]> WaitForAnswerValuesAsync(
        ReviewServer server, ReviewSession session, string questionId)
        => ReadValues(await WaitForAnswerAsync(server, session, questionId));

    private static string[] ReadValues(JsonElement answer)
        => answer.GetProperty("values").EnumerateArray().Select(v => v.GetString()!).ToArray();

    private static async Task<JsonElement> ListAnswersAsync(Uri address, string key)
    {
        using var client = new HttpClient();
        var url = new UriBuilder(address) { Path = "api/answers", Query = "key=" + key }.Uri;
        using var doc = JsonDocument.Parse(await client.GetStringAsync(url));
        return doc.RootElement.Clone();
    }

    private static void AssertNoBrowserErrors(Instrumented instrumented)
    {
        Assert.True(
            instrumented.ConsoleErrors.Count == 0,
            "console errors present:\n  " + string.Join("\n  ", instrumented.ConsoleErrors));
        Assert.True(
            instrumented.PageErrors.Count == 0,
            "page errors present:\n  " + string.Join("\n  ", instrumented.PageErrors));
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
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

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
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

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

    // ---- the in-page round hand-off: "Send to agent" -----------------------------------------------------

    /// <summary>
    /// The reviewer hands their round to the agent WITHOUT leaving the page. The button starts disabled
    /// ("nothing to send"), enables once there is queued feedback, and on click posts the hand-off to the
    /// server — proven against the server's own <c>GET /api/review</c>, not merely by the button changing
    /// colour — after which it reflects the sent state and the panel confirms it. Zero console and page errors
    /// throughout: a rejected fetch (wrong route, blocked by CSP, bad shape) surfaces as a console error, so
    /// that assertion is what makes a silently-broken button impossible to ship.
    /// </summary>
    [SkippableFact]
    public async Task Send_to_agent_hands_the_round_off_and_the_button_reflects_its_state()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-send-to-agent-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, Plan);

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
            await WaitForEventAsync(page, "round-loaded");

            await page.ClickAsync(Ui("panel-toggle"));

            // ---- nothing queued: the control exists but has nothing to hand off ----
            await page.WaitForSelectorAsync(Ui("send-to-agent"));
            Assert.True(
                await page.IsDisabledAsync(Ui("send-to-agent")),
                "Send to agent must be disabled while there is nothing pending to send");
            Assert.Equal("false", await page.GetAttributeAsync(Ui("send-to-agent"), "data-charter-sent"));

            // ---- queue one note, the way a reviewer does ----
            await page.ClickAsync("body > p", new PageClickOptions { Modifiers = new[] { KeyboardModifier.Alt } });
            await page.WaitForSelectorAsync(Ui("composer-input"));
            await page.FillAsync(Ui("composer-input"), "Spell out the acceptance criteria here.");
            await page.ClickAsync(Ui("composer-save"));
            await WaitForEventAsync(page, "submitted");

            // The button enables off the SERVER's pending count (a live reload wipes any local tally), so wait
            // for the refresh rather than assuming it is synchronous with the submit.
            await WaitForSendEnabledAsync(page);

            var before = await ReviewStatusAsync(server.Address, session.Key.Value);
            Assert.False(before.GetProperty("submitted").GetBoolean());
            Assert.Equal(1, before.GetProperty("pending").GetProperty("annotations").GetInt32());

            // ---- click it: the hand-off must REACH THE SERVER ----
            await page.ClickAsync(Ui("send-to-agent"));
            await WaitForEventAsync(page, "round-sent");

            var after = await ReviewStatusAsync(server.Address, session.Key.Value);
            Assert.True(after.GetProperty("submitted").GetBoolean(), "the click must record the hand-off server-side");
            Assert.Equal(1, after.GetProperty("submission").GetProperty("annotations").GetInt32());

            // ---- and the button reflects it: sent, and not re-sendable until the agent takes the round ----
            Assert.Equal("true", await page.GetAttributeAsync(Ui("send-to-agent"), "data-charter-sent"));
            Assert.True(
                await page.IsDisabledAsync(Ui("send-to-agent")),
                "Send to agent must disable once the round is handed off");
            Assert.Contains(
                "Sent", (await page.InnerTextAsync(Ui("panel-status"))).Trim(), StringComparison.Ordinal);

            // ---- the note itself is untouched: a hand-off SIGNALS, it does not drain or apply anything ----
            Assert.Equal(1, (await ListAnnotationsAsync(server.Address, session.Key.Value)).GetArrayLength());
            Assert.Equal(Plan, await File.ReadAllTextAsync(planPath));

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
    /// Charter #75 item 2 — the replaced-plan quarantine must reach the REVIEWER, not just stderr.
    /// <c>charter review</c> is frequently launched by an agent, so the stream carrying "your earlier notes were
    /// set aside, here is how to get them back" is often one no human ever sees, and the panel said nothing at
    /// all. This is the browser-side proof that it now does: the notice is real DOM, it names the recovery, and
    /// (invariant 1) it is runtime-only chrome that <c>dispose()</c> removes.
    /// </summary>
    [SkippableFact]
    public async Task Replaced_plan_tells_the_reviewer_in_the_panel_that_their_queue_was_set_aside()
    {
        const string original =
            "# Rate limiting\n\nThe read path stays Postgres-only until the write path is proven.\n";
        const string replacement =
            "# Tenant onboarding\n\nEvery tenant gets an isolated schema provisioned at signup time.\n";

        var directory = Path.Combine(
            Path.GetTempPath(), "charter-stale-panel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var planPath = Path.Combine(directory, "plan.charter.md");
        var sidecarDirectory = Path.Combine(directory, "sidecars");
        Directory.CreateDirectory(sidecarDirectory);

        await File.WriteAllTextAsync(planPath, original);

        // Seed the queue exactly as a previous session would have left it, then replace the plan at the SAME
        // path — the #67 shape, whose quarantine is what this notice reports.
        var anchors = SourceMap.Build(original).Anchors.OrderBy(a => a, StringComparer.Ordinal).ToList();
        ReviewSidecar.WriteState(
            ReviewSidecar.PathForPlan(sidecarDirectory, planPath),
            planPath,
            anchors.Select((anchor, i) => new Annotation(
                "seed-" + i, AnnotationKind.Element, anchor, "a note from the previous document")).ToList(),
            Array.Empty<Answer>());
        await File.WriteAllTextAsync(planPath, replacement);

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session,
            new ReviewServerOptions
            {
                BindAddress = IPAddress.Loopback,
                Port = 0,
                SidecarDirectory = sidecarDirectory,
            });

        try
        {
            Assert.NotNull(server.StaleAnnotations);

            var launched = await TryLaunchAsync();
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;

            await page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(page, "ready");

            // The reviewer did not have to go looking for it: the panel opens itself, because notes silently
            // missing is exactly the situation a reviewer would otherwise misread as "they were handed off".
            await page.WaitForSelectorAsync(Ui("stale-queue"));
            await WaitForEventAsync(page, "stale-queue");
            Assert.False(
                await page.Locator(Ui("panel")).IsHiddenAsync(),
                "a set-aside queue must open the panel rather than hide its own explanation");

            var notice = (await page.InnerTextAsync(Ui("stale-queue"))).Trim();
            Assert.Contains("set aside", notice, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--keep-annotations", notice, StringComparison.Ordinal);
            Assert.Contains("Nothing was deleted", notice, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                server.StaleAnnotations!.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                await page.GetAttributeAsync(Ui("stale-queue"), "data-charter-stale-count"));

            // A local absolute path must never reach page DOM — the notice names the file, not its location.
            Assert.DoesNotContain(sidecarDirectory, notice, StringComparison.OrdinalIgnoreCase);

            // Invariant 1: it is SDK chrome, so it goes when the SDK goes and it was never in the artifact.
            await page.EvaluateAsync("() => window.CharterAnnotate.dispose()");
            Assert.Equal(0, await page.Locator(Ui("stale-queue")).CountAsync());
            Assert.DoesNotContain(
                "charter-panel-stale", await File.ReadAllTextAsync(planPath), StringComparison.Ordinal);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }

    /// <summary>
    /// The mid-review guard: a reviewer part-way through ANSWERING a question must not have the page
    /// navigated out from under them when the agent revises the plan. An unsaved answer is deferred exactly
    /// as a half-typed note is (Charter #41's banner), and saving it releases the deferred reload.
    /// </summary>
    [SkippableFact]
    public async Task Unsaved_answer_defers_a_live_reload_instead_of_being_discarded()
    {
        const string plan =
            "# Mid-review\n\n" +
            "An ordinary prose paragraph.\n\n" +
            ":::question\n" +
            "{\"id\":\"q-mid\",\"title\":\"Which store?\",\"mode\":\"single\",\"target\":\"human\"," +
            "\"options\":[\"Postgres\",\"DynamoDB\"]}\n" +
            ":::\n";

        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-midreview-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, plan);

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        using var stopNudger = new CancellationTokenSource();
        Task? nudger = null;
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

            // The reviewer picks an answer but has not saved it yet — the decision exists only in the form.
            await page.CheckAsync(Control("q-mid", "input[type=radio][value=\"DynamoDB\"]"));
            await page.EvaluateAsync("() => { window.__charterNotReloaded = true; }");

            // The agent revises the plan underneath them. Re-touch on a cadence so the test never depends on a
            // single write landing after the server's FileSystemWatcher is armed.
            nudger = Task.Run(async () =>
            {
                var edit = 0;
                while (!stopNudger.Token.IsCancellationRequested)
                {
                    try
                    {
                        await File.WriteAllTextAsync(
                            planPath, plan + "\nEdit " + (++edit) + " by the agent.\n", stopNudger.Token);
                        await Task.Delay(TimeSpan.FromMilliseconds(150), stopNudger.Token);
                    }
                    catch (IOException)
                    {
                        // A transient sharing conflict with the server's per-request read is harmless.
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });

            await WaitForEventAsync(page, "reload-deferred", 30_000);
            stopNudger.Cancel();

            // The choice survives, the page never navigated, and the reload is offered rather than taken.
            Assert.True(await page.IsCheckedAsync(Control("q-mid", "input[type=radio][value=\"DynamoDB\"]")));
            Assert.True(
                await page.EvaluateAsync<bool>("() => window.__charterNotReloaded === true"),
                "the SDK must NOT navigate while an answer is unsaved");
            await page.WaitForSelectorAsync(Ui("reload-banner"));

            AssertNoBrowserErrors(instrumented);
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

    /// <summary>
    /// Wait until the panel's "Send to agent" control is enabled. Bounded Playwright actionability polling —
    /// never <c>WaitForFunctionAsync</c>, whose in-page <c>eval</c> the served CSP correctly refuses.
    /// </summary>
    private static async Task WaitForSendEnabledAsync(IPage page, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await page.IsEnabledAsync(Ui("send-to-agent")))
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("Send to agent never enabled once feedback was queued");
    }

    /// <summary>The server's own round status (<c>GET /api/review?key=…</c>) — the hand-off's system of record.</summary>
    private static async Task<JsonElement> ReviewStatusAsync(Uri address, string key)
    {
        using var client = new HttpClient();
        var url = new UriBuilder(address) { Path = "api/review", Query = "key=" + key }.Uri;
        using var doc = JsonDocument.Parse(await client.GetStringAsync(url));
        return doc.RootElement.Clone();
    }

    // ---- git-mediated team review (docs/plans/03-git-mediated-team-review.md, steps 2-3) -----------------

    /// <summary>
    /// The team-review loop in a real browser: a comment authored IN THE PAGE lands as a durable record in
    /// this author's log beside the plan, and a SECOND author's log — arriving while the server runs, exactly
    /// as a <c>git pull</c> would deliver it — shows up in the same panel with its author, its actor and its
    /// status. Also pins the two renderings the design is most insistent about: a contested comment shows BOTH
    /// sides, and an orphan shows its quote and is never called "addressed".
    /// </summary>
    [SkippableFact]
    public async Task Review_panel_shows_this_authors_committed_comment_and_a_teammates_log()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "charter-team-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var planPath = Path.Combine(directory, "team.charter.md");
        await File.WriteAllTextAsync(planPath, Plan);

        var alice = new ReviewLogWriter(planPath, new ReviewAuthor("Alice Ng", "alice@example.com"));
        var bob = new ReviewLogWriter(planPath, new ReviewAuthor("Bob Chen", "bob@example.com"));

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(session, new ReviewServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            ReviewLog = alice,
        });

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
            await WaitForEventAsync(page, "review-log-loaded");

            // ---- a comment authored in the page becomes a COMMITTED record beside the plan ----
            await page.ClickAsync("body > p", new PageClickOptions { Modifiers = new[] { KeyboardModifier.Alt } });
            await page.WaitForSelectorAsync(Ui("composer-input"));
            await page.FillAsync(Ui("composer-input"), "This paragraph needs a concrete example.");
            await page.ClickAsync(Ui("composer-save"));
            await WaitForEventAsync(page, "submitted");

            await WaitForFileAsync(alice.LogPath);
            var written = await File.ReadAllTextAsync(alice.LogPath);
            Assert.Contains("\"op\":\"create\"", written, StringComparison.Ordinal);
            Assert.Contains("\"email\":\"alice@example.com\"", written, StringComparison.Ordinal);
            Assert.Contains("This paragraph needs a concrete example.", written, StringComparison.Ordinal);
            Assert.EndsWith("\n", written, StringComparison.Ordinal);

            // ...and the panel shows it as this author's own, committed and open.
            var mine = page.Locator(Ui("item")).First;
            await page.WaitForSelectorAsync(Ui("item-status"));
            Assert.Equal("true", await mine.GetAttributeAsync("data-charter-committed"));
            Assert.Equal("open", await mine.GetAttributeAsync("data-charter-status"));
            Assert.Equal("alice@example.com", await mine.GetAttributeAsync("data-charter-author-email"));
            Assert.Contains("Alice Ng", await mine.Locator(Ui("item-author")).InnerTextAsync(), StringComparison.Ordinal);

            // ---- a SECOND author's log arrives beside the plan while the server runs ----
            var anchors = SourceMap.Build(Plan).Anchors.OrderBy(a => a, StringComparer.Ordinal).ToList();
            var bobs = bob.AppendCreate(
                new ReviewAnchor(anchors[1], "element", "a quote from the plan", null),
                "The write path needs a retry budget.");
            var contested = bob.AppendCreate(
                new ReviewAnchor(anchors[2], "element", "another quote", null), "Is Postgres right here?");
            // Two orphans, differing ONLY in the revision they were written against (§4.3.1). The panel's
            // strong sentence — "the plan has CHANGED since this comment was written" — is a claim about the
            // whole document, and it is earned by exactly one of them.
            var orphan = bob.AppendCreate(
                new ReviewAnchor("b-no-such-block", "element", "the read path will be built after",
                    PlanHash("# An entirely different document\n")),
                "a note whose block the agent has since rewritten");
            var orphanOnAnUnchangedPlan = bob.AppendCreate(
                new ReviewAnchor("b-also-no-such-block", "element", "a quote", PlanHash(Plan)),
                "a note whose plan is byte-identical to what the reviewer saw");

            // Concurrent, disagreeing settlements: neither observed the other (prev is null on both), so the
            // fold reports CONTESTED rather than ordering them by a clock nobody synchronized.
            bob.AppendResolve(contested.Id, prev: null);
            alice.Append(new ReviewRecord
            {
                Version = ReviewRecord.CurrentVersion,
                Id = ReviewLogWriter.NewId(ReviewOpKind.Reopen),
                Op = ReviewOps.Token(ReviewOpKind.Reopen),
                Author = alice.Author,
                Target = contested.Id,
            });

            // The server watches `.review/`, so this refreshes the panel WITHOUT a page navigation. Re-touch
            // on a cadence so the test never depends on one write landing after the watcher is armed.
            //
            // Wait on the LAST write's effect (the contested chip), not the FIRST comment's presence. The
            // watcher fires on Bob's very first append, so a `review-log` re-read can be in flight while the
            // rest of these records are still being written; the panel renders from ONE fold, so waiting for
            // Bob's comment could be satisfied by a fold that had seen his `resolve` but not yet Alice's
            // `reopen` — and every assertion below would then read a half-written review. That is not
            // hypothetical: it reproduced on Linux, where the timing differs from Windows. The reopen is
            // written after everything else here, so its effect is the honest "it has all landed" signal.
            await WaitForSelectorWhileTouchingAsync(
                page,
                "[data-annotation-id=\"" + contested.Id + "\"][data-charter-status=\"contested\"]",
                new[] { bob.LogPath, alice.LogPath });

            var fromBob = page.Locator("[data-annotation-id=\"" + bobs.Id + "\"]");
            Assert.Equal("bob@example.com", await fromBob.GetAttributeAsync("data-charter-author-email"));
            Assert.Contains("Bob Chen", await fromBob.Locator(Ui("item-author")).InnerTextAsync(), StringComparison.Ordinal);
            Assert.Equal(
                "The write path needs a retry budget.",
                (await fromBob.Locator(Ui("item-note")).InnerTextAsync()).Trim());

            // A teammate's comment is not this reviewer's to withdraw, but anyone may resolve it.
            Assert.Equal(0, await fromBob.Locator(Ui("item-delete")).CountAsync());
            Assert.Equal(1, await fromBob.Locator(Ui("item-resolve")).CountAsync());

            // ---- contested: BOTH sides, with their authors ----
            var disputed = page.Locator("[data-annotation-id=\"" + contested.Id + "\"]");
            Assert.Equal("contested", await disputed.GetAttributeAsync("data-charter-status"));
            var sides = (await disputed.Locator(Ui("item-side")).AllInnerTextsAsync()).ToList();
            Assert.Equal(2, sides.Count);
            Assert.Contains(sides, s => s.Contains("resolved by Bob Chen", StringComparison.Ordinal));
            Assert.Contains(sides, s => s.Contains("reopened by Alice Ng", StringComparison.Ordinal));

            // ---- orphan: its quote, a neutral statement of fact, and never "addressed" ----
            var stranded = page.Locator("[data-annotation-id=\"" + orphan.Id + "\"]");
            Assert.Equal("orphaned", await stranded.GetAttributeAsync("data-charter-anchor-status"));
            Assert.True(await stranded.Locator(Ui("item-jump")).IsDisabledAsync());
            Assert.Contains(
                "the read path will be built after",
                await stranded.Locator(Ui("item-quote")).InnerTextAsync(),
                StringComparison.Ordinal);
            Assert.Equal("different", await stranded.GetAttributeAsync("data-charter-base-status"));
            Assert.Contains(
                "The plan has changed",
                await stranded.Locator(Ui("item-orphan-note")).InnerTextAsync(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "addressed",
                await stranded.InnerTextAsync(),
                StringComparison.OrdinalIgnoreCase);

            // ---- the same orphan, on a plan that has NOT changed: the strong claim is withheld (§4.3.1) ----
            var unmoved = page.Locator("[data-annotation-id=\"" + orphanOnAnUnchangedPlan.Id + "\"]");
            Assert.Equal("orphaned", await unmoved.GetAttributeAsync("data-charter-anchor-status"));
            Assert.Equal("current", await unmoved.GetAttributeAsync("data-charter-base-status"));
            var unmovedNote = await unmoved.Locator(Ui("item-orphan-note")).InnerTextAsync();
            Assert.DoesNotContain("The plan has changed", unmovedNote, StringComparison.Ordinal);
            Assert.Contains("is not in the plan", unmovedNote, StringComparison.Ordinal);

            // ---- resolving a teammate's comment appends a resolve record attributed to THIS author ----
            await fromBob.Locator(Ui("item-resolve")).ClickAsync();
            await WaitForEventAsync(page, "annotation-resolved");
            await page.WaitForSelectorAsync(
                "[data-annotation-id=\"" + bobs.Id + "\"][data-charter-status=\"resolved\"]");
            Assert.Contains("\"op\":\"resolve\"", await File.ReadAllTextAsync(alice.LogPath), StringComparison.Ordinal);

            Assert.True(instrumented.ConsoleErrors.Count == 0,
                "console errors present:\n  " + string.Join("\n  ", instrumented.ConsoleErrors));
            Assert.True(instrumented.PageErrors.Count == 0,
                "page errors present:\n  " + string.Join("\n  ", instrumented.PageErrors));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is harmless.
            }
        }
    }

    /// <summary>Wait until <paramref name="path"/> exists — bounded, never a fixed sleep.</summary>
    private static async Task WaitForFileAsync(string path, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("the review log was never written at " + path);
    }

    /// <summary>
    /// Wait for <paramref name="selector"/>, re-touching the given log files on a cadence so the test never
    /// depends on one filesystem event landing after the server's watcher was armed. Touching changes only the
    /// modification time — it fabricates no records — so what the panel ends up showing is exactly what the
    /// logs say.
    /// </summary>
    private static async Task WaitForSelectorWhileTouchingAsync(
        IPage page, string selector, IReadOnlyList<string> logPaths, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await page.Locator(selector).CountAsync() > 0)
            {
                return;
            }

            foreach (var log in logPaths.Where(File.Exists))
            {
                try
                {
                    File.SetLastWriteTimeUtc(log, DateTime.UtcNow);
                }
                catch (IOException)
                {
                    // A transient sharing conflict with the server's own read is harmless — retry next pass.
                }
            }

            await Task.Delay(200);
        }

        Assert.Fail("the panel never showed '" + selector + "' after the teammate's log arrived");
    }

    // ---- Charter #48 / #60 / #61: annotating a rendered :::diagram ---------------------------------------

    /// <summary>
    /// A plan whose diagram BRANCHES, so the rendered SVG has real empty background between and beside its
    /// nodes for a background click to land on, and whose prose sits ABOVE the diagram — the "unrelated text
    /// elsewhere on the page" that a stray double-click's native word-select can grab (Charter #61).
    /// </summary>
    private const string DiagramPlan =
        "# Diagram review\n\n" +
        "Preceding prose the reviewer never touched at all.\n\n" +
        ":::diagram\n" +
        "graph TD\n" +
        "A[Start] --> B[Middle]\n" +
        "A --> C[Other]\n" +
        "B --> D[End]\n" +
        ":::\n";

    /// <summary>
    /// Charter #48 + #60, together, because they are one problem: a <c>:::diagram</c> renders as
    /// <c>&lt;pre class="mermaid" id="&lt;stable charter id&gt;"&gt;</c> and Mermaid then replaces its content
    /// with an <c>&lt;svg&gt;</c> carrying ITS OWN generated ids — on the svg and on every <c>g.node</c>.
    ///
    /// <para>#48: a diagram-node note resolved its anchor by the generic "nearest ancestor with an id" walk, so
    /// it stopped at the Mermaid node id. <c>SourceMap.LineForAnchor</c> cannot map that, so the agent was
    /// handed no <c>sourceLine</c> at all, and the anchor orphaned on the next render. The fix keeps the node's
    /// identity in <c>nodeId</c> — which is what that field is for — and anchors to the BLOCK.</para>
    ///
    /// <para>#60: the diagram was the one block type with no whole-block annotation. Clicking its background
    /// either did nothing useful or (as the probe for this fix found) anchored to the SVG's Mermaid id with a
    /// context line read out of Mermaid's own inline <c>&lt;style&gt;</c>. A background click now produces the
    /// same plain <c>element</c> annotation every other block produces.</para>
    ///
    /// Both legs assert the POSTED payload, not the DOM: the C#-string golden tests are blind to all of this.
    /// </summary>
    [SkippableFact]
    public async Task Diagram_node_and_diagram_background_both_anchor_to_the_block_with_a_source_line()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-diagram-anchor-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, DiagramPlan);

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
            await page.WaitForSelectorAsync(".mermaid svg", new PageWaitForSelectorOptions { Timeout = 20_000 });

            // ---- the fixture's own premise: Mermaid really does stamp ids of its own ----
            // Without this the whole test could pass against the broken code by coincidence.
            var blockId = await page.EvaluateAsync<string>("() => document.querySelector('pre.mermaid').id");
            var svgId = await page.EvaluateAsync<string>("() => document.querySelector('pre.mermaid svg').id");
            var mermaidNodeId = await page.EvaluateAsync<string>(
                "() => document.querySelector('pre.mermaid g.node').id");
            Assert.False(string.IsNullOrEmpty(blockId), "the renderer must stamp a stable block id on pre.mermaid");
            Assert.False(string.IsNullOrEmpty(svgId), "fixture drift: Mermaid no longer stamps an id on its <svg>");
            Assert.False(
                string.IsNullOrEmpty(mermaidNodeId), "fixture drift: Mermaid no longer stamps an id on its nodes");
            Assert.NotEqual(blockId, svgId);
            Assert.NotEqual(blockId, mermaidNodeId);

            // ---- (c) a NODE: kind diagram-node, anchored to the block, node identity in nodeId ----
            await page.Locator("pre.mermaid g.node").First
                .ClickAsync(new LocatorClickOptions { Modifiers = new[] { KeyboardModifier.Alt } });
            await page.WaitForSelectorAsync(Ui("composer"));

            var nodeContext = await page.InnerTextAsync(Ui("composer-context"));
            Assert.Contains("diagram node", nodeContext, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("whole diagram", nodeContext, StringComparison.OrdinalIgnoreCase);

            await page.FillAsync(Ui("composer-input"), "this node needs an explicit failure path");
            await page.ClickAsync(Ui("composer-save"));
            await WaitForEventAsync(page, "submitted");

            // ---- (a) the BACKGROUND: the same shape any other block produces ----
            var background = await DiagramBackgroundPointAsync(page);
            await page.Keyboard.DownAsync("Alt");
            await page.Mouse.ClickAsync(background.X, background.Y);
            await page.Keyboard.UpAsync("Alt");
            await page.WaitForSelectorAsync(Ui("composer"));

            var wholeContext = await page.InnerTextAsync(Ui("composer-context"));
            Assert.Contains("whole diagram", wholeContext, StringComparison.OrdinalIgnoreCase);
            // Mermaid ships its theme CSS in a <style> INSIDE the svg, whose text nodes used to be read out
            // as the block's "visible" label — the composer's context line was literally a stylesheet.
            Assert.DoesNotContain("font-family", wholeContext, StringComparison.OrdinalIgnoreCase);

            await page.FillAsync(Ui("composer-input"), "this diagram should flow left to right");
            await page.ClickAsync(Ui("composer-save"));
            await WaitForEventAsync(page, "submitted");

            // ---- the posted payloads: both anchored to the BLOCK, both source-mappable ----
            var listed = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(2, listed.GetArrayLength());

            var node = FindByNote(listed, "this node needs an explicit failure path");
            Assert.Equal("diagram-node", node.GetProperty("kind").GetString());
            Assert.Equal(blockId, node.GetProperty("anchorId").GetString());
            Assert.Equal(mermaidNodeId, node.GetProperty("nodeId").GetString());
            AssertMapsToTheDiagramBlock(node);

            var whole = FindByNote(listed, "this diagram should flow left to right");
            Assert.Equal("element", whole.GetProperty("kind").GetString());
            Assert.Equal(blockId, whole.GetProperty("anchorId").GetString());
            Assert.Equal(JsonValueKind.Null, whole.GetProperty("nodeId").ValueKind);
            AssertMapsToTheDiagramBlock(whole);

            // Nothing reached the agent carrying a Mermaid-generated id in the field the source map reads.
            foreach (var annotation in listed.EnumerateArray())
            {
                Assert.NotEqual(svgId, annotation.GetProperty("anchorId").GetString());
                Assert.DoesNotContain(
                    "flowchart", annotation.GetProperty("anchorId").GetString()!, StringComparison.Ordinal);
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
    /// Charter #61: double-clicking a diagram's background must produce NOTHING. Chromium's native
    /// word-select fallback fires where there is no selectable prose and lands on whatever text is nearest —
    /// which the SDK's selection listener then took for an intentional text-range annotation, pointing the
    /// composer at text the reviewer never touched.
    ///
    /// The second half of the test is the anti-over-correction guard: a REAL press-drag-release across the
    /// prose paragraph must still produce a coherent text-range note (Charter #56's contract, unregressed).
    /// </summary>
    [SkippableFact]
    public async Task Double_clicking_the_diagram_background_annotates_nothing_but_prose_still_selects()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-diagram-dblclick-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, DiagramPlan);

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
            await page.WaitForSelectorAsync(".mermaid svg", new PageWaitForSelectorOptions { Timeout = 20_000 });

            var openedBefore = await CountEventsAsync(page, "composer-opened");
            var background = await DiagramBackgroundPointAsync(page);

            // Twice, and once more as a triple click: the reported behaviour was intermittent, and a
            // paragraph-select gesture is the same class of accident.
            await page.Mouse.DblClickAsync(background.X, background.Y);
            await page.Mouse.DblClickAsync(background.X, background.Y);
            await page.Mouse.ClickAsync(background.X, background.Y, new MouseClickOptions { ClickCount = 3 });

            // The composer is created synchronously inside the mouseup handler, so one round trip after the
            // gesture is enough to observe it — but hold the negative for a bounded window anyway, since an
            // accidental composer arriving late would be just as wrong as one arriving now.
            await AssertNoComposerForAsync(page, 1_500);
            Assert.Equal(openedBefore, await CountEventsAsync(page, "composer-opened"));
            Assert.Equal(0, (await ListAnnotationsAsync(server.Address, session.Key.Value)).GetArrayLength());

            // ---- the reported symptom itself, made deterministic ----
            // WHICH far-away word Chromium's fallback grabs depends on layout, so the STATE it produces is
            // reproduced directly instead: a real, non-empty selection in the heading, while the reviewer's
            // gesture ended on the diagram. It is a perfectly good selection in a perfectly good block, and
            // it is still not what the reviewer pointed at — so it must annotate nothing.
            await page.EvaluateAsync(
                "() => {" +
                "  const range = document.createRange();" +
                "  range.selectNodeContents(document.querySelector('h1'));" +
                "  const sel = window.getSelection();" +
                "  sel.removeAllRanges(); sel.addRange(range);" +
                "}");

            // Asserted BEFORE the gesture: opening a composer focuses its textarea, which collapses the
            // selection — so reading this afterwards would report "empty" in exactly the failing case.
            Assert.False(
                await page.EvaluateAsync<bool>("() => window.getSelection().isCollapsed"),
                "fixture drift: this leg proves nothing unless the stray selection is genuinely non-empty");

            await page.EvaluateAsync(
                "() => document.querySelector('pre.mermaid svg')" +
                "  .dispatchEvent(new MouseEvent('mouseup', { bubbles: true }))");

            await AssertNoComposerForAsync(page, 500);
            Assert.Equal(openedBefore, await CountEventsAsync(page, "composer-opened"));
            Assert.Equal(0, (await ListAnnotationsAsync(server.Address, session.Key.Value)).GetArrayLength());

            // ---- and the legitimate gesture is untouched: a real drag across prose still annotates ----
            var line = await FirstLineRectAsync(page, "body > p");
            await page.Mouse.MoveAsync(line.X + 4, line.Y + (line.Height / 2));
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(
                line.X + (line.Width * 0.7f), line.Y + (line.Height / 2), new MouseMoveOptions { Steps = 10 });
            await page.Mouse.UpAsync();

            await page.WaitForSelectorAsync(Ui("composer-input"));
            await page.FillAsync(Ui("composer-input"), "say which prose the reviewer meant");
            await page.ClickAsync(Ui("composer-save"));
            await WaitForEventAsync(page, "submitted");

            var listed = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(1, listed.GetArrayLength());
            var prose = listed[0];
            Assert.Equal("text-range", prose.GetProperty("kind").GetString());
            Assert.Equal(
                await page.EvaluateAsync<string>("() => document.querySelector('body > p').id"),
                prose.GetProperty("anchorId").GetString());
            Assert.False(
                string.IsNullOrWhiteSpace(prose.GetProperty("quote").GetString()),
                "a real prose drag must still carry the selected text");
            Assert.True(
                prose.GetProperty("end").GetInt32() > prose.GetProperty("start").GetInt32(),
                "a real prose drag must still carry an ordered span (Charter #56)");

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
    /// The drained annotation resolves to the <c>:::diagram</c>'s own source line — the whole point of
    /// anchoring to the block rather than to a Mermaid id. Asserted against the plan text itself, so it proves
    /// the agent is pointed at the right markdown, not merely that some number arrived.
    /// </summary>
    private static void AssertMapsToTheDiagramBlock(JsonElement annotation)
    {
        var line = annotation.GetProperty("sourceLine");
        Assert.True(
            line.ValueKind == JsonValueKind.Number,
            "Charter #48: the annotation reached the agent with no sourceLine, so it cannot tell which "
                + "markdown line to edit (anchorId was '" + annotation.GetProperty("anchorId").GetString() + "')");
        Assert.Equal("resolved", annotation.GetProperty("anchorStatus").GetString());

        var lines = DiagramPlan.Split('\n');
        var text = lines[line.GetInt32() - 1];
        Assert.StartsWith(":::diagram", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A viewport point on the rendered diagram's BACKGROUND — inside the block, inside the <c>&lt;svg&gt;</c>,
    /// and not on any Mermaid node. Scanned from the live layout rather than assumed, and it FAILS when the
    /// fixture leaves no such point, which would make every background assertion vacuous.
    /// </summary>
    private static async Task<(float X, float Y)> DiagramBackgroundPointAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(
            "() => {" +
            "  const block = document.querySelector('pre.mermaid');" +
            "  const svg = block && block.querySelector('svg');" +
            "  if (!svg) return 'null';" +
            "  const r = svg.getBoundingClientRect();" +
            "  for (let fy = 0.06; fy < 1; fy += 0.04) {" +
            "    for (let fx = 0.02; fx < 1; fx += 0.02) {" +
            "      const x = r.left + (r.width * fx), y = r.top + (r.height * fy);" +
            "      const el = document.elementFromPoint(x, y);" +
            "      if (!el || !block.contains(el)) continue;" +
            "      if (el.closest && el.closest('.node, g.node, [data-node-id], [data-charter-ui]')) continue;" +
            "      return JSON.stringify({ x: x, y: y });" +
            "    }" +
            "  }" +
            "  return 'null';" +
            "}");

        Assert.NotEqual("null", json);
        using var doc = JsonDocument.Parse(json!);
        return (
            (float)doc.RootElement.GetProperty("x").GetDouble(),
            (float)doc.RootElement.GetProperty("y").GetDouble());
    }

    /// <summary>The first RENDERED LINE BOX of <paramref name="selector"/>'s contents — a real drag target.</summary>
    private static async Task<(float X, float Y, float Width, float Height)> FirstLineRectAsync(
        IPage page, string selector)
    {
        var json = await page.EvaluateAsync<string>(
            "s => {" +
            "  const range = document.createRange();" +
            "  range.selectNodeContents(document.querySelector(s));" +
            "  const r = Array.from(range.getClientRects()).filter(b => b.width > 0 && b.height > 0)[0];" +
            "  return r ? JSON.stringify({ x: r.left, y: r.top, w: r.width, h: r.height }) : 'null';" +
            "}",
            selector);

        Assert.NotEqual("null", json);
        using var doc = JsonDocument.Parse(json!);
        return (
            (float)doc.RootElement.GetProperty("x").GetDouble(),
            (float)doc.RootElement.GetProperty("y").GetDouble(),
            (float)doc.RootElement.GetProperty("w").GetDouble(),
            (float)doc.RootElement.GetProperty("h").GetDouble());
    }

    /// <summary>
    /// No composer appears for <paramref name="windowMs"/>. Asserting a NEGATIVE needs a settle window, and
    /// this is a bounded poll over the selector engine — never <c>WaitForFunctionAsync</c>, whose in-page
    /// <c>eval</c> the served CSP refuses.
    /// </summary>
    private static async Task AssertNoComposerForAsync(IPage page, int windowMs)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(windowMs);
        while (DateTime.UtcNow < deadline)
        {
            Assert.Equal(0, await page.Locator(Ui("composer")).CountAsync());
            await Task.Delay(100);
        }
    }


    // ---- #109: the free-text escape hatch on a select --------------------------------------------------

    /// <summary>
    /// The agent authoring a select's options is the party LEAST qualified to know they are exhaustive — it is
    /// asking precisely because it does not know the answer. Without an escape hatch a reviewer who disagrees
    /// with the framing must pick a wrong option or abandon the form, and either way the real decision is lost.
    /// <para>
    /// The renderer therefore appends a "Something else" control to every single/multi form, and the typed text
    /// — not the control's own empty value — becomes the answer. An Other that is checked but EMPTY must yield
    /// nothing: "an empty string is not an answer" is what keeps the Save button honest, and a blank Other
    /// would read as resolved while saying less than leaving the question open.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Something_else_lets_a_reviewer_answer_outside_the_offered_options()
    {
        // #111 — same WebKit defect as free-text: an answer whose value comes from a TEXT FIELD never
        // reaches the server. Value collection is fine on WebKit (Save correctly enables once text is typed);
        // it is the submit that is lost. Quarantined so the WebKit leg stays blocking for everything else.
        Skip.If(BrowserEngine.Name == "webkit", "Known WebKit defect - see issue #111.");

        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-other-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, ClearableQuestionPlan);

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

            var other = Control("q-open", "input[data-answer-other]");
            var otherText = Control("q-open", "input[data-answer-other-text]");

            // The hatch exists on a select the agent never authored one into.
            await page.WaitForSelectorAsync(other);
            await AssertSaveDisabledAsync(page, "q-open");

            // Checked but empty is NOT an answer — Save must stay disabled.
            await page.CheckAsync(other);
            await AssertSaveDisabledAsync(page, "q-open");

            // Type the answer the agent did not think of.
            await page.FillAsync(otherText, "Commits, but only on the default branch");
            Assert.True(
                await page.IsEnabledAsync(SaveButton("q-open")),
                "typing into Something else must make the answer saveable");

            await SaveAsync(page, "q-open");

            // The TYPED TEXT reaches the server, not the control's empty value — and it is deliberately a
            // value that matches no declared option, which the schema already tolerates.
            Assert.Equal(
                new[] { "Commits, but only on the default branch" },
                await WaitForAnswerValuesAsync(server, session, "q-open"));
        }
        finally
        {
            if (File.Exists(planPath))
            {
                File.Delete(planPath);
            }
        }
    }

    // ---- Charter #63: a reviewer can clear an accidental radio answer -----------------------------------

    /// <summary>
    /// One open single-select, one open bool (also radios), and one ALREADY-ANSWERED single-select — the three
    /// states the deselect rule has to be coherent across.
    /// </summary>
    private const string ClearableQuestionPlan =
        "# Clearing an answer\n\n" +
        ":::question\n" +
        "{\"id\":\"q-open\",\"title\":\"Pick one colour\",\"mode\":\"single\",\"target\":\"human\"," +
        "\"options\":[\"Red\",\"Green\",\"Blue\"]}\n" +
        ":::\n\n" +
        ":::question\n" +
        "{\"id\":\"q-bool\",\"title\":\"Ship it?\",\"mode\":\"bool\",\"target\":\"human\"}\n" +
        ":::\n\n" +
        ":::question\n" +
        "{\"id\":\"q-settled\",\"title\":\"Which store?\",\"mode\":\"single\",\"target\":\"human\"," +
        "\"options\":[\"Postgres\",\"DynamoDB\"],\"answer\":[\"Postgres\"]}\n" +
        ":::\n";

    /// <summary>
    /// Charter #63. A native radio cannot be deselected, so one mis-click leaves a decision the reviewer never
    /// made with no way back — and for a <c>:::question</c> "unanswered" is a real state, not the absence of
    /// one (<c>charter-format</c>: a question with no non-empty <c>answer</c> IS open).
    ///
    /// <para>The chosen semantics, asserted here in both states. On an OPEN question a deselect returns the
    /// form to nothing-selected, so there is nothing to save and Save goes back to disabled. On an ALREADY
    /// ANSWERED one a deselect DIFFERS from the recorded answer, so Save enables, says <c>Clear answer</c>
    /// rather than <c>Save answer</c>, and posts <c>values: []</c> — which clears the recorded answer and
    /// returns the question to open. A reviewer who may freely change a settled decision must be able to
    /// withdraw it too, and a form showing nothing selected while the server still held an answer would be a
    /// lying UI.</para>
    /// </summary>
    [SkippableFact]
    public async Task Clicking_the_selected_radio_clears_it_and_an_answered_question_can_be_returned_to_open()
    {
        // #111 — known WebKit defects, quarantined so the WebKit leg stays BLOCKING for everything else:
        // a new engine regression must fail CI immediately rather than hide behind these two.
        Skip.If(BrowserEngine.Name == "webkit", "Known WebKit defect - see issue #111.");

        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-clear-answer-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, ClearableQuestionPlan);

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

            // ---- an OPEN question: the accidental click, then the way back ----
            var red = Control("q-open", "input[type=radio][value=\"Red\"]");
            await AssertSaveDisabledAsync(page, "q-open");
            await page.ClickAsync(red);
            Assert.True(await page.IsCheckedAsync(red));
            Assert.True(
                await page.IsEnabledAsync(SaveButton("q-open")),
                "picking an option must enable Save (the baseline behaviour this fix must not disturb)");

            await page.ClickAsync(red);
            await WaitForEventAsync(page, "answer-cleared");
            Assert.False(
                await page.IsCheckedAsync(red),
                "Charter #63: clicking the already-selected option must clear it");
            await AssertSaveDisabledAsync(page, "q-open");
            Assert.Equal("Save answer", (await page.InnerTextAsync(SaveButton("q-open"))).Trim());

            // Nothing was ever submitted — an accidental click that is taken back leaves no trace.
            Assert.Equal(0, (await ListAnswersAsync(server.Address, session.Key.Value)).GetArrayLength());

            // ---- the keyboard path: arrow keys MOVE the selection, Space clears it ----
            // Arrow-key navigation fires a `click` on the newly selected radio just as a mouse does, so a
            // deselect rule that trusted a stale "was it checked?" sample would clear the option the reviewer
            // just moved onto — turning the keyboard into a way to answer nothing at all.
            var green = Control("q-open", "input[type=radio][value=\"Green\"]");
            var blue = Control("q-open", "input[type=radio][value=\"Blue\"]");
            await page.ClickAsync(green);
            Assert.True(await page.IsCheckedAsync(green));

            await page.Keyboard.PressAsync("ArrowRight");
            Assert.True(
                await page.IsCheckedAsync(blue),
                "ArrowRight must MOVE the selection to the next option, never clear it");
            Assert.False(await page.IsCheckedAsync(green));
            Assert.True(await page.IsEnabledAsync(SaveButton("q-open")));

            await page.Keyboard.PressAsync("Space");
            Assert.False(
                await page.IsCheckedAsync(blue),
                "Space on the already-selected option must clear it, like a click on it");
            await AssertSaveDisabledAsync(page, "q-open");

            // ---- a bool renders as radios too (Charter #43), so it clears the same way ----
            var yes = Control("q-bool", "input[type=radio][value=\"true\"]");
            await page.ClickAsync(yes);
            Assert.True(await page.IsCheckedAsync(yes));
            await page.ClickAsync(yes);
            Assert.False(await page.IsCheckedAsync(yes), "a bool's radios must deselect like any other");
            await AssertSaveDisabledAsync(page, "q-bool");

            // ---- an ANSWERED question: deselect is a real, submittable retraction ----
            var postgres = Control("q-settled", "input[type=radio][value=\"Postgres\"]");
            Assert.True(await page.IsCheckedAsync(postgres), "the recorded answer must start pre-selected");
            await AssertSaveDisabledAsync(page, "q-settled");

            await page.ClickAsync(postgres);
            Assert.False(await page.IsCheckedAsync(postgres));
            Assert.True(
                await page.IsEnabledAsync(SaveButton("q-settled")),
                "clearing a RECORDED answer differs from what the markup holds, so it must be submittable");

            // The control says what it will do before it is pressed — the UI must not be coy about a retraction.
            Assert.Equal("Clear answer", (await page.InnerTextAsync(SaveButton("q-settled"))).Trim());
            Assert.Contains(
                "unanswered",
                await page.GetAttributeAsync(SaveButton("q-settled"), "title") ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);

            await page.ClickAsync(SaveButton("q-settled"));
            Assert.Empty(await WaitForAnswerValuesAsync(server, session, "q-settled"));
            Assert.Equal(
                "single", (await WaitForAnswerAsync(server, session, "q-settled")).GetProperty("mode").GetString());

            // Having landed, the cleared state IS the recorded state, so there is nothing left to submit.
            await AssertSaveDisabledAsync(page, "q-settled");
            Assert.Equal("Save answer", (await page.InnerTextAsync(SaveButton("q-settled"))).Trim());

            // A 400 on the empty-values post would surface here (the server must accept a cleared answer).
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

    // ---- Charter #56: a text range's start/end must be in ONE reference frame ----------------------------

    /// <summary>
    /// One paragraph with a bold run in the middle, so a selection can cross THREE text nodes, and long enough
    /// to wrap onto several rendered lines at a narrow viewport. The concatenated text of the block is exactly
    /// the sentence below with the <c>**</c> markers removed — which is the frame the offsets are measured in.
    /// </summary>
    private const string TextRangePlan =
        "# Text range offsets\n\n" +
        "The opening clause is plain prose, then **the middle clause is bold**, and then a closing clause " +
        "runs on for long enough that this paragraph wraps onto more than one rendered line at a narrow " +
        "viewport width.\n";

    /// <summary>
    /// Charter #56: the SDK recorded <c>start: selection.anchorOffset</c> and <c>end: selection.focusOffset</c>
    /// — offsets within their OWN text nodes. Across a multi-node selection they are not comparable at all (the
    /// focus node's offset 0 is the start of the LAST node), and a real multi-line selection drained as
    /// <c>"start": 146, "end": 0</c> over a ~150-character quote: <c>end</c> before <c>start</c>.
    ///
    /// Both legs assert the same contract — the offsets index the ANCHORED BLOCK's text content, so
    /// <c>end &gt; start</c> and <c>blockText.slice(start, end)</c> is the quote. The multi-node leg is the one
    /// that fails against the old code; the single-node leg pins that the fix did not shift the easy case.
    /// </summary>
    [SkippableFact]
    public async Task Text_range_offsets_index_the_blocks_own_text_for_single_and_multi_node_selections()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-text-range-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, TextRangePlan);

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

            var blockText = await BlockTextAsync(page);

            // A span wholly inside the FIRST text node...
            const string singleNode = "opening clause is plain";
            var singleAt = blockText.IndexOf(singleNode, StringComparison.Ordinal);
            Assert.True(singleAt >= 0, "fixture drift: the single-node span is not in the block's text");
            await SelectAndAnnotateAsync(page, singleAt, singleAt + singleNode.Length, "single-node note");

            // ...and a span crossing the <strong>, so THREE text nodes contribute. Its first and last
            // characters live in different nodes, which is precisely what the old anchorOffset/focusOffset
            // pair could not express.
            const string multiNode = "prose, then the middle clause is bold, and then a closing";
            var multiAt = blockText.IndexOf(multiNode, StringComparison.Ordinal);
            Assert.True(multiAt >= 0, "fixture drift: the multi-node span is not in the block's text");

            // Pin that the block really does contribute three text nodes and that this span straddles them —
            // otherwise the "multi-node" leg could quietly degrade into a second single-node one.
            var nodeCount = await page.EvaluateAsync<int>(
                "() => { let n = 0;" +
                "  (function walk(x) {" +
                "    if (x.nodeType === 1) {" +
                "      if (x.hasAttribute && x.hasAttribute('data-charter-ui')) return;" +
                "      for (const c of x.childNodes) walk(c);" +
                "      return;" +
                "    }" +
                "    if (x.nodeType === 3) n++;" +
                "  })(document.querySelector('body > p'));" +
                "  return n; }");
            Assert.Equal(3, nodeCount);
            Assert.True(
                multiAt < blockText.IndexOf("the middle clause is bold", StringComparison.Ordinal)
                    && multiAt + multiNode.Length > blockText.IndexOf("bold,", StringComparison.Ordinal),
                "fixture drift: the multi-node span must start before the <strong> and end after it");

            await SelectAndAnnotateAsync(page, multiAt, multiAt + multiNode.Length, "multi-node note");

            var listed = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(2, listed.GetArrayLength());

            AssertCoherentSpan(FindByNote(listed, "single-node note"), blockText, singleAt, singleNode);
            AssertCoherentSpan(FindByNote(listed, "multi-node note"), blockText, multiAt, multiNode);

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
    /// The issue's own scenario, driven by a REAL mouse: a human drag across more than one rendered line,
    /// through the real composer, must drain a coherent pair. The reported failure was produced by exactly this
    /// gesture and by no scripted one, so this leg exists to make the human path itself the guard.
    /// </summary>
    [SkippableFact]
    public async Task Real_multi_line_mouse_selection_drains_a_coherent_text_range()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-text-range-drag-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, TextRangePlan);

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

            // Narrow enough that the paragraph is guaranteed to wrap, so the drag really does cross lines.
            await page.SetViewportSizeAsync(520, 800);
            await page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(page, "ready");

            var blockText = await BlockTextAsync(page);

            // One client rect per LINE BOX. It has to come from a RANGE over the paragraph's contents: a
            // block-level element's own getClientRects() is a single border box, which says nothing about wrap.
            var lines = await page.EvaluateAsync<JsonElement>(
                "() => {" +
                "  const range = document.createRange();" +
                "  range.selectNodeContents(document.querySelector('body > p'));" +
                "  return Array.from(range.getClientRects())" +
                "    .filter(r => r.width > 0 && r.height > 0)" +
                "    .map(r => ({ x: r.left, y: r.top, w: r.width, h: r.height }));" +
                "}");
            Assert.True(
                lines.GetArrayLength() >= 2,
                "the fixture paragraph must wrap for this to be a multi-LINE selection; got "
                    + lines.GetArrayLength() + " line box(es)");

            var firstLine = lines[0];
            var secondLine = lines[1];

            // A genuine press-drag-release from partway along line 1 to partway along line 2.
            await page.Mouse.MoveAsync(
                (float)(firstLine.GetProperty("x").GetDouble() + 4),
                (float)(firstLine.GetProperty("y").GetDouble() + (firstLine.GetProperty("h").GetDouble() / 2)));
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(
                (float)(secondLine.GetProperty("x").GetDouble()
                    + (secondLine.GetProperty("w").GetDouble() * 0.6)),
                (float)(secondLine.GetProperty("y").GetDouble() + (secondLine.GetProperty("h").GetDouble() / 2)),
                new MouseMoveOptions { Steps = 12 });
            await page.Mouse.UpAsync();

            // The composer opening at all proves the drag produced a text-range anchor.
            await page.WaitForSelectorAsync(Ui("composer-input"));
            await page.FillAsync(Ui("composer-input"), "put soft line feeds here");
            await page.ClickAsync(Ui("composer-save"));
            await WaitForEventAsync(page, "submitted");

            var listed = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(1, listed.GetArrayLength());
            var drained = listed[0];
            Assert.Equal("text-range", drained.GetProperty("kind").GetString());

            // The exact pair the issue reported as meaningless. No expected VALUES here — a real drag lands
            // where it lands — only that the pair is a real span of the block, in one frame, matching the quote.
            var start = drained.GetProperty("start").GetInt32();
            var end = drained.GetProperty("end").GetInt32();
            Assert.True(end > start, $"end must follow start for a non-empty selection; got start={start}, end={end}");
            Assert.InRange(start, 0, blockText.Length);
            Assert.InRange(end, 0, blockText.Length);
            Assert.Equal(
                Normalize(drained.GetProperty("quote").GetString()),
                Normalize(blockText[start..end]));

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
    /// The annotated block's text content with every <c>[data-charter-ui]</c> subtree skipped — the SDK's own
    /// frame, computed here independently so the assertions do not simply echo the code under test.
    /// </summary>
    private static Task<string> BlockTextAsync(IPage page)
        => page.EvaluateAsync<string>(
            "() => {" +
            "  const out = [];" +
            "  (function walk(n) {" +
            "    if (n.nodeType === 1) {" +
            "      if (n.hasAttribute && n.hasAttribute('data-charter-ui')) return;" +
            "      for (const c of n.childNodes) walk(c);" +
            "      return;" +
            "    }" +
            "    if (n.nodeType === 3) out.push(n.nodeValue || '');" +
            "  })(document.querySelector('body > p'));" +
            "  return out.join('');" +
            "}");

    /// <summary>
    /// Select <c>[from, to)</c> of the block's text — placing each boundary in whichever text node actually
    /// holds it, which is what makes the multi-node leg genuinely multi-node — then release the mouse and save
    /// a note through the real composer.
    /// </summary>
    private static async Task SelectAndAnnotateAsync(IPage page, int from, int to, string note)
    {
        await page.EvaluateAsync(
            "([from, to]) => {" +
            "  const nodes = [];" +
            "  (function walk(n) {" +
            "    if (n.nodeType === 1) {" +
            "      if (n.hasAttribute && n.hasAttribute('data-charter-ui')) return;" +
            "      for (const c of n.childNodes) walk(c);" +
            "      return;" +
            "    }" +
            "    if (n.nodeType === 3) nodes.push(n);" +
            "  })(document.querySelector('body > p'));" +
            "  const range = document.createRange();" +
            "  let pos = 0, started = false;" +
            "  for (const n of nodes) {" +
            "    const len = (n.nodeValue || '').length;" +
            "    if (!started && from < pos + len) { range.setStart(n, from - pos); started = true; }" +
            "    if (started && to <= pos + len) { range.setEnd(n, to - pos); break; }" +
            "    pos += len;" +
            "  }" +
            "  const sel = window.getSelection();" +
            "  sel.removeAllRanges(); sel.addRange(range);" +
            "  document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));" +
            "}",
            new[] { from, to });

        await page.WaitForSelectorAsync(Ui("composer-input"));
        await page.FillAsync(Ui("composer-input"), note);
        await page.ClickAsync(Ui("composer-save"));
        await WaitForEventAsync(page, "submitted");
    }

    /// <summary>
    /// The contract, stated once: the pair is a real, ordered span of the block's own text, it is exactly where
    /// the selection was made, and slicing the block with it reproduces the quote.
    /// </summary>
    private static void AssertCoherentSpan(JsonElement annotation, string blockText, int at, string expected)
    {
        var start = annotation.GetProperty("start").GetInt32();
        var end = annotation.GetProperty("end").GetInt32();

        Assert.True(end > start, $"end must follow start for a non-empty selection; got start={start}, end={end}");
        Assert.Equal(at, start);
        Assert.Equal(at + expected.Length, end);
        Assert.Equal(expected, blockText[start..end]);
        Assert.Equal(Normalize(expected), Normalize(annotation.GetProperty("quote").GetString()));
    }

    private static JsonElement FindByNote(JsonElement annotations, string note)
    {
        foreach (var annotation in annotations.EnumerateArray())
        {
            if (string.Equals(annotation.GetProperty("note").GetString(), note, StringComparison.Ordinal))
            {
                return annotation;
            }
        }

        Assert.Fail("no drained annotation carried the note '" + note + "'");
        return default;
    }

    // The browser's Selection.toString() renders whitespace as the page shows it, while the block's text
    // content carries the source's own runs. Comparing them collapsed is the honest comparison of the two.
    private static string Normalize(string? text)
        => System.Text.RegularExpressions.Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();

    // ---- Charter #51: pan/zoom for an oversized :::diagram ----------------------------------------------

    /// <summary>
    /// Two diagrams and two paragraphs. The first diagram is a left-to-right chain of six long-labelled
    /// nodes, so its INTRINSIC width comfortably exceeds any review column — and because Mermaid renders
    /// with <c>useMaxWidth</c> it does not overflow, it SHRINKS, which is the actual defect: the labels
    /// become unreadable and no scrollbar ever appears to say so. The second diagram fits and must gain
    /// nothing at all. The paragraph below the first diagram is the layout the zoom visibly moves.
    /// </summary>
    private const string PanZoomDiagramPlan =
        "# Diagram pan and zoom\n\n" +
        "Prose above the diagram, at the content column's full width.\n\n" +
        ":::diagram\n" +
        "graph LR\n" +
        "IngressGateway[Public ingress gateway terminating TLS] --> AuthService[Authentication and authorization service]\n" +
        "AuthService --> SessionStore[Session store backed by a Redis cluster]\n" +
        "SessionStore --> PlanRenderer[Charter plan renderer and source map builder]\n" +
        "PlanRenderer --> ReviewServer[Loopback review server and annotation API]\n" +
        "ReviewServer --> HandoffWriter[Guardrails handoff writer and flattener]\n" +
        ":::\n\n" +
        "Prose below the diagram, which moves down when the diagram grows.\n\n" +
        ":::diagram\n" +
        "graph TD\n" +
        "S[Small] --> T[Tiny]\n" +
        ":::\n";

    /// <summary>The oversized diagram, and the one that fits and must stay untouched.</summary>
    private const int Oversized = 0;

    private const int Fitting = 1;

    /// <summary>
    /// Charter #51, the shape of it: an oversized diagram gains the pan/zoom affordance, a diagram that
    /// fits gains NOTHING, and the exported artifact gains neither — pan/zoom is a review-time affordance
    /// and the saved file must still render the diagram statically with no SDK (invariant 1).
    ///
    /// <para>The fixture's own premise is asserted first, because every other assertion here is vacuous
    /// without it: the wide diagram really is being drawn smaller than Mermaid laid it out, and the small
    /// one really is not.</para>
    /// </summary>
    [SkippableFact]
    public async Task Oversized_diagram_gains_zoom_chrome_a_fitting_one_gains_none_and_the_export_gains_neither()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-diagram-zoom-" + Guid.NewGuid().ToString("N") + ".charter.md");
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

            await page.SetViewportSizeAsync(1000, 800);
            await page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(page, "ready");
            await WaitForDiagramsAsync(page, 2);
            await WaitForEventAsync(page, "diagram-zoomable");

            // ---- the premise ----
            var big = await DiagramProbeAsync(page, Oversized);
            var small = await DiagramProbeAsync(page, Fitting);
            Assert.True(
                big.GetProperty("intrinsicWidth").GetDouble()
                    > big.GetProperty("renderedWidth").GetDouble() + 8,
                "fixture drift: the wide diagram is not being shown smaller than it was drawn: " + big);
            Assert.True(
                small.GetProperty("intrinsicWidth").GetDouble()
                    <= small.GetProperty("renderedWidth").GetDouble() + 8,
                "fixture drift: the 'fitting' diagram does not fit: " + small);

            // ---- the oversized one is discoverable and keyboard-reachable ----
            Assert.Equal(1, await page.Locator(Ui("diagram-zoom")).CountAsync());
            Assert.True(big.GetProperty("hasBar").GetBoolean(), "the oversized diagram must carry the zoom bar");
            Assert.Contains("charter-zoomable", big.GetProperty("classes").GetString()!, StringComparison.Ordinal);
            Assert.Equal(0, big.GetProperty("tabIndex").GetInt32());
            Assert.Equal("group", big.GetProperty("role").GetString());
            Assert.False(
                string.IsNullOrEmpty(big.GetProperty("ariaLabel").GetString()),
                "a zoomable diagram needs an accessible name or its tab stop announces as nothing");
            Assert.Equal("100%", await page.InnerTextAsync(Ui("diagram-zoom-level")));

            // The SDK reaches the browser as an EMBEDDED RESOURCE, so every glyph in it makes an encoding
            // hop the source file never sees. Asserted on the characters that actually ARRIVED: a mojibake
            // zoom-out button is a defect no string test upstream of the resource pipeline can see.
            Assert.Equal("−", await page.InnerTextAsync(Ui("diagram-zoom-out")));
            Assert.Equal("+", await page.InnerTextAsync(Ui("diagram-zoom-in")));
            Assert.Contains("—", big.GetProperty("ariaLabel").GetString()!, StringComparison.Ordinal);

            // ---- and the one that fits gains no chrome and no behaviour change ----
            // Held over a window: the views are created from a MutationObserver, so chrome arriving LATE
            // would be just as wrong as chrome arriving now.
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(750);
            while (DateTime.UtcNow < deadline)
            {
                small = await DiagramProbeAsync(page, Fitting);
                Assert.False(small.GetProperty("hasBar").GetBoolean(),
                    "a diagram that fits must gain no zoom chrome: " + small);
                Assert.DoesNotContain(
                    "charter-zoomable", small.GetProperty("classes").GetString()!, StringComparison.Ordinal);
                Assert.Equal(-1, small.GetProperty("tabIndex").GetInt32());
                Assert.Equal(string.Empty, small.GetProperty("role").GetString());
                Assert.Equal(string.Empty, small.GetProperty("svgInlineWidth").GetString());
                await Task.Delay(150);
            }

            // ---- the exported artifact: the diagram renders, and none of this exists ----
            var exportPath = Path.ChangeExtension(planPath, ".export.html");
            await File.WriteAllTextAsync(
                exportPath, ArtifactExporter.Export(PanZoomDiagramPlan, Path.GetDirectoryName(planPath)!));
            try
            {
                await page.GotoAsync(
                    new Uri(exportPath).AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
                await WaitForDiagramsAsync(page, 2);

                Assert.False(
                    await page.EvaluateAsync<bool>("() => typeof window.CharterAnnotate !== 'undefined'"),
                    "the exported artifact must ship WITHOUT the annotation SDK");
                Assert.Equal(0, await page.Locator("[data-charter-ui]").CountAsync());
                Assert.Equal(0, await page.Locator(".charter-zoomable").CountAsync());

                var offline = await DiagramProbeAsync(page, Oversized);
                Assert.False(offline.GetProperty("hasBar").GetBoolean());
                Assert.Equal(-1, offline.GetProperty("tabIndex").GetInt32());
                Assert.Equal(string.Empty, offline.GetProperty("svgInlineWidth").GetString());
                Assert.Equal(string.Empty, offline.GetProperty("blockInlineOverflow").GetString());
                Assert.True(
                    offline.GetProperty("renderedWidth").GetDouble() > 0,
                    "the exported artifact must still render the diagram: " + offline);
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
    /// The capability itself: an oversized diagram ZOOMS (and gets bigger, not blurrier — the &lt;svg&gt;
    /// is widened, never transformed) and PANS, by every gesture the affordance offers, and Reset returns
    /// the block to exactly the markup the renderer emitted.
    ///
    /// <para>The load-bearing negative is that panning is not annotating. A drag is a drag whether or not
    /// Alt is held, so BOTH are exercised: neither may open a composer, and neither may reach the server's
    /// pre-drain queue. A plain wheel must also never be stolen — hijacking page scroll is hostile, so
    /// only Ctrl+wheel zooms.</para>
    /// </summary>
    [SkippableFact]
    public async Task Oversized_diagram_zooms_and_pans_and_a_drag_never_becomes_an_annotation()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-diagram-panzoom-" + Guid.NewGuid().ToString("N") + ".charter.md");
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

            await page.SetViewportSizeAsync(1000, 800);
            await page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(page, "ready");
            await WaitForDiagramsAsync(page, 2);
            await WaitForEventAsync(page, "diagram-zoomable");

            var fit = await DiagramProbeAsync(page, Oversized);
            var fitWidth = fit.GetProperty("renderedWidth").GetDouble();

            // ---- at FIT a drag is not a pan gesture AT ALL ----
            // Not merely "it does not move anything": the gesture must not ENGAGE, because an engaged pan
            // swallows the click that ends it, and a diagram showing everything it has is one a reviewer is
            // most likely to be clicking rather than navigating.
            await DragOverDiagramAsync(page, Oversized, -140, 0);
            Assert.Equal(0, (await DiagramProbeAsync(page, Oversized)).GetProperty("scrollLeft").GetDouble());
            Assert.Equal(0, await CountEventsAsync(page, "diagram-panned"));

            // And the reviewer's next Alt+click still annotates.
            var atFitBackground = await VisibleDiagramBackgroundPointAsync(page, Oversized);
            await page.Keyboard.DownAsync("Alt");
            await page.Mouse.ClickAsync(atFitBackground.X, atFitBackground.Y);
            await page.Keyboard.UpAsync("Alt");
            await page.WaitForSelectorAsync(Ui("composer"));
            await page.ClickAsync(Ui("composer-cancel"));

            // ---- zoom in with the explicit controls ----
            await page.ClickAsync(Ui("diagram-zoom-in"));
            await page.ClickAsync(Ui("diagram-zoom-in"));
            Assert.Equal("156%", await page.InnerTextAsync(Ui("diagram-zoom-level")));

            var zoomed = await DiagramProbeAsync(page, Oversized);
            Assert.True(
                zoomed.GetProperty("renderedWidth").GetDouble() > fitWidth * 1.5,
                "zooming must make the diagram BIGGER, not merely change a label: " + zoomed);
            // Widened, never transformed: a CSS transform would rasterize the label text this exists to
            // make readable, and would move every rect the annotation overlay is painted from.
            Assert.Equal("none", zoomed.GetProperty("svgTransform").GetString());
            Assert.True(
                zoomed.GetProperty("scrollWidth").GetDouble() > zoomed.GetProperty("clientWidth").GetDouble(),
                "a zoomed diagram must become a scroll region: " + zoomed);

            // ---- a plain wheel is NOT a zoom (page scroll stays the page's) ----
            await WheelOverDiagramAsync(page, Oversized, -240, control: false);
            Assert.Equal("156%", await page.InnerTextAsync(Ui("diagram-zoom-level")));

            // ---- Ctrl+wheel IS ----
            await WheelOverDiagramAsync(page, Oversized, -240, control: true);
            Assert.NotEqual("156%", await page.InnerTextAsync(Ui("diagram-zoom-level")));

            // ---- and now a real drag PANS ----
            await ScrollDiagramAsync(page, Oversized, 0, 0);
            await DragOverDiagramAsync(page, Oversized, -160, 0);
            var panned = await DiagramProbeAsync(page, Oversized);
            Assert.True(
                panned.GetProperty("scrollLeft").GetDouble() > 0,
                "a primary-button drag over a zoomed diagram did not pan it: " + panned);

            // The zoom bar is absolutely positioned INSIDE the scroll container, so it would ride away with
            // the content if it were not pushed back by the scroll offset.
            var barLeft = await page.EvaluateAsync<double>(
                "() => { const b = document.querySelector('[data-charter-ui=\"diagram-zoom\"]');" +
                "  const p = b.closest('pre.mermaid');" +
                "  return b.getBoundingClientRect().left - p.getBoundingClientRect().left; }");
            Assert.True(barLeft >= 0 && barLeft < 40, "the zoom bar panned away with the content: " + barLeft);

            // ---- neither drag annotated anything, with or without the annotate modifier ----
            await ScrollDiagramAsync(page, Oversized, 0, 0);
            var openedBefore = await CountEventsAsync(page, "composer-opened");
            await DragOverDiagramAsync(page, Oversized, -150, 0, alt: true);
            await DragOverDiagramAsync(page, Oversized, 0, -60);
            await AssertNoComposerForAsync(page, 900);
            Assert.Equal(openedBefore, await CountEventsAsync(page, "composer-opened"));
            Assert.Equal(0, (await ListAnnotationsAsync(server.Address, session.Key.Value)).GetArrayLength());

            // ---- Reset restores the block to exactly what the renderer emitted ----
            await page.ClickAsync(Ui("diagram-zoom-reset"));
            Assert.Equal("100%", await page.InnerTextAsync(Ui("diagram-zoom-level")));

            var reset = await DiagramProbeAsync(page, Oversized);
            Assert.Equal(string.Empty, reset.GetProperty("svgInlineWidth").GetString());
            Assert.Equal(string.Empty, reset.GetProperty("svgInlineMaxWidth").GetString());
            Assert.Equal(string.Empty, reset.GetProperty("blockInlineOverflow").GetString());
            Assert.Equal(string.Empty, reset.GetProperty("blockInlineMaxHeight").GetString());
            Assert.Equal(0, reset.GetProperty("scrollLeft").GetDouble());
            Assert.True(
                Math.Abs(reset.GetProperty("renderedWidth").GetDouble() - fitWidth) <= 1,
                "Reset must return the diagram to its fit width: " + reset);

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
    /// The regression this feature could most easily cause, asserted on the POSTED PAYLOAD: after a zoom
    /// AND a pan, a node click is still a <c>diagram-node</c> note carrying the BLOCK's anchor id and that
    /// node's own id (#48), and a background click is still the plain <c>element</c> note (#60). Both must
    /// still resolve to the <c>:::diagram</c>'s markdown line.
    ///
    /// <para>Every click here is a real mouse click at a point computed from the LIVE, zoomed, panned
    /// layout — never a locator click, which would scroll the block and quietly undo the pan the test is
    /// about.</para>
    /// </summary>
    [SkippableFact]
    public async Task Diagram_node_and_background_still_anchor_to_the_block_after_a_zoom_and_a_pan()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-diagram-zoom-anchor-" + Guid.NewGuid().ToString("N") + ".charter.md");
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

            await page.SetViewportSizeAsync(1000, 800);
            await page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(page, "ready");
            await WaitForDiagramsAsync(page, 2);
            await WaitForEventAsync(page, "diagram-zoomable");

            var blockId = await page.EvaluateAsync<string>("() => document.querySelectorAll('pre.mermaid')[0].id");
            Assert.False(string.IsNullOrEmpty(blockId), "the renderer must stamp a stable block id on pre.mermaid");

            await page.ClickAsync(Ui("diagram-zoom-in"));
            await page.ClickAsync(Ui("diagram-zoom-in"));

            // Zooming about the block's centre already moves the scroll offset, so the DRAG is measured
            // against where the zoom left it — otherwise this premise would hold with panning broken.
            var beforeDrag = (await DiagramProbeAsync(page, Oversized)).GetProperty("scrollLeft").GetDouble();
            await DragOverDiagramAsync(page, Oversized, -180, 0);
            var panned = (await DiagramProbeAsync(page, Oversized)).GetProperty("scrollLeft").GetDouble();
            Assert.True(
                panned > beforeDrag,
                "this test proves nothing unless the drag really panned the diagram (" +
                    beforeDrag + " -> " + panned + ")");

            // ---- a NODE, clicked where it actually is on screen now ----
            var node = await VisibleDiagramNodePointAsync(page, Oversized);
            await page.Keyboard.DownAsync("Alt");
            await page.Mouse.ClickAsync(node.X, node.Y);
            await page.Keyboard.UpAsync("Alt");
            await page.WaitForSelectorAsync(Ui("composer"));
            Assert.Contains(
                "diagram node", await page.InnerTextAsync(Ui("composer-context")), StringComparison.OrdinalIgnoreCase);
            await page.FillAsync(Ui("composer-input"), "this node needs a failure path");
            await page.ClickAsync(Ui("composer-save"));
            await WaitForEventAsync(page, "submitted");

            // The pan must survive the annotation round trip — losing it would put the reviewer back at the
            // top-left of a diagram they had just navigated.
            Assert.True(
                (await DiagramProbeAsync(page, Oversized)).GetProperty("scrollLeft").GetDouble() > 0,
                "annotating a node reset the diagram's pan");

            // ---- the BACKGROUND, likewise ----
            var background = await VisibleDiagramBackgroundPointAsync(page, Oversized);
            await page.Keyboard.DownAsync("Alt");
            await page.Mouse.ClickAsync(background.X, background.Y);
            await page.Keyboard.UpAsync("Alt");
            await page.WaitForSelectorAsync(Ui("composer"));
            Assert.Contains(
                "whole diagram", await page.InnerTextAsync(Ui("composer-context")), StringComparison.OrdinalIgnoreCase);
            await page.FillAsync(Ui("composer-input"), "this diagram is missing the retry edge");
            await page.ClickAsync(Ui("composer-save"));
            await WaitForEventAsync(page, "submitted");

            // ---- the posted payloads ----
            var listed = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(2, listed.GetArrayLength());

            var nodeNote = FindByNote(listed, "this node needs a failure path");
            Assert.Equal("diagram-node", nodeNote.GetProperty("kind").GetString());
            Assert.Equal(blockId, nodeNote.GetProperty("anchorId").GetString());
            Assert.Equal(node.NodeId, nodeNote.GetProperty("nodeId").GetString());
            AssertMapsToThePanZoomDiagram(nodeNote);

            var wholeNote = FindByNote(listed, "this diagram is missing the retry edge");
            Assert.Equal("element", wholeNote.GetProperty("kind").GetString());
            Assert.Equal(blockId, wholeNote.GetProperty("anchorId").GetString());
            Assert.Equal(JsonValueKind.Null, wholeNote.GetProperty("nodeId").ValueKind);
            AssertMapsToThePanZoomDiagram(wholeNote);

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
    /// The transient text highlight is painted as fixed-position rectangles from a <c>Range</c>'s client
    /// rects, so it is only correct while something repaints it. A zoom changes the diagram block's own
    /// height and therefore moves every block below it — exactly the class of movement a scroll or a
    /// resize causes, and it must be answered the same way.
    ///
    /// <para>Asserted as the OFFSET between the overlay and the paragraph it covers, which is invariant to
    /// anything else that may move the page (Playwright scrolling a control into view, for instance),
    /// with the reflow itself asserted separately so the test cannot pass by nothing having happened.</para>
    /// </summary>
    [SkippableFact]
    public async Task Annotation_overlay_stays_aligned_when_a_diagram_zoom_reflows_the_page()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-diagram-zoom-overlay-" + Guid.NewGuid().ToString("N") + ".charter.md");
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

            await page.SetViewportSizeAsync(1000, 900);
            await page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(page, "ready");
            await WaitForDiagramsAsync(page, 2);
            await WaitForEventAsync(page, "diagram-zoomable");

            // A text-range note on the paragraph BELOW the diagram — the block a zoom pushes down.
            var line = await FirstLineRectAsync(page, "body > p:nth-of-type(2)");
            await page.Mouse.MoveAsync(line.X + 4, line.Y + (line.Height / 2));
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(
                line.X + (line.Width * 0.7f), line.Y + (line.Height / 2), new MouseMoveOptions { Steps = 10 });
            await page.Mouse.UpAsync();
            await page.WaitForSelectorAsync(Ui("composer-input"));
            await page.FillAsync(Ui("composer-input"), "which paragraph the overlay covers");
            await page.ClickAsync(Ui("composer-save"));
            await WaitForEventAsync(page, "submitted");

            // Jump paints the highlight over that range. (The panel opens itself once there is an entry, so
            // the floating toggle is hidden — clicking it here would wait forever.)
            await page.WaitForSelectorAsync(Ui("item-jump"));
            await page.ClickAsync(Ui("item-jump"));
            await page.WaitForSelectorAsync(Ui("overlay-rect"));

            var before = await OverlayOffsetAsync(page);
            await page.ClickAsync(Ui("diagram-zoom-in"));
            var after = await OverlayOffsetAsync(page);

            // The premise: the zoom really did make the diagram taller, so the paragraph really did move.
            Assert.True(
                after.BlockHeight > before.BlockHeight + 10,
                "fixture drift: the zoom did not change the diagram's height, so nothing reflowed (" +
                    before.BlockHeight + " -> " + after.BlockHeight + ")");

            // The assertion: the highlight is still exactly over the text it names.
            Assert.True(
                Math.Abs(after.Offset - before.Offset) <= 1,
                "the annotation overlay drifted off its text when the diagram was zoomed (" +
                    before.Offset + " -> " + after.Offset + ")");

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
    /// Charter #68's precedent, applied: an affordance only a mouse can reach hides the diagram from a
    /// keyboard-only reviewer just as effectively as shrinking it did. The block itself is a tab stop, the
    /// zoom keys work there, arrow keys pan it (it is a real scroll container, so the browser does that
    /// for us) and <c>0</c> puts it back.
    /// </summary>
    [SkippableFact]
    public async Task Diagram_pan_and_zoom_are_reachable_and_operable_from_the_keyboard()
    {
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-diagram-zoom-keys-" + Guid.NewGuid().ToString("N") + ".charter.md");
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

            await page.SetViewportSizeAsync(1000, 800);
            await page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(page, "ready");
            await WaitForDiagramsAsync(page, 2);
            await WaitForEventAsync(page, "diagram-zoomable");

            // ---- TAB reaches it, from the top of the document, without a mouse ----
            await page.EvaluateAsync("() => { document.body.setAttribute('tabindex', '-1'); document.body.focus(); }");
            var reached = false;
            for (var i = 0; i < 6 && !reached; i++)
            {
                await page.Keyboard.PressAsync("Tab");
                reached = await page.EvaluateAsync<bool>(
                    "() => document.activeElement === document.querySelectorAll('pre.mermaid')[0]");
            }

            Assert.True(reached, "a keyboard-only reviewer cannot reach the zoomable diagram with Tab");

            // ---- the zoom keys ----
            await page.Keyboard.PressAsync("+");
            await page.Keyboard.PressAsync("+");
            Assert.Equal("156%", await page.InnerTextAsync(Ui("diagram-zoom-level")));

            // ---- and the arrows pan it ----
            for (var i = 0; i < 10; i++)
            {
                await page.Keyboard.PressAsync("ArrowRight");
            }

            Assert.True(
                await PollDiagramScrollLeftAsync(page, Oversized) > 0,
                "ArrowRight on the focused diagram did not pan it");

            // ---- 0 puts it back ----
            await page.Keyboard.PressAsync("0");
            Assert.Equal("100%", await page.InnerTextAsync(Ui("diagram-zoom-level")));
            Assert.Equal(0, (await DiagramProbeAsync(page, Oversized)).GetProperty("scrollLeft").GetDouble());

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

    // ---- #51 helpers -------------------------------------------------------------------------------------

    /// <summary>Wait until Mermaid has rendered <paramref name="count"/> diagrams to inline SVG.</summary>
    private static async Task WaitForDiagramsAsync(IPage page, int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            if (await page.Locator("pre.mermaid svg").CountAsync() >= count)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Mermaid never rendered " + count + " diagrams to <svg>");
    }

    /// <summary>
    /// Everything the pan/zoom affordance could get wrong about one rendered <c>:::diagram</c>, as JSON so
    /// the whole shape lands in any assertion message. <c>intrinsicWidth</c> vs <c>renderedWidth</c> is the
    /// defect itself: with Mermaid's <c>useMaxWidth</c> an oversized diagram never overflows, it shrinks.
    /// </summary>
    private static async Task<JsonElement> DiagramProbeAsync(IPage page, int index)
    {
        var json = await page.EvaluateAsync<string>(
            "i => {" +
            "  const el = document.querySelectorAll('pre.mermaid')[i];" +
            "  if (!el) return 'null';" +
            "  const svg = el.querySelector('svg');" +
            "  const box = svg ? svg.getBoundingClientRect() : { width: 0, height: 0 };" +
            "  const vb = svg && svg.viewBox && svg.viewBox.baseVal ? svg.viewBox.baseVal.width : 0;" +
            "  return JSON.stringify({" +
            "    blockId: el.id," +
            "    classes: el.className," +
            "    tabIndex: el.tabIndex," +
            "    role: el.getAttribute('role') || ''," +
            "    ariaLabel: el.getAttribute('aria-label') || ''," +
            "    hasBar: !!el.querySelector('[data-charter-ui=\"diagram-zoom\"]')," +
            "    intrinsicWidth: vb," +
            "    renderedWidth: box.width," +
            "    svgInlineWidth: svg ? svg.style.width : ''," +
            "    svgInlineMaxWidth: svg ? svg.style.maxWidth : ''," +
            "    svgTransform: svg ? getComputedStyle(svg).transform : ''," +
            "    blockInlineOverflow: el.style.overflow," +
            "    blockInlineMaxHeight: el.style.maxHeight," +
            "    blockHeight: el.getBoundingClientRect().height," +
            "    scrollLeft: el.scrollLeft, scrollTop: el.scrollTop," +
            "    scrollWidth: el.scrollWidth, clientWidth: el.clientWidth," +
            "    scrollHeight: el.scrollHeight, clientHeight: el.clientHeight" +
            "  });" +
            "}",
            index);

        Assert.NotEqual("null", json);
        using var doc = JsonDocument.Parse(json!);
        return doc.RootElement.Clone();
    }

    private static Task ScrollDiagramAsync(IPage page, int index, int left, int top)
        => page.EvaluateAsync(
            "a => { const el = document.querySelectorAll('pre.mermaid')[a.i];" +
            "  el.scrollLeft = a.left; el.scrollTop = a.top; return null; }",
            new { i = index, left, top });

    /// <summary>
    /// A real press-drag-release across the diagram, starting at the centre of its visible box. Delivered
    /// through <c>page.Mouse</c> so Chromium produces genuine pointer events — a synthetic
    /// <c>dispatchEvent</c> would prove nothing about the gesture a reviewer actually makes.
    /// </summary>
    private static async Task DragOverDiagramAsync(IPage page, int index, int dx, int dy, bool alt = false)
    {
        var box = await page.Locator("pre.mermaid").Nth(index).BoundingBoxAsync();
        Assert.NotNull(box);
        var startX = box!.X + (box.Width / 2);
        var startY = box.Y + (box.Height / 2);

        if (alt)
        {
            await page.Keyboard.DownAsync("Alt");
        }

        await page.Mouse.MoveAsync(startX, startY);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(startX + dx, startY + dy, new MouseMoveOptions { Steps = 12 });
        await page.Mouse.UpAsync();

        if (alt)
        {
            await page.Keyboard.UpAsync("Alt");
        }
    }

    private static async Task WheelOverDiagramAsync(IPage page, int index, int deltaY, bool control)
    {
        var box = await page.Locator("pre.mermaid").Nth(index).BoundingBoxAsync();
        Assert.NotNull(box);
        await page.Mouse.MoveAsync(box!.X + (box.Width / 2), box.Y + (box.Height / 2));

        if (control)
        {
            await page.Keyboard.DownAsync("Control");
        }

        await page.Mouse.WheelAsync(0, deltaY);

        if (control)
        {
            await page.Keyboard.UpAsync("Control");
        }

        // Wheel handling is asynchronous (the compositor delivers it), so give the SDK a bounded window to
        // answer before anything is read back.
        await Task.Delay(250);
    }

    /// <summary>
    /// Poll the diagram's own <c>scrollLeft</c> until it moves off zero, or give up. A bounded
    /// <c>EvaluateAsync</c> poll, never <c>WaitForFunctionAsync</c>, whose in-page <c>eval</c> the served
    /// page's CSP correctly refuses.
    /// </summary>
    private static async Task<double> PollDiagramScrollLeftAsync(IPage page, int index, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        double scrollLeft = 0;
        while (DateTime.UtcNow < deadline)
        {
            scrollLeft = await page.EvaluateAsync<double>(
                "i => { const el = document.querySelectorAll('pre.mermaid')[i]; return el ? el.scrollLeft : -1; }",
                index);
            if (scrollLeft > 0)
            {
                return scrollLeft;
            }

            await Task.Delay(50);
        }

        return scrollLeft;
    }

    /// <summary>
    /// A viewport point on a Mermaid NODE that is genuinely visible inside the (possibly clipped, possibly
    /// panned) block right now, plus that node's own Mermaid id. Scanned from the live layout and it FAILS
    /// when no such point exists, so a zoom that broke hit-testing cannot pass as "nothing to click".
    /// </summary>
    private static async Task<(float X, float Y, string NodeId)> VisibleDiagramNodePointAsync(
        IPage page, int index)
    {
        var json = await page.EvaluateAsync<string>(
            "i => {" +
            "  const block = document.querySelectorAll('pre.mermaid')[i];" +
            "  const br = block.getBoundingClientRect();" +
            "  const nodes = block.querySelectorAll('g.node');" +
            "  for (let n = 0; n < nodes.length; n++) {" +
            "    const r = nodes[n].getBoundingClientRect();" +
            "    for (let fy = 0.25; fy <= 0.8; fy += 0.15) {" +
            "      for (let fx = 0.2; fx <= 0.85; fx += 0.1) {" +
            "        const x = r.left + (r.width * fx), y = r.top + (r.height * fy);" +
            "        if (x < br.left + 2 || x > br.right - 2 || y < br.top + 2 || y > br.bottom - 2) continue;" +
            "        const el = document.elementFromPoint(x, y);" +
            "        if (!el || !block.contains(el) || el.closest('[data-charter-ui]')) continue;" +
            "        if (el.closest('g.node') !== nodes[n]) continue;" +
            "        return JSON.stringify({ x: x, y: y, nodeId: nodes[n].id });" +
            "      }" +
            "    }" +
            "  }" +
            "  return 'null';" +
            "}",
            index);

        Assert.NotEqual("null", json);
        using var doc = JsonDocument.Parse(json!);
        return (
            (float)doc.RootElement.GetProperty("x").GetDouble(),
            (float)doc.RootElement.GetProperty("y").GetDouble(),
            doc.RootElement.GetProperty("nodeId").GetString()!);
    }

    /// <summary>The same scan for the diagram's BACKGROUND — inside the visible box, on no node.</summary>
    private static async Task<(float X, float Y)> VisibleDiagramBackgroundPointAsync(IPage page, int index)
    {
        var json = await page.EvaluateAsync<string>(
            "i => {" +
            "  const block = document.querySelectorAll('pre.mermaid')[i];" +
            "  const br = block.getBoundingClientRect();" +
            "  for (let fy = 0.1; fy < 1; fy += 0.04) {" +
            "    for (let fx = 0.02; fx < 1; fx += 0.02) {" +
            "      const x = br.left + (br.width * fx), y = br.top + (br.height * fy);" +
            "      const el = document.elementFromPoint(x, y);" +
            "      if (!el || !block.contains(el)) continue;" +
            "      if (el.closest('.node, g.node, [data-node-id], [data-charter-ui]')) continue;" +
            "      return JSON.stringify({ x: x, y: y });" +
            "    }" +
            "  }" +
            "  return 'null';" +
            "}",
            index);

        Assert.NotEqual("null", json);
        using var doc = JsonDocument.Parse(json!);
        return (
            (float)doc.RootElement.GetProperty("x").GetDouble(),
            (float)doc.RootElement.GetProperty("y").GetDouble());
    }

    /// <summary>
    /// The first overlay rectangle's top relative to the paragraph it covers, plus the diagram block's
    /// height. The OFFSET is what must not change; the height is the proof that something moved at all.
    /// </summary>
    private static async Task<(double Offset, double BlockHeight)> OverlayOffsetAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(
            "() => {" +
            "  const rect = document.querySelector('[data-charter-ui=\"overlay-rect\"]');" +
            "  const prose = document.querySelectorAll('body > p')[1];" +
            "  const block = document.querySelectorAll('pre.mermaid')[0];" +
            "  if (!rect) return 'null';" +
            "  return JSON.stringify({" +
            "    offset: rect.getBoundingClientRect().top - prose.getBoundingClientRect().top," +
            "    blockHeight: block.getBoundingClientRect().height" +
            "  });" +
            "}");

        Assert.NotEqual("null", json);
        using var doc = JsonDocument.Parse(json!);
        return (
            doc.RootElement.GetProperty("offset").GetDouble(),
            doc.RootElement.GetProperty("blockHeight").GetDouble());
    }

    /// <summary>
    /// The drained annotation resolves to the <c>:::diagram</c>'s own source line — asserted against the
    /// plan text, so it proves the agent is pointed at the right markdown and not merely that a number
    /// arrived.
    /// </summary>
    private static void AssertMapsToThePanZoomDiagram(JsonElement annotation)
    {
        var line = annotation.GetProperty("sourceLine");
        Assert.True(
            line.ValueKind == JsonValueKind.Number,
            "the annotation reached the agent with no sourceLine (anchorId was '" +
                annotation.GetProperty("anchorId").GetString() + "')");
        Assert.Equal("resolved", annotation.GetProperty("anchorStatus").GetString());
        Assert.StartsWith(
            ":::diagram", PanZoomDiagramPlan.Split('\n')[line.GetInt32() - 1], StringComparison.Ordinal);
    }

    // ---- shared browser plumbing -------------------------------------------------------------------------

    /// <summary>A launched Playwright + browser pair, or <see langword="null"/> where Chromium is absent.</summary>
    private sealed record Launched(IPlaywright Playwright, IBrowser Browser);

    /// <summary>
    /// How long a NAVIGATION in this suite may take before it fails, deliberately far above Playwright's 30s
    /// default. Charter #66: <c>Answered_question_can_be_re_answered_and_save_tracks_the_change</c> hit the
    /// default on a contended <c>windows-latest</c> runner and a re-run of the identical commit passed 8/8. A
    /// gate that fails randomly gets re-run reflexively and stops being believed — which would quietly cost us
    /// the guard these tests exist to be (#37, #38, #57 were all invisible to the C#-string golden tests).
    ///
    /// Only navigation is relaxed, and only ONCE, on the context — so every page created from it inherits the
    /// bound and a new test cannot reintroduce the flake by forgetting a per-call option. Every ASSERTION below
    /// keeps its own tight, explicit deadline, so a genuine hang still fails; this only stops a slow cold start
    /// being reported as one.
    /// </summary>
    private const float NavigationTimeoutMs = 90_000;

    /// <summary>
    /// The suite's single browser-context factory. Everything that navigates goes through here, which is what
    /// makes <see cref="NavigationTimeoutMs"/> a property of the suite rather than of one call site.
    /// </summary>
    private static async Task<IBrowserContext> NewContextAsync(IBrowser browser)
    {
        var context = await browser.NewContextAsync();
        context.SetDefaultNavigationTimeout(NavigationTimeoutMs);
        return context;
    }

    /// <param name="showScrollbars">
    /// Drop Chromium's default <c>--hide-scrollbars</c> flag, which Playwright passes for headless runs and
    /// which forces EVERY scrollbar to zero width. A test that measures a scroll affordance (Charter #68)
    /// must opt out of it or it measures the flag, not the stylesheet. Every other test keeps the default, so
    /// no existing layout assertion shifts.
    /// </param>
    private static async Task<Launched?> TryLaunchAsync(bool showScrollbars = false)
    {
        try
        {
            var playwright = await Playwright.CreateAsync();
            var options = new BrowserTypeLaunchOptions { Headless = true };

            // --hide-scrollbars is a Chromium flag; WebKit and Firefox reject unknown args outright, so this
            // opt-out is only meaningful (and only safe) on the Chromium family (#110).
            if (showScrollbars && BrowserEngine.IsChromium)
            {
                options.IgnoreDefaultArgs = new[] { "--hide-scrollbars" };
            }

            var browser = await BrowserEngine.For(playwright).LaunchAsync(options);
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
        var context = await NewContextAsync(launched.Browser);
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
    /// A record's <c>anchor.base</c> — the plan's content hash as the reviewer saw it (§4). Computed here
    /// rather than through the server's minting helper so this test pins the committed wire format from a
    /// consumer's side.
    /// </summary>
    private static string PlanHash(string markdown)
        => "sha256:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(markdown))).ToLowerInvariant();

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
