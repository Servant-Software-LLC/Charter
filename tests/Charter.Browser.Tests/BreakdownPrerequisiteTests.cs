using System.Net;
using System.Text.Json;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #243 — the breakdown row's stop-draining prerequisite was a footnote, and was shown whether or not it
/// applied.
///
/// <para>
/// A reviewer's drain agent told them to stop the watch before a breakdown, or the breakdown would queue behind it
/// and look like nothing happened. The page already said so — in 11px of muted grey BENEATH the command and its Copy
/// button, and with open notes as the middle sentence of one grey paragraph. Worse, it said so unconditionally
/// while holding the one signal that says whether it applies: <c>state.agent.waiting</c> (#107). A caveat that fires
/// when its condition is not true trains the reader to skip it, and they skip it on the day it matters.
/// </para>
/// <para>
/// This drives BOTH presence states with a REAL long poll held open against the server — the thing that actually
/// makes <c>waiting</c> true — rather than stubbing the status the page reads. And it pins the half that is easy to
/// get wrong in the other direction: without positive evidence the caveat is DEMOTED, never dropped, and nothing on
/// the row ever claims that no agent is draining. Presence is evidence, not proof: an agent polling in a loop reads
/// as not waiting between cycles while still draining.
/// </para>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    [SkippableFact]
    [Trait("Feature", "BreakdownPrerequisite")]
    public async Task The_stop_draining_prerequisite_is_promoted_only_while_an_agent_is_listening()
    {
        var directory = Path.Combine(Path.GetTempPath(), "charter-breakdown-prereq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var planPath = Path.Combine(directory, "plan.charter.md");
        await File.WriteAllTextAsync(planPath, Plan);

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        using var agent = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var stopListening = new CancellationTokenSource();
        Task<HttpResponseMessage>? longPoll = null;

        try
        {
            var launched = await TryLaunchAsync();
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;

            // ---- nothing listening: demoted to the quiet note, never dropped, never a claim of absence --------
            await OpenBreakdownRowAsync(page, CapabilityUrl(server, session), reload: false);

            var row = page.Locator(Ui("breakdown-command"));
            Assert.Equal("false", await row.GetAttributeAsync("data-charter-agent-listening"));
            Assert.False(
                await page.Locator(Ui("breakdown-command-prerequisite")).IsVisibleAsync(),
                "with no agent listening the prerequisite must not compete for attention on a line of its own");

            var quiet = await page.Locator(Ui("breakdown-command-note")).TextContentAsync() ?? string.Empty;
            Assert.Contains("stop draining", quiet, StringComparison.OrdinalIgnoreCase);
            AssertNeverClaimsNoAgentIsDraining(await row.InnerTextAsync());

            // ---- an agent listening: a REAL long poll held open on this session --------------------------------
            longPoll = agent.GetAsync(
                new Uri(server.Address, "api/poll?key=" + Uri.EscapeDataString(session.Key.Value)),
                stopListening.Token);

            // Reload only once the SERVER reports the agent — the page then reads exactly what the server knows,
            // so the reload cannot race the wait being registered.
            await WaitForServerToReportAListeningAgentAsync(server, session);
            await OpenBreakdownRowAsync(page, CapabilityUrl(server, session), reload: true);

            row = page.Locator(Ui("breakdown-command"));
            Assert.Equal("true", await row.GetAttributeAsync("data-charter-agent-listening"));

            var prerequisite = page.Locator(Ui("breakdown-command-prerequisite"));
            Assert.True(await prerequisite.IsVisibleAsync(), "an agent is listening, so the prerequisite must be on its own line");
            Assert.Contains("stop draining", await prerequisite.InnerTextAsync(), StringComparison.OrdinalIgnoreCase);

            // Legible BEFORE the copy: it precedes both the command and the Copy button, and it is not the muted
            // grey of the footnote it used to be. Measured together, synchronously, after the last await.
            var placement = await page.EvaluateAsync<JsonElement>(
                "() => {" +
                "  const q = n => document.querySelector('[data-charter-ui=\"' + n + '\"]');" +
                "  const pre = q('breakdown-command-prerequisite');" +
                "  const text = q('breakdown-command-text');" +
                "  const copy = q('breakdown-command-copy');" +
                "  const note = q('breakdown-command-note');" +
                "  const FOLLOWING = 4;" +
                "  return {" +
                "    beforeCommand: !!(pre.compareDocumentPosition(text) & FOLLOWING)," +
                "    beforeCopy: !!(pre.compareDocumentPosition(copy) & FOLLOWING)," +
                "    prerequisiteColour: getComputedStyle(pre).color," +
                "    footnoteColour: getComputedStyle(note).color" +
                "  };" +
                "}");
            Assert.True(placement.GetProperty("beforeCommand").GetBoolean(), "the prerequisite must precede the command it governs");
            Assert.True(placement.GetProperty("beforeCopy").GetBoolean(), "the prerequisite must be read before Copy is pressed");
            Assert.NotEqual(
                placement.GetProperty("footnoteColour").GetString(),
                placement.GetProperty("prerequisiteColour").GetString());

            // It is said once: while it has its own line, the quiet note stops repeating it.
            var standing = await page.Locator(Ui("breakdown-command-note")).TextContentAsync() ?? string.Empty;
            Assert.DoesNotContain("stop draining", standing, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("never a run", standing, StringComparison.OrdinalIgnoreCase);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            stopListening.Cancel();
            if (longPoll is not null)
            {
                try
                {
                    (await longPoll).Dispose();
                }
                catch (OperationCanceledException)
                {
                    // The agent stopped listening, which is the point.
                }
            }

            TryDeleteTree(directory);
        }
    }

    private static async Task OpenBreakdownRowAsync(IPage page, string url, bool reload)
    {
        if (reload)
        {
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.Load });
        }
        else
        {
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        }

        // The init script re-runs on every load, so the event log starts empty and these are first sightings.
        await WaitForEventAsync(page, "ready");
        await WaitForEventAsync(page, "round-loaded");

        await page.ClickAsync(Ui("panel-toggle"));
        await WaitForEventAsync(page, "panel-opened");
        await page.WaitForSelectorAsync(Ui("breakdown-command"));
    }

    private static async Task WaitForServerToReportAListeningAgentAsync(ReviewServer server, ReviewSession session)
    {
        using var client = new HttpClient();
        var status = new Uri(server.Address, "api/review?key=" + Uri.EscapeDataString(session.Key.Value));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            using var document = JsonDocument.Parse(await client.GetStringAsync(status));
            if (document.RootElement.TryGetProperty("agent", out var agent)
                && agent.ValueKind == JsonValueKind.Object
                && agent.GetProperty("waiting").GetBoolean())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("the server never reported the long-polling agent as waiting, so the listening pass would prove nothing");
    }

    private static void AssertNeverClaimsNoAgentIsDraining(string rowText)
    {
        foreach (var claim in new[] { "no agent", "not draining", "nothing is draining", "go ahead", "safe to paste" })
        {
            Assert.DoesNotContain(claim, rowText, StringComparison.OrdinalIgnoreCase);
        }
    }
}
