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
