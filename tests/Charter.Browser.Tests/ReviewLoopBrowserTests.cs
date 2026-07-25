using System.Net;
using System.Net.Http;
using System.Text.Json;
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
