using System;
using System.IO;
using System.Threading;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Charter #88. The <c>/events</c> stream arms its <c>.review/</c> watch ONCE, at connect, so a chain that
/// fails to arm leaves the panel blind for the whole life of that connection — a teammate's pulled log
/// silently never refreshes it. These pin the two things that make the chain trustworthy: the arming ORDER
/// (which removes the window the arm could be lost in) and the keep-alive re-check (which bounds every
/// residual, because <see cref="FileSystemWatcher"/> is best-effort by contract and drops notifications).
/// </summary>
/// <remarks>
/// Deliberately at the <see cref="ReviewLogWatch"/> seam rather than over HTTP: the failure is "the watch
/// never armed", and asserting that through an SSE frame can only ever say "no frame arrived within N
/// seconds" — exactly the flaky shape #88 was filed about. The stream-level behaviour keeps its own test
/// (<c>ReviewLogPanelTests.ALogArrivingWhileTheServerRuns…</c>).
/// </remarks>
[Trait("Category", "ReviewLogWatch")]
public class ReviewLogWatchTests : IDisposable
{
    // Bounded, and generous enough that a contended runner is not the thing under test. Every wait below is on
    // an EVENT, never a sleep — a passing run takes milliseconds.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "charter-watch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is harmless.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The ordinary arrival: the watch is created beside a plan nobody has commented on (so <c>.review/</c>
    /// does not exist yet — §5.0), and a teammate's log lands.
    /// </summary>
    [Fact]
    public void ALogArrivingAfterTheWatchIsCreated_ArmsTheWatchThenRaisesOnChange()
    {
        var review = PlanReviewDirectory();
        var box = new WatchBox();
        using var armedWhenTold = new ManualResetEventSlim(false);
        using var watch = new ReviewLogWatch(review, () => box.SetIfArmed(armedWhenTold));
        box.Watch = watch;

        Assert.False(watch.IsArmed);

        WriteLog(review, "bob.deadbeef.jsonl");

        Assert.True(
            armedWhenTold.Wait(Budget),
            "the watch must notice a log arriving in a `.review/` created after it");
    }

    /// <summary>
    /// The arming-ORDER regression. The shipped code looked for <c>.review/</c> first and armed the
    /// directory-name bridge <b>only if it was missing</b> — so a watch created while the directory already
    /// existed had no bridge at all, and a <c>.review/</c> removed and restored (a branch switch, a
    /// <c>git clean</c>) left the panel blind with nothing able to re-arm it. Arming the bridge
    /// unconditionally, and FIRST, is what closes the window the flake lived in; this is its observable
    /// consequence.
    /// </summary>
    [Fact]
    public void AReviewDirectoryRemovedAndRestored_ReArmsTheWatchBeforeAnnouncingTheChange()
    {
        var review = PlanReviewDirectory();
        WriteLog(review, "alice.cafebabe.jsonl");

        var box = new WatchBox();
        using var armedWhenTold = new ManualResetEventSlim(false);
        using var watch = new ReviewLogWatch(review, () => box.SetIfArmed(armedWhenTold));
        box.Watch = watch;
        Assert.True(watch.IsArmed);

        // A branch switch takes `.review/` away, and a beat lands while it is gone: the watcher is bound BY
        // HANDLE to a directory that no longer exists and can never fire again, so it is dropped.
        Directory.Delete(review, recursive: true);
        watch.Poll();
        Assert.False(watch.IsArmed);
        armedWhenTold.Reset();

        // ...and the branch comes back. Only the arrival bridge can re-arm from here.
        WriteLog(review, "bob.deadbeef.jsonl");

        Assert.True(
            armedWhenTold.Wait(Budget),
            "a restored `.review/` must re-arm the inner watch and THEN announce the change — never leave a dead handle");
        Assert.True(watch.IsArmed);
    }

    /// <summary>
    /// The safety net, at the layer the stream's keep-alive beat calls it. This watch can never be told
    /// anything — the plan's own directory does not exist, so neither the bridge nor the inner watch has
    /// anything to attach to — and the beat still arms it and reports the change. That is the property which
    /// makes the design sound in spite of <see cref="FileSystemWatcher"/> being best-effort: no notification
    /// is REQUIRED for the panel to catch up. It also reports the change exactly once, so a quiet session
    /// never pushes a <c>review-log</c> frame on every beat.
    /// </summary>
    [Fact]
    public void Poll_ArmsAndReportsAChangeOnce_ForAWatchNothingCanNotify()
    {
        var review = Path.Combine(_root, "not-yet-checked-out", "team.charter.review");
        using var watch = new ReviewLogWatch(review, () => { });

        Assert.False(watch.IsArmed);
        Assert.False(watch.Poll());

        Directory.CreateDirectory(review);
        WriteLog(review, "bob.deadbeef.jsonl");

        Assert.True(watch.Poll(), "the beat must report a log the watcher chain never saw arrive");
        Assert.True(watch.IsArmed);
        Assert.False(watch.Poll(), "and must not report the same log again on the next beat");
    }

    /// <summary>
    /// §5.0 solo primacy, guarded against the safety net itself: neither constructing the watch nor beating on
    /// it may leave a trace beside a plan the reviewer only read.
    /// </summary>
    [Fact]
    public void NeitherConstructionNorPolling_EverCreatesTheReviewDirectory()
    {
        var review = PlanReviewDirectory();
        using var watch = new ReviewLogWatch(review, () => { });

        for (var beat = 0; beat < 5; beat++)
        {
            Assert.False(watch.Poll());
        }

        Assert.False(Directory.Exists(review));
        Assert.Equal("team.charter.md", Path.GetFileName(Assert.Single(Directory.GetFileSystemEntries(_root))));
    }

    /// <summary>
    /// Lets a change callback ask the watch it belongs to whether it is armed YET — the watch cannot be handed
    /// to its own constructor. <b>Reading the arm state INSIDE the callback is the point</b>, not a
    /// convenience: the contract is "re-arm, THEN announce", so a callback that arrives unarmed would be
    /// announcing a change the watch cannot yet follow up on, and asserting <c>IsArmed</c> after an
    /// event-wait instead reads it an instant too early. It is also the only way to tell the RESTORE apart
    /// from the <c>Deleted</c> notifications the removal itself raises — those are real and wanted (the
    /// panel's comments just vanished, so it should re-read), and the OS delivers them on its own schedule,
    /// sometimes well after the removal call returns. A test that waits on "any callback" latches onto one of
    /// those; measured at 4 in 40 locally, and it is what reddened this test on `windows-latest` after 13 ms.
    /// </summary>
    private sealed class WatchBox
    {
        private volatile ReviewLogWatch? _watch;

        public ReviewLogWatch? Watch
        {
            get => _watch;
            set => _watch = value;
        }

        public void SetIfArmed(ManualResetEventSlim signal)
        {
            if (_watch?.IsArmed == true)
            {
                signal.Set();
            }
        }
    }

    // The plan's `.review/` path — computed, never created, exactly as `charter review` leaves it.
    private string PlanReviewDirectory()
    {
        Directory.CreateDirectory(_root);
        var plan = Path.Combine(_root, "team.charter.md");
        File.WriteAllText(plan, "# Plan\n");
        return ReviewLogPaths.DirectoryForPlan(plan);
    }

    private static void WriteLog(string reviewDirectory, string fileName)
    {
        Directory.CreateDirectory(reviewDirectory);
        File.WriteAllText(Path.Combine(reviewDirectory, fileName), "{\"op\":\"create\"}\n");
    }
}
