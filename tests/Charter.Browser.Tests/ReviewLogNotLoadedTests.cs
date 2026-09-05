using System.Net;
using Charter.Core;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #221, the half that lives entirely in the browser — a review log the SDK has NOT READ YET is
/// indistinguishable, in <c>state.log</c>, from one it read and found empty.
///
/// <para><b>The defect, in two lines of <c>sdk/charter-annotate.js</c>.</b> <c>state.log</c> is initialised to
/// <c>{ comments: [], diagnostics: [], unreadable: [], selfEmail: null }</c> — a literal empty log, byte-for-byte
/// what a server that answered "nobody has commented" produces. And the SSE handler is
/// <c>es.addEventListener('review-log', function () { emit('review-log-changed', {}); hydrateLog(); })</c>:
/// the emit is synchronous, <c>hydrateLog()</c> is fire-and-forget, and nothing awaits it. Every render that
/// happens before the first answer lands therefore runs against that literal and concludes, with complete
/// confidence, that the review log holds nothing.</para>
///
/// <para><b>Why the fix tasks 01-08 shipped does not reach this.</b> That pair made an unreadable directory a
/// third OUTCOME on the wire and taught <c>hydrateLog</c> to decline it. Here there is no answer yet to decline
/// — no <c>outcome</c>, no response, nothing on the wire at all. Its guard is also explicitly conditioned on
/// <c>holdingAReadLog()</c>, which is FALSE for exactly the state this file is about, because the initial
/// literal is empty. So the two fixes cannot be each other: one is about an answer that says "I could not
/// tell", this one is about there being no answer yet.</para>
///
/// <para><b>What that costs a reviewer, concretely.</b> Charter's own rule is that a retract HIDES a comment's
/// body and KEEPS its thread — <c>RerenderFocusTests</c> records the finding that a retracted note therefore
/// does NOT leave the panel, which is why <c>ITEM_GONE</c> could not be reached that way at all. So a reviewer
/// who withdraws their own note keeps its card, keeps their place in the list, and is told nothing. Unless the
/// log has not been read yet: then <c>mergedRecords()</c> sees the pending note removed and the durable thread
/// invisible, the card is destroyed with no counterpart built, focus lands on <c>&lt;body&gt;</c>, and the SDK
/// says <i>"That note is no longer in the list"</i> — about a note that is, in the log, right where they left
/// it. That is #221's captured trace: <c>focus-not-restored key=item:cmt_… built=false panelHidden=false
/// items=0</c>.</para>
///
/// <para><b>How the window becomes a fact rather than a race.</b> <see cref="HoldEveryReviewLogReadAsync"/> is
/// <c>StaleQueueReadTests</c>' <c>HoldFirstQueueReadAsync</c> shape, retargeted at <c>/api/review-log</c> and
/// held OPEN: every read the page issues waits at the Playwright route boundary until the test releases it, so
/// "the log has never been read" lasts exactly as long as the test needs instead of however long a contended
/// runner happens to take. Holding all of them rather than only the first is load-bearing — a dual-written
/// record makes the server's watcher push a <c>review-log</c> frame, and a single pass-through read would close
/// the window before the measurement.</para>
///
/// <para><b>Nothing is fabricated.</b> The reads are fulfilled with <c>Response = response</c> — the real
/// server's own answer, fetched at RELEASE time. A hand-written body could not see whether the server and the
/// SDK agree about what they are exchanging, and would pass just as happily against a server that never emits
/// the field.</para>
///
/// <para><b>The structural fact these tests need.</b> "The panel looked right" is equally true of a page that
/// never renders and of one that renders correctly, so the branch under test has to be legible from outside.
/// #209 reports <c>stale: true|false</c> on <c>list-loaded</c>; #221's first half reports
/// <c>declined: true|false</c> on <c>review-log-loaded</c>. The mirror here is on <c>markers-rendered</c>,
/// because the question belongs to the RENDER and not to a load: every render says whether the review log it
/// drew from had been read (<c>logLoaded</c>). Asserted STRICTLY against <c>true</c>/<c>false</c>, so an absent
/// field — today's state, where the client simply has no such notion — fails rather than being coerced into
/// one of the two answers.</para>
///
/// <para>In the two symptom tests that fact is asserted AFTER the symptom, deliberately, so the pre-fix failure
/// is a report about the reviewer being told their note vanished rather than about the fix's instrumentation.
/// </para>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    // ---- #221: an unread log is not an empty one -----------------------------------------------------------

    /// <summary>
    /// Charter #221 — the sentence. A reviewer withdraws their own note while the review log has never been
    /// read, and the panel tells them it is gone from the list. It is not: the withdrawal was dual-written to
    /// <c>&lt;plan&gt;.review/</c> as a retract, and a retract keeps the thread. The SDK is reporting an
    /// absence it has not looked for.
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "ReviewLogNotLoaded")]
    public async Task A_render_before_the_first_log_load_does_not_report_a_note_gone()
    {
        var directory = NewPlanDirectory("log-not-loaded-sentence");
        var planPath = Path.Combine(directory, "not-loaded-sentence.charter.md");
        await File.WriteAllTextAsync(planPath, BadgeGatePlan);

        var mine = new ReviewLogWriter(planPath, new ReviewAuthor("Alice Ng", "alice@example.com"));

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(session, new ReviewServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            // The writer is what makes the durable half real: every create the page posts is dual-written to
            // `<plan>.review/`, and every delete appends a retract there. Without it the note really WOULD be
            // gone and the sentence below would be true.
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
            await page.AddInitScriptAsync(RecordLogRendersScript);

            // Installed BEFORE the navigation, so init()'s own hydrateLog() is the first read caught.
            var held = await HoldEveryReviewLogReadAsync(page);
            await OpenBadgeGateAsync(page, server, session);

            var anchorId = await AnchorIdAsync(page, "body > p:nth-of-type(1)");
            Assert.False(
                string.IsNullOrEmpty(anchorId),
                "fixture/renderer drift: the badge-gate plan no longer renders an anchored paragraph at the " +
                    "top level, so the reviewer's note would land on a block that is not there.");

            await SeedNotesAsync(page, anchorId, 1, "the reviewer's own note, withdrawn below");

            await AssertTheLogIsStillUnreadAsync(page, held, "before the reviewer stands on their card");

            var standing = await PanelItemCountAsync(page);
            Assert.True(
                standing == 1,
                "the panel is showing " + standing + " card(s) rather than the reviewer's 1, so the card this " +
                    "test withdraws is not the one it thinks it is.");

            // Opened by the reviewer's own gesture, which is what puts focus in the panel to begin with (#168),
            // and then walked to the card with real Tab presses — the rule the whole #200 family is built on.
            await EnsurePanelClosedAsync(page);
            await TabToAsync(page, "BUTTON[panel-toggle]");
            await page.Keyboard.PressAsync("Enter");
            Assert.Equal("DIV[panel]", await FocusIdentityAsync(page));

            var card = await FirstCardIdAsync(page);
            Assert.False(
                string.IsNullOrEmpty(card),
                "the panel showed no card to stand on, so the render this test is about was never provoked.");
            await TabForwardToAsync(page, "DIV[item]#" + card);

            await RetractThroughThePageAsync(page, card);

            // The premise, re-asserted on the far side of the gesture: nothing slipped a read through while the
            // delete was in flight, so the render that just happened really did run on an UNREAD log.
            await AssertTheLogIsStillUnreadAsync(page, held, "after the note was withdrawn");

            // THE assertion. Re-read after the last await and never held across it: the render swept the panel
            // away and rebuilt it, so a locator resolved earlier answers for chrome the page no longer shows.
            var told = await PanelStatusAsync(page);
            Assert.False(
                told.Contains("no longer in the list", StringComparison.OrdinalIgnoreCase),
                "Charter #221 — the reviewer withdrew their own note and the panel told them: '" + told +
                    "'. The note is not gone. The withdrawal was dual-written to the review log as a retract, " +
                    "and a retract hides a comment's body and KEEPS its thread, so the card belongs in the " +
                    "list and will be back in it the moment the log is read. The SDK has never read the log " +
                    "on this page — its state.log is still the empty literal it was initialised to — and it " +
                    "cannot tell that apart from a log it read and found empty, so it reported an absence it " +
                    "never looked for.");

            // ...and last, the render's own report, so a page that stays quiet for some unrelated reason is
            // still caught. Deliberately AFTER the sentence: a premise only the fix can satisfy would make the
            // pre-fix failure a statement about the fix's instrumentation rather than about the reviewer.
            var report = await LastLogRenderAsync(page);
            Assert.True(
                await LastLogRenderSaysLoadedAsync(page, false),
                "the panel said nothing wrong, but the SDK did not report that this render ran on an UNREAD " +
                    "review log, so nothing here proves the distinction exists. The last markers-rendered " +
                    "detail was " + report + " (a missing 'logLoaded' field means the client still has no way " +
                    "to say 'not loaded yet', which is #221's whole subject). #209 reports stale:true|false " +
                    "for the same reason: a branch whose only evidence is that nothing visible happened is " +
                    "one no test can prove was taken.");

            // Let the page finish: the held reads answer, the log lands, and nothing is left hanging at the
            // route boundary while the context closes.
            await ReleaseAndSettleAsync(page, held);
            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            CleanupDirectory(directory);
        }
    }

    // ---- #221: ...and the claim on the wire ----------------------------------------------------------------

    /// <summary>
    /// Charter #221 — the same moment, measured where CI measured it. <c>restoreChromeFocus</c> emits
    /// <c>focus-not-restored</c> with <c>built: false</c>, which is a definite claim: <i>no counterpart was
    /// built for what you were standing on</i>. On an unread log that claim is not something the SDK is in a
    /// position to make — the counterpart is in a file it has not opened.
    ///
    /// <para>Separate from its sibling because the two rot independently: a fix that only softened the panel's
    /// wording would leave the wire saying the note vanished, and every post-mortem reading the wire — which is
    /// exactly how #221 was investigated — would be told the same wrong thing.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "ReviewLogNotLoaded")]
    public async Task Focus_is_not_reported_unrestorable_while_the_log_is_unloaded()
    {
        var directory = NewPlanDirectory("log-not-loaded-focus");
        var planPath = Path.Combine(directory, "not-loaded-focus.charter.md");
        await File.WriteAllTextAsync(planPath, BadgeGatePlan);

        var mine = new ReviewLogWriter(planPath, new ReviewAuthor("Alice Ng", "alice@example.com"));

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
            await page.AddInitScriptAsync(RecordLogRendersScript);

            var held = await HoldEveryReviewLogReadAsync(page);
            await OpenBadgeGateAsync(page, server, session);

            var anchorId = await AnchorIdAsync(page, "body > p:nth-of-type(1)");
            Assert.False(
                string.IsNullOrEmpty(anchorId),
                "fixture/renderer drift: the badge-gate plan no longer renders an anchored paragraph at the " +
                    "top level.");

            await SeedNotesAsync(page, anchorId, 1, "the reviewer's own note, withdrawn below");

            await AssertTheLogIsStillUnreadAsync(page, held, "before the reviewer stands on their card");

            await EnsurePanelClosedAsync(page);
            await TabToAsync(page, "BUTTON[panel-toggle]");
            await page.Keyboard.PressAsync("Enter");
            Assert.Equal("DIV[panel]", await FocusIdentityAsync(page));

            var card = await FirstCardIdAsync(page);
            Assert.False(
                string.IsNullOrEmpty(card),
                "the panel showed no card to stand on, so the focus this test is about was never placed.");
            await TabForwardToAsync(page, "DIV[item]#" + card);
            Assert.Equal("DIV[item]#" + card, await FocusIdentityAsync(page));

            // The count is taken from a baseline rather than asked "has this ever happened": that question is
            // answered instantly by anything earlier in the page's life and is not a measurement at all.
            var claimsBefore = await CountEventsAsync(page, "focus-not-restored");

            await RetractThroughThePageAsync(page, card);
            await AssertTheLogIsStillUnreadAsync(page, held, "after the note was withdrawn");

            // THE assertion, and the one that reproduces #221's CI trace verbatim: pre-fix this fires once,
            // with built=false, items=0 and panelHidden=false.
            var claims = await CountEventsAsync(page, "focus-not-restored");
            var trace = await page.EvaluateAsync<string[]>("() => window.__charterFocusTrace || []");
            Assert.True(
                claims == claimsBefore,
                "Charter #221 — the SDK reported that the reviewer's card could not be restored, on a page " +
                    "whose review log it has never read. 'built: false' says no counterpart was built; the " +
                    "counterpart is a retract record in <plan>.review/ that this page has not opened, so the " +
                    "SDK is answering a question it has not asked. Focus events on the wire:" +
                    Environment.NewLine + "  " +
                    (trace.Length == 0 ? "(none)" : string.Join(Environment.NewLine + "  ", trace)));

            var report = await LastLogRenderAsync(page);
            Assert.True(
                await LastLogRenderSaysLoadedAsync(page, false),
                "the wire is quiet, but the SDK did not report that this render ran on an UNREAD review log, " +
                    "so it may simply not have re-rendered at all and this run proves nothing about #221's " +
                    "window. The last markers-rendered detail was " + report + ".");

            await ReleaseAndSettleAsync(page, held);
            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            CleanupDirectory(directory);
        }
    }

    // ---- #221: and the window CLOSES ------------------------------------------------------------------------

    /// <summary>
    /// Charter #221's other direction — "not loaded yet" is a state to leave, not a state to live in.
    ///
    /// <para>Everything above is satisfied by an SDK that treats the log as permanently unread and never draws
    /// a single teammate comment, which is a worse bug and a silent one: the whole point of the folded log is
    /// that a teammate's committed note reaches the panel. So the held reads are released with two of a
    /// teammate's comments waiting behind them, and the panel must show both — and must say that the render it
    /// did that with was drawn from a log it had READ.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "ReviewLogNotLoaded")]
    public async Task The_panel_renders_its_entries_once_the_log_loads()
    {
        var directory = NewPlanDirectory("log-not-loaded-then-loads");
        var planPath = Path.Combine(directory, "not-loaded-then-loads.charter.md");
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
            await page.AddInitScriptAsync(RecordLogRendersScript);

            var held = await HoldEveryReviewLogReadAsync(page);
            await OpenBadgeGateAsync(page, server, session);

            // Committed beside the plan exactly as a `git pull` delivers them — never posted through the page,
            // so every card the panel ends up showing came from the review log and none from a pending queue.
            // The waits SeedTeammateLogAsync uses are not available here: they wait on the card, and the whole
            // arrangement is that no read may land until this test says so.
            var paragraph = await AnchorIdAsync(page, "body > p:nth-of-type(1)");
            var list = await AnchorIdAsync(page, "body > ul");
            Assert.False(
                string.IsNullOrEmpty(paragraph) || string.IsNullOrEmpty(list),
                "fixture/renderer drift: the badge-gate plan no longer renders an anchored paragraph and list " +
                    "at the top level, so the teammate's comments would land on blocks that are not there.");

            teammate.AppendCreate(
                new ReviewAnchor(paragraph, "element", "an ordinary prose paragraph", null),
                "A teammate's note on the paragraph, waiting behind a read this page has not been allowed.");
            teammate.AppendCreate(
                new ReviewAnchor(list, "element", "a plain bullet", null),
                "A second note, so a fold that has seen one has seen both.");

            await AssertTheLogIsStillUnreadAsync(page, held, "with both of the teammate's comments committed");

            var before = await PanelItemCountAsync(page);
            Assert.True(
                before == 0,
                "the panel is already showing " + before + " card(s) before any review-log read has landed, " +
                    "so what a FIRST load does to it is not what this test would measure.");

            // Released now, which is also when the real server is asked: what lands is a genuine answer to a
            // read taken after both records were on disk, never a body this test wrote.
            var loadsBefore = await CountLogLoadsAsync(page);
            held.Release();
            await WaitForLogLoadAsync(page, loadsBefore + 1);

            var items = await PanelItemCountAsync(page);
            Assert.True(
                items == 2,
                "the review log holds the teammate's 2 comments and the panel is showing " + items + " after " +
                    "the load landed. An SDK that treats an unread log as permanently unreadable passes every " +
                    "other test in this file and breaks the thing they exist to protect: a teammate's " +
                    "committed note would stop reaching the panel while the panel still looked right. The " +
                    "last review-log-loaded detail was " + await LastLogLoadAsync(page) + ".");

            var report = await LastLogRenderAsync(page);
            Assert.True(
                await LastLogRenderSaysLoadedAsync(page, true),
                "the entries rendered, but the SDK did not report that this render was drawn from a log it " +
                    "had READ: " + report + ". A read log and an unread one must be told apart by what the " +
                    "SDK says, never by silence — otherwise the day it starts calling every log unread, the " +
                    "two tests above pass and nothing notices.");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            CleanupDirectory(directory);
        }
    }

    // ---- #221: the anti-tautology control -------------------------------------------------------------------

    /// <summary>
    /// Charter #221 — a log that was READ and is genuinely empty is a real state the panel must be able to
    /// show, and it is the state "not loaded yet" is byte-identical to. If the two cannot be told apart in the
    /// direction that matters here, they cannot be told apart at all.
    ///
    /// <para>The distinction is the SERVER's, not a guess: <c>.review/</c> is left in place and only its logs
    /// are removed, so the read finds a directory that exists and holds nothing — <c>outcome: empty</c>, which
    /// this test asserts off the wire before it measures anything downstream of it.</para>
    ///
    /// <para>Without this row, "never conclude anything about the log" passes every other row in the file: an
    /// SDK permanently in its not-loaded state reports no note gone, refuses no focus, and — with the row above
    /// satisfied by a single load — could still decline to ever reach zero again. A plan whose comments were
    /// all withdrawn, or one nobody has commented on, must still render as the empty review it is.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Feature", "ReviewLogNotLoaded")]
    public async Task A_loaded_and_genuinely_empty_log_still_renders_as_empty()
    {
        var directory = NewPlanDirectory("log-not-loaded-empty-control");
        var planPath = Path.Combine(directory, "not-loaded-empty-control.charter.md");
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
            await page.AddInitScriptAsync(RecordLogRendersScript);

            var held = await HoldEveryReviewLogReadAsync(page);
            await OpenBadgeGateAsync(page, server, session);

            var paragraph = await AnchorIdAsync(page, "body > p:nth-of-type(1)");
            Assert.False(
                string.IsNullOrEmpty(paragraph),
                "fixture/renderer drift: the badge-gate plan no longer renders an anchored paragraph at the " +
                    "top level.");

            // Written and then removed, which is how the directory comes to EXIST and hold nothing — a review
            // whose comments were all withdrawn, or one that a teammate started and cleaned up. Nothing here
            // creates `.review/` eagerly, so a record has to be written to bring it into being at all.
            teammate.AppendCreate(
                new ReviewAnchor(paragraph, "element", "an ordinary prose paragraph", null),
                "A note whose whole job is to bring `.review/` into existence before it is emptied again.");
            await RemoveReviewLogsAsync(teammate.ReviewDirectory);

            await AssertTheLogIsStillUnreadAsync(page, held, "with the review directory present and empty");

            var loadsBefore = await CountLogLoadsAsync(page);
            held.Release();
            await WaitForLogLoadAsync(page, loadsBefore + 1);

            // The premise, asserted off the wire: the server really did look and really did find nothing, so
            // what lands is `empty` and not the `unknown` the sibling pair is about.
            AssertServedOutcomeIs(held.Served, "empty");

            var items = await PanelItemCountAsync(page);
            Assert.True(
                items == 0,
                "the review log really is empty and the panel is showing " + items + " card(s). The last " +
                    "review-log-loaded detail was " + await LastLogLoadAsync(page) + ".");

            var empty = await page.Locator(Ui("panel-empty")).CountAsync();
            Assert.True(
                empty == 1,
                "the panel showed neither cards nor its empty state, so it is in neither of the two states a " +
                    "reviewer can read. An empty review is a finished one as often as it is an untouched one, " +
                    "and 'nothing here' is the sentence for both — a page that renders neither leaves them " +
                    "unable to tell whether Charter is working.");

            var report = await LastLogRenderAsync(page);
            Assert.True(
                await LastLogRenderSaysLoadedAsync(page, true),
                "the panel is empty and the SDK did not report that it got there from a log it had READ: " +
                    report + ". This is #221 in one line — an unread log and an empty one produce the same " +
                    "zero, and the whole fix is that they must stop being the same thing. If a genuinely " +
                    "empty log still reports itself unread, the guard the two tests above demand can never " +
                    "be released and the panel is stuck in a state it can never leave.");

            AssertNoBrowserErrors(instrumented);
        }
        finally
        {
            CleanupDirectory(directory);
        }
    }

    // ---- the interception -----------------------------------------------------------------------------------

    /// <summary>
    /// Hold EVERY <c>GET /api/review-log</c> the page issues at the network boundary, and hand the test one
    /// switch: when the reads may happen at all.
    ///
    /// <para>This is what turns "the fetch had not come back yet" — a property of whichever runner the suite
    /// lands on — into a fact the test states. <see cref="HoldNextReviewLogReadAsync"/> holds only the FIRST
    /// read because that is the window its own pair needs; here the window has to stay open across a POST and a
    /// delete, each of which dual-writes a record that the server's watcher reports, and each report brings
    /// another read. One pass-through read closes the window before anything is measured.</para>
    ///
    /// <para>The answers are FETCHED at release, not at interception, so what lands is the real server's real
    /// view of the directory as it stands then — never a body this test invented, and never a stale one. A
    /// fabricated body could not see whether the server and the SDK agree about what they are exchanging.</para>
    /// </summary>
    private static async Task<HeldReviewLogReads> HoldEveryReviewLogReadAsync(IPage page)
    {
        var held = new HeldReviewLogReads();

        await page.RouteAsync("**/api/review-log?*", async route =>
        {
            held.Arrived();
            try
            {
                await held.MayAnswer.Task;
                var response = await route.FetchAsync();
                held.Answered(await response.TextAsync());
                await route.FulfillAsync(new RouteFulfillOptions { Response = response });
            }
            catch (Exception ex)
            {
                // A failed interception must surface in the assertion that needed it rather than as a hang,
                // and it must not be raised from here: this runs on Playwright's dispatcher, where a throw is
                // reported as an unrelated route error long after the test has moved on.
                held.Failed(ex);
            }
        });

        return held;
    }

    private sealed class HeldReviewLogReads
    {
        private readonly object _gate = new();
        private string? _served;
        private string? _failure;
        private int _arrived;

        internal TaskCompletionSource MayAnswer { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>How many reads the page has issued into the hold — the proof the window was ever real.</summary>
        internal int Count => Volatile.Read(ref _arrived);

        internal void Arrived() => Interlocked.Increment(ref _arrived);

        internal void Answered(string body)
        {
            lock (_gate)
            {
                _served = body;
            }
        }

        internal void Failed(Exception ex)
        {
            lock (_gate)
            {
                _failure ??= ex.Message;
            }
        }

        /// <summary>The last genuine answer the server produced, for assertions about the wire.</summary>
        internal string Served
        {
            get
            {
                lock (_gate)
                {
                    return _served
                        ?? "(no /api/review-log answer ever reached the browser" +
                           (_failure is null ? ")" : "; the interception failed with: " + _failure + ")");
                }
            }
        }

        internal string? Failure
        {
            get
            {
                lock (_gate)
                {
                    return _failure;
                }
            }
        }

        /// <summary>Let every held read — and every later one — go through to the real server.</summary>
        internal void Release() => MayAnswer.TrySetResult();
    }

    // ---- the arrangement ------------------------------------------------------------------------------------

    /// <summary>
    /// The premise every measurement in this file rests on: the SDK has been given no answer about the review
    /// log at all, and has been asking. Both halves matter — zero loads on a page that never asked would mean
    /// the hold caught nothing and the window under test is somebody else's.
    /// </summary>
    private static async Task AssertTheLogIsStillUnreadAsync(IPage page, HeldReviewLogReads held, string moment)
    {
        var loads = await CountLogLoadsAsync(page);
        Assert.True(
            loads == 0,
            "the SDK has already loaded the review log " + loads + " time(s) " + moment + ", so state.log is " +
                "no longer the empty literal it was initialised to and nothing below measures #221's window." +
                (held.Failure is null ? "" : " The interception also failed with: " + held.Failure + "."));

        Assert.True(
            held.Count >= 1,
            "no GET /api/review-log was ever held " + moment + ", so the page is not asking for the log at " +
                "all and the window this test opens is not the one the SDK lives in. Either the route pattern " +
                "no longer matches the SDK's URL, or hydrateLog() is no longer called on init.");
    }

    /// <summary>
    /// Withdraw a note through the SDK's own path — the programmatic twin of the panel's delete, which
    /// dual-writes a retract to <c>&lt;plan&gt;.review/</c> and removes the note from the pre-drain queue.
    ///
    /// <para>Gated on the <c>annotation-deleted</c> COUNT from a baseline, never on "has this ever happened":
    /// that question is answered instantly by anything earlier in the page's life, which is the trap
    /// <see cref="WaitForEventCountAsync"/> exists for.</para>
    /// </summary>
    private static async Task RetractThroughThePageAsync(IPage page, string id)
    {
        var before = await CountEventsAsync(page, "annotation-deleted");
        await page.EvaluateAsync(
            "(id) => { window.postMessage(" +
            "  { channel: 'charter-annotate', type: 'delete', detail: { id: id } }, '*'); return null; }",
            id);
        await WaitForEventCountAsync(page, "annotation-deleted", before + 1, atLeast: true);
    }

    /// <summary>
    /// Let the held reads answer and wait for one to land — cleanup, not a measurement, so it runs after every
    /// assertion. A route handler still parked at the boundary when the context closes is a hang looking for a
    /// runner to happen on, and the page recovering is worth seeing anyway.
    /// </summary>
    private static async Task ReleaseAndSettleAsync(IPage page, HeldReviewLogReads held)
    {
        var loadsBefore = await CountLogLoadsAsync(page);
        held.Release();
        await WaitForLogLoadAsync(page, loadsBefore + 1);
    }

    // ---- the measurements -----------------------------------------------------------------------------------

    /// <summary>
    /// The sentence the panel is showing a reviewer, read from the live document rather than from a locator
    /// resolved earlier: a render destroys the SDK's chrome and rebuilds it, so a handle taken before the
    /// render answers for an element the page is no longer showing — and a detached one reports an empty
    /// string, which is exactly the answer that would make this assertion pass for the wrong reason (#198).
    ///
    /// <para><c>textContent</c> and not <c>innerText</c>, because the status line is <c>display: none</c>
    /// whenever it carries nothing and an unrendered element has no innerText to report.</para>
    /// </summary>
    private static Task<string> PanelStatusAsync(IPage page)
        => page.EvaluateAsync<string>(
            "() => { const s = document.querySelector('[data-charter-ui=\"panel-status\"]');" +
            "  return s ? (s.textContent || '') : ''; }");

    /// <summary>
    /// Record what every render says about the log it drew from.
    ///
    /// <para><c>markers-rendered</c> and not <c>review-log-loaded</c>, deliberately: the question is about a
    /// RENDER, and the renders this file is about are the ones that happen when no load has occurred — there is
    /// no <c>review-log-loaded</c> to hang the answer on, which is the defect stated as a wiring problem.</para>
    ///
    /// <para>Captured RAW, never coerced. A missing <c>logLoaded</c> and an explicit <c>false</c> mean
    /// completely different things — the first is a client with no notion of "not loaded yet", which is what
    /// #221 is — and a <c>!!</c> here would silently turn the first into the second and certify nothing.</para>
    /// </summary>
    private const string RecordLogRendersScript =
        "window.__charterLogRenders = [];" +
        "window.addEventListener('message', function (e) {" +
        "  if (e && e.data && e.data.channel === 'charter-annotate' && e.data.type === 'markers-rendered') {" +
        "    var d = e.data.detail || {};" +
        "    window.__charterLogRenders.push({ logLoaded: d.logLoaded, blocks: d.blocks });" +
        "  }" +
        "});";

    /// <summary>The last render's report, as JSON, for a failure message. A field never set is simply absent.</summary>
    private static Task<string> LastLogRenderAsync(IPage page)
        => page.EvaluateAsync<string>(
            "() => { const r = window.__charterLogRenders || [];" +
            "  return r.length ? JSON.stringify(r[r.length - 1]) : '(no markers-rendered event at all)'; }");

    private static Task<bool> LastLogRenderSaysLoadedAsync(IPage page, bool loaded)
        => page.EvaluateAsync<bool>(
            "(want) => { const r = window.__charterLogRenders || [];" +
            "  const last = r.length ? r[r.length - 1] : null;" +
            "  return !!last && last.logLoaded === want; }",
            loaded);
}
