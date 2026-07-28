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
        var planPath = Path.Combine(
            Path.GetTempPath(), "charter-question-modes-" + Guid.NewGuid().ToString("N") + ".charter.md");
        await File.WriteAllTextAsync(planPath, ModesPlan);

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
            Skip.If(launched is null, "Chromium/Playwright unavailable on this host.");

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
            Skip.If(launched is null, "Chromium/Playwright unavailable on this host.");

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
            Skip.If(launched is null, "Chromium/Playwright unavailable on this host.");

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
            Skip.If(launched is null, "Chromium/Playwright unavailable on this host.");

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
            Skip.If(launched is null, "Chromium/Playwright unavailable on this host.");

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
            Skip.If(launched is null, "Chromium/Playwright unavailable on this host.");

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
            var orphan = bob.AppendCreate(
                new ReviewAnchor("b-no-such-block", "element", "the read path will be built after", null),
                "a note whose block the agent has since rewritten");

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
            Assert.Contains(
                "The plan has changed",
                await stranded.Locator(Ui("item-orphan-note")).InnerTextAsync(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "addressed",
                await stranded.InnerTextAsync(),
                StringComparison.OrdinalIgnoreCase);

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
            Skip.If(launched is null, "Chromium/Playwright unavailable on this host.");

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
            Skip.If(launched is null, "Chromium/Playwright unavailable on this host.");

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
            if (showScrollbars)
            {
                options.IgnoreDefaultArgs = new[] { "--hide-scrollbars" };
            }

            var browser = await playwright.Chromium.LaunchAsync(options);
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
