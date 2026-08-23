using System.Net;
using System.Text.Json;
using Charter.Core;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #209 — a note the reviewer saved must not be erased by a queue read that predates it.
///
/// <para><b>The defect, in one sentence.</b> <c>hydrate()</c> assigned <c>GET /api/annotations</c> straight
/// over <c>state.annotations</c>. That read is a SNAPSHOT of whenever the server took it, so a save landing
/// between the taking and the delivery was simply overwritten — and since nothing re-renders afterwards, the
/// page then sat showing fewer notes than the reviewer had saved, indefinitely.</para>
///
/// <para><b>Why it was filed as a wait.</b> It surfaced as
/// <c>A_railed_badge_does_not_ride_away_with_the_tables_horizontal_scroll</c> failing on macOS CI with
/// <c>Expected: "2" Actual: "1"</c> after seeding two notes, which reads exactly like a probe that ran too
/// early — and this codebase has a family of those (a <c>submitted</c> wait satisfied by the first submit; a
/// badge-present wait satisfied by a badge about to be swept). It is not one of them, and the distinction is
/// worth stating because it decides what a fix may be: <b>the regression arrives after every signal a test
/// could wait for</b> — after the POST, after its render, after <c>submitted</c>. A wait cannot see it, one
/// more render is the damage rather than the repair, and a re-run "fixes" it, which is how it stayed
/// nameless through its first occurrence.</para>
///
/// <para><b>How this test makes a CI-only race deterministic.</b> It does not slow anything down or hope. It
/// holds the page's own load-time queue read at the network boundary, lets the server answer it at a chosen
/// moment — after the first note and before the second — and delivers that genuine, genuinely stale response
/// once both are saved. Every byte is the real server's; only the timing is chosen. The window that opens on
/// a contended runner is therefore opened on every runner, on both engines.</para>
///
/// <para><b>The three premises are asserted, not assumed</b>, because each one is a way this test could pass
/// while proving nothing: the intercepted read really did carry exactly one note (or it was never stale); the
/// SDK really did decline it (or the interception missed and this measured an ordinary page); and the server
/// really does hold two (or "2" on the badge would be agreement about a wrong number).</para>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    [SkippableFact]
    public async Task A_note_saved_while_a_queue_read_is_in_flight_survives_that_read_landing()
    {
        var planPath = NewPlanPath("stale-queue-read");
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

            // `list-loaded` carries whether the read was applied or declined. Recorded across the same
            // postMessage boundary as every other event, so the assertion below is on the SDK's own
            // structured report rather than on an inference from the DOM.
            await page.AddInitScriptAsync(
                "window.__charterListLoads = [];" +
                "window.addEventListener('message', function (e) {" +
                "  if (e && e.data && e.data.channel === 'charter-annotate' && e.data.type === 'list-loaded') {" +
                "    window.__charterListLoads.push(!!(e.data.detail && e.data.detail.stale));" +
                "  }" +
                "});");

            var held = await HoldFirstQueueReadAsync(page);

            await OpenBadgeGateAsync(page, server, session);

            var table = RailedBlocks.Single(b => b.Tag == "TABLE");
            var anchorId = await RequireAnchorIdAsync(page, table);

            // ---- the reviewer's first note ------------------------------------------------------------
            await SeedNotesAsync(page, anchorId, 1, "the first note on the table");

            // ---- ...and NOW the server answers the read that was issued before it ---------------------
            var snapshot = await held.TakeSnapshotAsync();
            var snapshotted = JsonDocument.Parse(snapshot).RootElement.GetArrayLength();
            Assert.True(
                snapshotted == 1,
                "the intercepted queue read carried " + snapshotted + " annotation(s), not the 1 this test " +
                    "needs it to carry. A read that already agrees with the page cannot be stale, so the " +
                    "race below would never open and this test would pass without exercising anything.");

            // ---- the second note, saved while that read is still on the wire --------------------------
            await SeedNotesAsync(page, anchorId, 1, "the second note on the table");

            var loadsBefore = await CountListLoadsAsync(page);
            await held.DeliverAsync();
            await WaitForListLoadAsync(page, loadsBefore + 1);

            // Truth is the server's own queue, never the attribute the badge was rendered from — the rule
            // this file is built on (#164).
            var truth = await ListAnnotationsAsync(server.Address, session.Key.Value);
            var queued = truth.EnumerateArray()
                .Count(a => string.Equals(a.GetProperty("anchorId").GetString(), anchorId, StringComparison.Ordinal));
            Assert.Equal(2, queued);

            // THE assertion, and the one that reproduces #209's CI failure verbatim: pre-fix this reads
            // Expected "2" / Actual "1", every run, on both engines.
            var probe = await ProbeBadgeAsync(page, anchorId);
            AssertBadgeIsRealAndPlaced(probe, "TABLE", expected: 2);

            // ...and last, the guard's own report, so a future page that reaches the right answer by luck
            // rather than by declining the read is still caught. Deliberately AFTER the measurement: a
            // premise that only the fix can satisfy would make the pre-fix failure a statement about the
            // fix's instrumentation instead of about the reviewer's vanished note.
            var declined = await page.EvaluateAsync<bool>(
                "() => { const l = window.__charterListLoads || []; return !!l[l.length - 1]; }");
            Assert.True(
                declined,
                "the badge is right but the SDK APPLIED the stale queue read instead of declining it, so " +
                    "#209's guard was never reached and this run proves nothing about it (#209).");

            // ---- and the guard is a GUARD, not a blanket refusal --------------------------------------
            // Everything above is equally true of an SDK that simply never applies a queue read at all —
            // which would be a worse bug than #209, and a silent one, because the delivery axis (#124) would
            // freeze while the notes still looked right. So one more read is asked for with nothing racing
            // it, and it has to be APPLIED.
            var beforeFresh = await CountListLoadsAsync(page);
            await page.EvaluateAsync("() => { window.CharterAnnotate.list(); return null; }");
            await WaitForListLoadAsync(page, beforeFresh + 1);

            var appliedFresh = await page.EvaluateAsync<bool>(
                "() => { const l = window.__charterListLoads || []; return l[l.length - 1] === false; }");
            Assert.True(
                appliedFresh,
                "a queue read with no write racing it was ALSO declined, so #209's guard is refusing every " +
                    "read rather than only the ones this page has written past.");

            var afterFresh = await ProbeBadgeAsync(page, anchorId);
            AssertBadgeIsRealAndPlaced(afterFresh, "TABLE", expected: 2);

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    /// <summary>
    /// Charter #209, the other direction. A retract is a queue write too, so a snapshot taken before it still
    /// lists the withdrawn note — and applying that would put it back on the page under a reviewer who just
    /// took it off. Same guard, same clock; asserted separately because a fix that only counted saves would
    /// pass the test above and leave this one live.
    /// </summary>
    [SkippableFact]
    public async Task A_note_retracted_while_a_queue_read_is_in_flight_is_not_resurrected_by_it()
    {
        var planPath = NewPlanPath("stale-queue-retract");
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

            await page.AddInitScriptAsync(
                "window.__charterListLoads = [];" +
                "window.addEventListener('message', function (e) {" +
                "  if (e && e.data && e.data.channel === 'charter-annotate' && e.data.type === 'list-loaded') {" +
                "    window.__charterListLoads.push(!!(e.data.detail && e.data.detail.stale));" +
                "  }" +
                "});");

            var held = await HoldFirstQueueReadAsync(page);

            await OpenBadgeGateAsync(page, server, session);

            var table = RailedBlocks.Single(b => b.Tag == "TABLE");
            var anchorId = await RequireAnchorIdAsync(page, table);

            await SeedNotesAsync(page, anchorId, 2, "a note on the table");

            // The read is taken while both notes are queued...
            var snapshot = await held.TakeSnapshotAsync();
            Assert.Equal(2, JsonDocument.Parse(snapshot).RootElement.GetArrayLength());

            // ...and one of them is withdrawn before it is delivered.
            var doomed = (await ListAnnotationsAsync(server.Address, session.Key.Value))
                .EnumerateArray().First().GetProperty("id").GetString();
            var deleted = await CountEventsAsync(page, "annotation-deleted");
            await page.EvaluateAsync(
                "(id) => { window.postMessage(" +
                "  { channel: 'charter-annotate', type: 'delete', detail: { id: id } }, '*'); return null; }",
                doomed!);
            await WaitForEventCountAsync(page, "annotation-deleted", deleted + 1, atLeast: true);

            var loadsBefore = await CountListLoadsAsync(page);
            await held.DeliverAsync();
            await WaitForListLoadAsync(page, loadsBefore + 1);

            var truth = await ListAnnotationsAsync(server.Address, session.Key.Value);
            var queued = truth.EnumerateArray()
                .Count(a => string.Equals(a.GetProperty("anchorId").GetString(), anchorId, StringComparison.Ordinal));
            Assert.Equal(1, queued);

            var probe = await ProbeBadgeAsync(page, anchorId);
            AssertBadgeIsRealAndPlaced(probe, "TABLE", expected: 1);

            var declined = await page.EvaluateAsync<bool>(
                "() => { const l = window.__charterListLoads || []; return !!l[l.length - 1]; }");
            Assert.True(
                declined,
                "the badge is right but the SDK APPLIED the stale queue read, so a retract is not advancing " +
                    "the write clock and this run proves nothing about the guard (#209).");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            Cleanup(planPath);
        }
    }

    // ---- the interception ------------------------------------------------------------------------------

    /// <summary>
    /// Hold the page's FIRST <c>GET /api/annotations</c> at the network boundary and hand the test two
    /// switches: when the server may answer it, and when the browser may see the answer.
    ///
    /// <para>This is what turns a runner-dependent race into a fact. The body is fetched from the real server
    /// at the moment <see cref="HeldQueueRead.TakeSnapshotAsync"/> is called and fulfilled unchanged, so what
    /// lands is a genuine response that is genuinely out of date — never a fabricated one. Only the two
    /// timings are the test's.</para>
    ///
    /// <para>Subsequent reads (a <c>queue</c> frame after a drain) are passed straight through, so nothing
    /// beyond the one window under test is altered.</para>
    /// </summary>
    private static async Task<HeldQueueRead> HoldFirstQueueReadAsync(IPage page)
    {
        var held = new HeldQueueRead();
        var seen = 0;

        await page.RouteAsync("**/api/annotations?*", async route =>
        {
            if (Interlocked.Increment(ref seen) > 1)
            {
                await route.ContinueAsync();
                return;
            }

            try
            {
                await held.MayAnswer.Task;
                var response = await route.FetchAsync();
                held.Body.TrySetResult(await response.TextAsync());
                await held.MayDeliver.Task;
                await route.FulfillAsync(new RouteFulfillOptions { Response = response });
            }
            catch (Exception ex)
            {
                // A failed interception must surface as the assertion that needed it, not as a hang.
                held.Body.TrySetException(ex);
            }
        });

        return held;
    }

    private sealed class HeldQueueRead
    {
        internal TaskCompletionSource MayAnswer { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource MayDeliver { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<string> Body { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Let the server answer the held read NOW, and return the body it produced.</summary>
        internal async Task<string> TakeSnapshotAsync()
        {
            MayAnswer.TrySetResult();
            var done = await Task.WhenAny(Body.Task, Task.Delay(ReadinessTimeoutMs));
            Assert.True(
                ReferenceEquals(done, Body.Task),
                "the page never issued the load-time GET /api/annotations that this test holds, so the " +
                    "stale-read window could not be opened.");
            return await Body.Task;
        }

        internal Task DeliverAsync()
        {
            MayDeliver.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private static Task<int> CountListLoadsAsync(IPage page)
        => page.EvaluateAsync<int>("() => (window.__charterListLoads || []).length");

    // A readiness gate like every other, so it takes the suite's one deadline rather than a local literal.
    private static async Task WaitForListLoadAsync(IPage page, int target, int timeoutMs = ReadinessTimeoutMs)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await CountListLoadsAsync(page) >= target)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail(
            "the held queue read never reached the SDK within " + timeoutMs + "ms: " +
            await CountListLoadsAsync(page) + " 'list-loaded' event(s) seen, expected " + target +
            ". Without it nothing below is measuring #209's window.");
    }
}
