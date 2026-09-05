using System.Net;
using System.Text.Json;
using Charter.Core;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #221 — a review-log read that could not tell must not be rendered as one that found nothing.
///
/// <para><b>The defect, in one sentence.</b> <c>hydrateLog()</c> assigns <c>GET /api/review-log</c> straight
/// over <c>state.log</c> and then renders. An answer carrying zero comments therefore empties a populated
/// panel — and <c>renderPanel</c> does not update its cards, it destroys and rebuilds them, so the card the
/// reviewer was standing on is REMOVED, no counterpart is built, and the browser drops focus to
/// <c>&lt;body&gt;</c>. That is exactly the CI trace this issue was filed from:
/// <c>focus-not-restored built=false panelHidden=false items=0</c>, on a plan with three teammate notes on
/// disk.</para>
///
/// <para><b>Why the server half is not enough.</b> Tasks 01-06 gave the read a third outcome and put it on the
/// wire, so the view now says <c>outcome: unknown</c> instead of pretending nobody commented. Nothing in the
/// browser reads that field yet, so the panel still empties for exactly the same reason it always did. These
/// four tests are what makes the SDK guard obligatory, and they fail on the current tree because the SDK
/// applies any view unconditionally today.</para>
///
/// <para><b>How a runner-dependent race becomes a fact.</b> Not a sleep and not a hope:
/// <see cref="HoldNextReviewLogReadAsync"/> holds the page's own re-read at the Playwright route boundary,
/// takes <c>.review/</c> away while it is in flight, lets the REAL server answer at a chosen moment, and
/// delivers that genuine response once the page is committed. Every byte is the server's; only the two
/// timings are the test's — the <c>HoldFirstQueueReadAsync</c> shape #209 used, which is what made that race
/// reproduce identically on every runner and both engines.</para>
///
/// <para><b>Fetching the answer rather than fabricating it is load-bearing.</b> The C# side names the field on
/// a PascalCase record and the SDK reads its camelCase name off the wire; a test that fulfilled the route with
/// a hand-written body would be red before the fix and green after it while being completely blind to the two
/// halves having agreed on a name. So the intercepted answer is asserted — <c>outcome</c>, spelled the way the
/// server really spells it — before anything downstream of it is measured.</para>
///
/// <para><b>The anti-tautology control is not optional.</b> Everything the first three tests assert is equally
/// true of an SDK that simply never applies a review-log view at all — a worse bug, and a silent one, because
/// a teammate's arriving note would stop reaching the panel while the panel still looked right. So the fourth
/// test hands the page a genuinely EMPTY view and requires that it be applied, and requires the SDK to say so
/// with the same structural fact: <c>declined</c> on the <c>review-log-loaded</c> detail, the mirror of #209's
/// <c>stale</c>, which exists precisely so the branch cannot rot into one nothing proves was taken.</para>
///
/// <para>In the three tests where the symptom is the point, that structural fact is asserted AFTER the symptom
/// — so the pre-fix failure is a report about the reviewer's vanished panel rather than about the fix's own
/// instrumentation.</para>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    // ---- #221: the panel keeps what it was showing -------------------------------------------------------

    /// <summary>
    /// Charter #221 — the headline symptom. Two teammate comments are on the page; the review directory is
    /// taken away under an in-flight re-read; the genuine <c>unknown</c> answer lands. Both cards must still
    /// be there.
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "ReviewLogUnknownPanel")]
    public async Task An_unknown_view_does_not_empty_a_populated_panel()
    {
        var directory = NewPlanDirectory("unknown-panel-kept");
        var planPath = Path.Combine(directory, "unknown-panel.charter.md");
        await File.WriteAllTextAsync(planPath, BadgeGatePlan);

        var mine = new ReviewLogWriter(planPath, new ReviewAuthor("Alice Ng", "alice@example.com"));
        var teammate = new ReviewLogWriter(planPath, new ReviewAuthor("Bob Chen", "bob@example.com"));

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(session, new ReviewServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            ReviewLog = mine,
        });

        try
        {
            var launched = await TryLaunchAsync();
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;

            await page.AddInitScriptAsync(RecordLogLoadsScript);
            await OpenBadgeGateAsync(page, server, session);
            await SeedTeammateLogAsync(page, teammate);

            // The read is issued while the directory is still there, and the directory goes away UNDER it —
            // which is the shape of the blip the CI trace caught, not a steady state the page could have
            // been loaded into.
            var held = await HoldNextReviewLogReadAsync(page);
            await RereadTheLogAsync(page);
            await RemoveReviewDirectoryAsync(teammate.ReviewDirectory);

            AssertServedOutcomeIs(await held.TakeSnapshotAsync(), "unknown");

            var loadsBefore = await CountLogLoadsAsync(page);
            held.Deliver();
            await WaitForLogLoadAsync(page, loadsBefore + 1);

            // THE assertion, and the one that reproduces #221's trace verbatim: pre-fix this reads items=0.
            var items = await PanelItemCountAsync(page);
            Assert.True(
                items == 2,
                "Charter #221 — the panel is showing " + items + " comment(s) after a view that said only " +
                    "that it could not read the directory. Two teammate comments were on the page and the " +
                    "server never claimed either had gone; an answer of 'I do not know' was rendered as " +
                    "'nobody commented'.");

            // ...and last, the guard's own report, so a page that keeps the right number by never applying a
            // view at all is still caught. Deliberately AFTER the measurement: a premise only the fix can
            // satisfy would make the pre-fix failure a statement about the fix's instrumentation instead of
            // about the reviewer's emptied panel.
            var report = await LastLogLoadAsync(page);
            Assert.True(
                await LastLogLoadDeclinedAsync(page, true),
                "the panel is right but the SDK did not report DECLINING the unknown view, so #221's guard " +
                    "was never reached and this run proves nothing about it. The last review-log-loaded " +
                    "detail was " + report + " (a missing 'declined' field means the branch does not exist).");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            CleanupDirectory(directory);
        }
    }

    // ---- #221: and the reviewer keeps their place --------------------------------------------------------

    /// <summary>
    /// Charter #221 — the REPORTED failure, end to end. A reviewer is standing on a note card, keyboard-first,
    /// when the unknown view lands. The card is what <c>renderPanel</c> rebuilds, so an applied empty view
    /// removes the element holding focus and leaves nothing to restore it to — <c>built=false</c>, and focus on
    /// <c>&lt;body&gt;</c>.
    ///
    /// <para>Focus is walked with real <c>Tab</c> presses and the panel is opened with a real <c>Enter</c>, the
    /// rule the rest of the #200 family is built on: the claim is about a reviewer's keyboard, not about an
    /// element being programmatically focusable.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "ReviewLogUnknownPanel")]
    public async Task An_unknown_view_does_not_drop_focus_to_body()
    {
        var directory = NewPlanDirectory("unknown-panel-focus");
        var planPath = Path.Combine(directory, "unknown-focus.charter.md");
        await File.WriteAllTextAsync(planPath, BadgeGatePlan);

        var mine = new ReviewLogWriter(planPath, new ReviewAuthor("Alice Ng", "alice@example.com"));
        var teammate = new ReviewLogWriter(planPath, new ReviewAuthor("Bob Chen", "bob@example.com"));

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(session, new ReviewServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            ReviewLog = mine,
        });

        try
        {
            var launched = await TryLaunchAsync();
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;

            await page.AddInitScriptAsync(RecordLogLoadsScript);
            await OpenBadgeGateAsync(page, server, session);
            await SeedTeammateLogAsync(page, teammate);

            // Opened by the reviewer's own gesture, which is what puts focus in the panel to begin with (#168),
            // and then walked to the first card the panel is actually showing.
            await EnsurePanelClosedAsync(page);
            await TabToAsync(page, "BUTTON[panel-toggle]");
            await page.Keyboard.PressAsync("Enter");
            Assert.Equal("DIV[panel]", await FocusIdentityAsync(page));

            var card = await FirstCardIdAsync(page);
            Assert.False(
                string.IsNullOrEmpty(card),
                "the panel showed no card to stand on, so the focus this test is about was never placed.");
            await TabForwardToAsync(page, "DIV[item]#" + card);

            var held = await HoldNextReviewLogReadAsync(page);
            await RereadTheLogAsync(page);
            await RemoveReviewDirectoryAsync(teammate.ReviewDirectory);

            AssertServedOutcomeIs(await held.TakeSnapshotAsync(), "unknown");

            var loadsBefore = await CountLogLoadsAsync(page);
            held.Deliver();
            await WaitForLogLoadAsync(page, loadsBefore + 1);

            // Re-read rather than held across the yield: a render sweeps the chrome away and rebuilds it, so a
            // handle taken before the answer landed answers for an element the page is no longer showing.
            await AssertFocusAsync(
                page, "DIV[item]#" + card,
                "a review-log view that said only that it could not read the directory landed while the " +
                    "reviewer was standing on a teammate's card (#221)");

            var report = await LastLogLoadAsync(page);
            Assert.True(
                await LastLogLoadDeclinedAsync(page, true),
                "focus is right but the SDK did not report DECLINING the unknown view, so it may simply " +
                    "never have re-rendered for an unrelated reason and this run proves nothing about " +
                    "#221's guard. The last review-log-loaded detail was " + report + ".");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            CleanupDirectory(directory);
        }
    }

    // ---- #221: declined OUT LOUD -------------------------------------------------------------------------

    /// <summary>
    /// Charter #221 — the decline is a structural fact, not an inference from the panel looking unchanged.
    ///
    /// <para>#209's guard reports <c>stale: true</c> for exactly this reason: a branch whose only evidence is
    /// that nothing visible happened is one no test can prove was taken, and it rots into a no-op the day
    /// something else keeps the panel intact. The mirror here is <c>declined</c> on the
    /// <c>review-log-loaded</c> detail, reported alongside the count the panel is STILL showing — a decline
    /// that announced zero would be the same lie one layer up.</para>
    ///
    /// <para>This test asserts the report and nothing else, so its failure names the missing report rather
    /// than the vanished panel its siblings are about.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "ReviewLogUnknownPanel")]
    public async Task The_declined_unknown_view_is_reported_out_loud()
    {
        var directory = NewPlanDirectory("unknown-panel-out-loud");
        var planPath = Path.Combine(directory, "unknown-out-loud.charter.md");
        await File.WriteAllTextAsync(planPath, BadgeGatePlan);

        var mine = new ReviewLogWriter(planPath, new ReviewAuthor("Alice Ng", "alice@example.com"));
        var teammate = new ReviewLogWriter(planPath, new ReviewAuthor("Bob Chen", "bob@example.com"));

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(session, new ReviewServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            ReviewLog = mine,
        });

        try
        {
            var launched = await TryLaunchAsync();
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;

            await page.AddInitScriptAsync(RecordLogLoadsScript);
            await OpenBadgeGateAsync(page, server, session);
            await SeedTeammateLogAsync(page, teammate);

            var held = await HoldNextReviewLogReadAsync(page);
            await RereadTheLogAsync(page);
            await RemoveReviewDirectoryAsync(teammate.ReviewDirectory);

            AssertServedOutcomeIs(await held.TakeSnapshotAsync(), "unknown");

            var loadsBefore = await CountLogLoadsAsync(page);
            held.Deliver();
            await WaitForLogLoadAsync(page, loadsBefore + 1);

            var report = await LastLogLoadAsync(page);
            Assert.True(
                await LastLogLoadDeclinedAsync(page, true),
                "Charter #221 — the SDK was handed a view that said only that the directory could not be " +
                    "read, and said nothing about what it did with it. The last review-log-loaded detail " +
                    "was " + report + ". #209's guard reports stale:true precisely so a test can assert the " +
                    "branch was taken; the equivalent here is declined:true.");

            // ...and it reports the count it KEPT, the way #209's decline reports state.annotations.length.
            // A decline that announced 0 would tell every listener the panel is empty while it is not.
            Assert.True(
                await LastLogLoadCountIsAsync(page, 2),
                "the decline was announced but reported the wrong count: " + report + ". A decline keeps the " +
                    "comments it already had, so the count it publishes is that kept number (2 here), never " +
                    "the zero the declined view carried.");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            CleanupDirectory(directory);
        }
    }

    // ---- #221: the anti-tautology control ----------------------------------------------------------------

    /// <summary>
    /// Charter #221's other half — a genuinely EMPTY view is a real state the panel must be able to show, and
    /// declining everything would pass all three tests above while breaking the thing they exist to protect.
    ///
    /// <para>The distinction is the server's, not a guess: <c>.review/</c> is left in place and only its logs
    /// are removed, so the read finds a directory that exists and holds nothing — <c>outcome: empty</c>, which
    /// this test asserts off the wire before it measures anything. The panel must then really empty.</para>
    ///
    /// <para>Its own red clause is the trailing one, and unavoidably so: today's SDK applies an empty view
    /// correctly and says nothing at all about having done it. <c>declined: false</c> is what makes the applied
    /// path as legible as the declined one, so the pair can never be told apart by silence.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "ReviewLogUnknownPanel")]
    public async Task A_genuinely_empty_view_still_empties_the_panel()
    {
        var directory = NewPlanDirectory("unknown-panel-empty-control");
        var planPath = Path.Combine(directory, "empty-control.charter.md");
        await File.WriteAllTextAsync(planPath, BadgeGatePlan);

        var mine = new ReviewLogWriter(planPath, new ReviewAuthor("Alice Ng", "alice@example.com"));
        var teammate = new ReviewLogWriter(planPath, new ReviewAuthor("Bob Chen", "bob@example.com"));

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(session, new ReviewServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            ReviewLog = mine,
        });

        try
        {
            var launched = await TryLaunchAsync();
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;

            await page.AddInitScriptAsync(RecordLogLoadsScript);
            await OpenBadgeGateAsync(page, server, session);
            await SeedTeammateLogAsync(page, teammate);

            // The directory STAYS. Only the logs go — every comment withdrawn, or a plan nobody has commented
            // on: the read looked, and there is nothing there.
            var held = await HoldNextReviewLogReadAsync(page);
            await RereadTheLogAsync(page);
            await RemoveReviewLogsAsync(teammate.ReviewDirectory);

            AssertServedOutcomeIs(await held.TakeSnapshotAsync(), "empty");

            var loadsBefore = await CountLogLoadsAsync(page);
            held.Deliver();
            await WaitForLogLoadAsync(page, loadsBefore + 1);

            var items = await PanelItemCountAsync(page);
            Assert.True(
                items == 0,
                "the review log really is empty and the panel is still showing " + items + " comment(s). A " +
                    "guard that declines every view is a worse bug than #221 and a silent one: a teammate's " +
                    "retraction would stop reaching the panel while the panel still looked right.");

            var report = await LastLogLoadAsync(page);
            Assert.True(
                await LastLogLoadDeclinedAsync(page, false),
                "the empty view was applied, but the SDK did not report APPLYING it: " + report + ". An " +
                    "applied read and a declined one must be told apart by what the SDK says, never by " +
                    "silence — otherwise the guard's own branch is unobservable and the day it starts " +
                    "declining everything, nothing here notices.");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            CleanupDirectory(directory);
        }
    }

    // ---- the interception --------------------------------------------------------------------------------

    /// <summary>
    /// Hold the page's NEXT <c>GET /api/review-log</c> at the network boundary and hand the test two switches:
    /// when the server may answer it, and when the browser may see the answer.
    ///
    /// <para>This is what turns a runner-dependent race into a fact. The answer is fetched from the real server
    /// at the moment <see cref="HeldReviewLogRead.TakeSnapshotAsync"/> is called and fulfilled unchanged, so
    /// what lands is a genuine response that genuinely could not read the directory — never a fabricated one.
    /// Only the two timings are the test's, which is also what keeps the field name honest: the server spells
    /// it, the SDK reads it, and this test is blind to neither.</para>
    ///
    /// <para>Later reads are passed straight through. Taking the directory away is itself a change the server
    /// watches for, so it pushes a review-log frame of its own; that read is answered by the same server with
    /// the same outcome, so letting it through alters nothing this test asserts and avoids inventing a network
    /// failure the page would have to explain.</para>
    /// </summary>
    private static async Task<HeldReviewLogRead> HoldNextReviewLogReadAsync(IPage page)
    {
        var held = new HeldReviewLogRead();
        var seen = 0;

        await page.RouteAsync("**/api/review-log?*", async route =>
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
                held.Answer.TrySetResult(await response.TextAsync());
                await held.MayDeliver.Task;
                await route.FulfillAsync(new RouteFulfillOptions { Response = response });
            }
            catch (Exception ex)
            {
                // A failed interception must surface as the assertion that needed it, not as a hang.
                held.Answer.TrySetException(ex);
            }
        });

        return held;
    }

    private sealed class HeldReviewLogRead
    {
        internal TaskCompletionSource MayAnswer { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource MayDeliver { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<string> Answer { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Let the server answer the held read NOW, and return the response it produced.</summary>
        internal async Task<string> TakeSnapshotAsync()
        {
            MayAnswer.TrySetResult();
            var done = await Task.WhenAny(Answer.Task, Task.Delay(ReadinessTimeoutMs));
            Assert.True(
                ReferenceEquals(done, Answer.Task),
                "the page never issued the GET /api/review-log that this test holds, so the window #221 " +
                    "lives in could not be opened.");
            return await Answer.Task;
        }

        /// <summary>Let the browser see it.</summary>
        internal void Deliver() => MayDeliver.TrySetResult();
    }

    // ---- the arrangement ---------------------------------------------------------------------------------

    /// <summary>
    /// Put two of a teammate's committed comments on the page, exactly as a <c>git pull</c> delivers them —
    /// written into the log beside the plan while the server runs, never posted through the page, so every
    /// card the panel is showing comes from the review log and nothing from this machine's pending queue.
    /// That is what makes an emptied review log an emptied PANEL.
    /// </summary>
    private static async Task SeedTeammateLogAsync(IPage page, ReviewLogWriter teammate)
    {
        var paragraph = await AnchorIdAsync(page, "body > p:nth-of-type(1)");
        var list = await AnchorIdAsync(page, "body > ul");
        Assert.False(
            string.IsNullOrEmpty(paragraph) || string.IsNullOrEmpty(list),
            "fixture/renderer drift: the badge-gate plan no longer renders an anchored paragraph and list at " +
                "the top level, so the teammate's comments would land on blocks that are not there and the " +
                "panel this test is about could never be populated.");

        teammate.AppendCreate(
            new ReviewAnchor(paragraph, "element", "an ordinary prose paragraph", null),
            "A teammate's note on the paragraph, and one of the two comments the panel must keep.");
        var second = teammate.AppendCreate(
            new ReviewAnchor(list, "element", "a plain bullet", null),
            "A second note, appended after the first, so a fold that has seen it has seen both.");

        // Wait on the LAST write's card: the watcher fires on the first append, so a re-read can be in flight
        // while the second record is still being written, and the panel renders from ONE fold.
        await WaitForCardAsync(page, second.Id, teammate);

        var items = await PanelItemCountAsync(page);
        Assert.True(
            items == 2,
            "the panel started this test with " + items + " comment(s) rather than the teammate's 2, so what " +
                "an unknown view does to a POPULATED panel was never under test.");
    }

    /// <summary>Ask the SDK to re-read the folded review log — the call every <c>review-log</c> frame makes.</summary>
    private static Task RereadTheLogAsync(IPage page)
        => page.EvaluateAsync("() => { window.CharterAnnotate.reviewLog(); return null; }");

    /// <summary>
    /// Take <c>.review/</c> away while a read is in flight — the branch switch, the <c>git clean</c>, or the
    /// <c>git pull</c> replacing the directory that #221 was reported from. Bounded and retried rather than
    /// attempted once: the server's own watcher and reads hold handles in that directory, and a transient
    /// sharing conflict is not a finding about the panel.
    /// </summary>
    private static async Task RemoveReviewDirectoryAsync(string reviewDirectory)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(ReadinessTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                Directory.Delete(reviewDirectory, recursive: true);
            }
            catch (IOException)
            {
                // The server is mid-read, or the OS still holds the watcher's handle. Try again.
            }
            catch (UnauthorizedAccessException)
            {
                // Likewise.
            }

            if (!Directory.Exists(reviewDirectory))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail(
            "the review directory at " + reviewDirectory + " could not be removed, so the read under test " +
            "was never made unreadable and nothing below measures #221.");
    }

    /// <summary>
    /// The other removal, and the one the control test needs: every log goes and the DIRECTORY STAYS, so the
    /// read finds a real directory holding nothing. That is <c>empty</c>, and the server tells the two apart.
    /// </summary>
    private static async Task RemoveReviewLogsAsync(string reviewDirectory)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(ReadinessTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var log in SafeLogs(reviewDirectory))
            {
                try
                {
                    File.Delete(log);
                }
                catch (IOException)
                {
                    // A concurrent read of the same file — retry on the next pass.
                }
                catch (UnauthorizedAccessException)
                {
                    // Likewise.
                }
            }

            if (Directory.Exists(reviewDirectory) && SafeLogs(reviewDirectory).Count == 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail(
            "the logs under " + reviewDirectory + " could not be removed while keeping the directory, so the " +
            "genuinely-empty read this control needs never happened.");
    }

    private static IReadOnlyList<string> SafeLogs(string reviewDirectory)
    {
        try
        {
            return Directory.GetFiles(reviewDirectory, ReviewLogPaths.LogSearchPattern);
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    // ---- the measurements --------------------------------------------------------------------------------

    /// <summary>
    /// What the SERVER actually answered, asserted before anything downstream of it is measured.
    ///
    /// <para>Two premises, each a way this test could pass while proving nothing: the answer really did carry
    /// the outcome this test needs (or the SDK was never handed the case under test), and it really did carry
    /// zero comments (or applying it would empty nothing and every assertion below would be vacuous).</para>
    /// </summary>
    private static void AssertServedOutcomeIs(string served, string expected)
    {
        using var document = JsonDocument.Parse(served);
        var root = document.RootElement;

        var outcome = root.TryGetProperty("outcome", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        Assert.True(
            string.Equals(outcome, expected, StringComparison.Ordinal),
            "the intercepted /api/review-log answer carried outcome '" + (outcome ?? "(absent or not a " +
                "string)") + "', not '" + expected + "'. This is the seam between the two halves of the fix: " +
                "the server names the field on a C# record and the SDK reads its camelCase name off the " +
                "wire, so an answer that does not spell it this way means the browser can never see it. The " +
                "served answer was: " + served);

        var comments = root.TryGetProperty("comments", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.GetArrayLength()
            : -1;
        Assert.True(
            comments == 0,
            "the intercepted answer carried " + comments + " comment(s), so applying it would empty nothing " +
                "and the panel this test measures could not change either way.");
    }

    /// <summary>How many comment cards the panel is showing, re-resolved on every call.</summary>
    private static Task<int> PanelItemCountAsync(IPage page)
        => page.Locator(Ui("item")).CountAsync();

    /// <summary>The annotation id of the first card in the panel — the one a Tab walk reaches first.</summary>
    private static Task<string> FirstCardIdAsync(IPage page)
        => page.EvaluateAsync<string>(
            "() => { const i = document.querySelector('[data-charter-ui=\"item\"]');" +
            "  return i ? (i.getAttribute('data-annotation-id') || '') : ''; }");

    /// <summary>
    /// Record every <c>review-log-loaded</c> the SDK emits, with the two fields that say what it DID with the
    /// view. Captured raw, never coerced: a missing <c>declined</c> field and an explicit <c>false</c> mean
    /// completely different things — the first is a guard that does not exist — and a <c>!!</c> here would
    /// silently turn one into the other.
    /// </summary>
    private const string RecordLogLoadsScript =
        "window.__charterLogLoads = [];" +
        "window.addEventListener('message', function (e) {" +
        "  if (e && e.data && e.data.channel === 'charter-annotate' && e.data.type === 'review-log-loaded') {" +
        "    var d = e.data.detail || {};" +
        "    window.__charterLogLoads.push({ declined: d.declined, count: d.count });" +
        "  }" +
        "});";

    private static Task<int> CountLogLoadsAsync(IPage page)
        => page.EvaluateAsync<int>("() => (window.__charterLogLoads || []).length");

    /// <summary>The last report, as JSON, for a failure message. A field the SDK never set is simply absent.</summary>
    private static Task<string> LastLogLoadAsync(IPage page)
        => page.EvaluateAsync<string>(
            "() => { const l = window.__charterLogLoads || [];" +
            "  return l.length ? JSON.stringify(l[l.length - 1]) : '(no review-log-loaded event at all)'; }");

    private static Task<bool> LastLogLoadDeclinedAsync(IPage page, bool declined)
        => page.EvaluateAsync<bool>(
            "(want) => { const l = window.__charterLogLoads || [];" +
            "  const last = l.length ? l[l.length - 1] : null;" +
            "  return !!last && last.declined === want; }",
            declined);

    private static Task<bool> LastLogLoadCountIsAsync(IPage page, int count)
        => page.EvaluateAsync<bool>(
            "(want) => { const l = window.__charterLogLoads || [];" +
            "  const last = l.length ? l[l.length - 1] : null;" +
            "  return !!last && last.count === want; }",
            count);

    /// <summary>
    /// Wait until the held answer has reached the SDK. A readiness gate like every other, so it takes the
    /// suite's one deadline rather than a local literal, and it polls the SDK's own event tap rather than
    /// evaluating a predicate inside the page — the served page's CSP has no unsafe-eval, so a polling
    /// predicate throws the moment it genuinely has to wait.
    /// </summary>
    private static async Task WaitForLogLoadAsync(IPage page, int target, int timeoutMs = ReadinessTimeoutMs)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await CountLogLoadsAsync(page) >= target)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail(
            "the delivered review-log answer never reached the SDK within " + timeoutMs + "ms: " +
            await CountLogLoadsAsync(page) + " 'review-log-loaded' event(s) seen, expected " + target +
            ". A guard that declines a view SILENTLY — returning without emitting — reaches this too: the " +
            "decline must still be announced, which is the whole point of #209's stale:true.");
    }
}
