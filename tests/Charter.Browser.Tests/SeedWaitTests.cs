using System.Linq;
using System.Net;
using Charter.Core;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #209, the WAIT half — a test of <see cref="ReviewLoopBrowserTests"/>'s own seeding helper rather
/// than of the product.
///
/// <para><b>What this exists to falsify.</b> <c>SeedNotesAsync</c> used to close with
/// <c>WaitForEventCountAsync("markers-rendered", markersBefore + 1)</c>. That wait is the third instance of
/// one family in this suite — a wait a PARTIAL result also satisfies — because an increment of ONE is
/// produced by the FIRST note's render, whatever N is. The corrected wait is <c>+ count</c>. A correction
/// nobody has watched fail is a hypothesis, so this test watches it: it seeds two notes, waits exactly the
/// old way, and shows the page in a state where the badge does not yet know about both.</para>
///
/// <para><b>Why it is deterministic and not a hope.</b> The second note's POST is held at the network
/// boundary, so its render provably has not happened when the first probe runs — the partial state is
/// constructed, not waited for, and the assertion is the same on an idle laptop and a saturated CI runner.
/// The hold is then released and the honest gate — the per-note <c>submitted</c> count, which is what
/// <c>SeedNotesAsync</c> actually leans on — is shown to admit no such state.</para>
///
/// <para><b>The assertion is deliberately "not 2", never "is 1".</b> Pinning the digit would pin a defect as
/// expected behaviour, which this codebase has been bitten by before (#79, #78). The claim is a property of
/// the wait: one render increment is not evidence that two notes have landed.</para>
///
/// <para>Note what this does NOT claim. A render count is a weak proxy in either direction — one saved note
/// starts several renders (the POST's own, <c>hydrateLog()</c>'s, the <c>review-log</c> SSE frame's), so
/// <c>+ count</c> is a floor rather than a correspondence. What makes <c>SeedNotesAsync</c> sound is the
/// per-note <c>submitted</c> wait inside its loop; the corrected trailing wait is there so that the day
/// those are fired concurrently, the helper does not silently become this test's first half.</para>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    [SkippableFact]
    public async Task One_render_increment_is_not_evidence_that_both_seeded_notes_have_landed()
    {
        var planPath = NewPlanPath("seed-wait");
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

            // Hold the SECOND save. Everything else — the first save, the queue and log reads, the round
            // state — goes straight through, so the only thing this test changes about the page is WHEN one
            // response arrives.
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondSaveReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var saves = 0;
            await page.RouteAsync("**/prompts", async route =>
            {
                if (Interlocked.Increment(ref saves) != 2)
                {
                    await route.ContinueAsync();
                    return;
                }

                secondSaveReached.TrySetResult();
                await release.Task;
                await route.ContinueAsync();
            });

            await OpenBadgeGateAsync(page, server, session);
            // The load's OWN two renders have to be spent before the baseline is taken, or the wait under
            // test is satisfied by them rather than by a note — which is a fourth way the same `+1` is not a
            // wait, and it would leave nothing badged for the probe to read. Both of these fire exactly once
            // per page (init only), which is the one condition that makes WaitForEventAsync legitimate.
            await WaitForEventAsync(page, "list-loaded");
            await WaitForEventAsync(page, "review-log-loaded");

            var table = RailedBlocks.Single(b => b.Tag == "TABLE");
            var anchorId = await RequireAnchorIdAsync(page, table);

            // ---- seed two notes the way a mutated helper would: fire both, wait for ONE render ----------
            var markersBefore = await CountEventsAsync(page, "markers-rendered");
            for (var i = 1; i <= 2; i++)
            {
                await page.EvaluateAsync(
                    "([id, note]) => { window.CharterAnnotate.annotate(" +
                    "  { anchorId: id, kind: 'element', note: note }); return null; }",
                    new[] { anchorId, "a note on the table #" + i });
            }

            await secondSaveReached.Task;
            await WaitForEventCountAsync(page, "markers-rendered", markersBefore + 1, atLeast: true);

            var partial = await ProbeBadgeAsync(page, anchorId);
            Assert.True(
                partial.HasBadge,
                "the first note never badged the block, so this test is measuring something other than a " +
                    "partial seed: " + partial);
            Assert.True(
                partial.Text != "2",
                "Charter #209 — a probe taken after ONE marker-render increment read '" + partial.Text +
                    "' for two seeded notes, which would mean the old wait was safe after all. It is not: " +
                    "the second note's POST is still held open at the network boundary here, so the page " +
                    "cannot honestly know about it, and any run in which it does has stopped constructing " +
                    "the race this test exists to construct.");

            // ---- and the gate SeedNotesAsync actually leans on admits no such state ---------------------
            release.TrySetResult();
            await WaitForEventCountAsync(page, "submitted", 2, atLeast: true);

            var truth = await ListAnnotationsAsync(server.Address, session.Key.Value);
            Assert.Equal(
                2,
                truth.EnumerateArray().Count(a =>
                    string.Equals(a.GetProperty("anchorId").GetString(), anchorId, StringComparison.Ordinal)));

            var complete = await ProbeBadgeAsync(page, anchorId);
            AssertBadgeIsRealAndPlaced(complete, "TABLE", expected: 2);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }
}
