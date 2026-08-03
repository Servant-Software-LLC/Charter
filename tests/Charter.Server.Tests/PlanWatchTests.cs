using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Charter #92 — the live-reload watch on the PLAN FILE, which had the same shape #88 turned out to be: a
/// <see cref="FileSystemWatcher"/> armed ONCE at SSE connect with nothing behind it. That watcher is
/// best-effort <i>by contract</i>, so one dropped notification silently stopped live reload for the whole life
/// of that connection and the reviewer went on reading a stale render while the agent revised the plan
/// underneath them.
/// </summary>
/// <remarks>
/// <para>
/// Tested at the <see cref="PlanWatch"/> seam rather than over HTTP, exactly as <c>ReviewLogWatchTests</c>
/// is and for the same reason: the failure is "the watch never told us", and asserting that through an SSE
/// frame can only ever say "no frame arrived within N seconds" — the flaky shape #88 was filed about. The
/// stream-level wiring (a beat's report actually reaching the client as an <c>event: reload</c> frame) has its
/// own test, <c>PlanWatchStreamTests</c>.
/// </para>
/// <para>
/// Every wait below is on an EVENT or on a beat the test drives itself — never on a sleep — so a passing run
/// takes milliseconds and the budget only ever bounds a failure.
/// </para>
/// </remarks>
[Trait("Category", "PlanWatch")]
public class PlanWatchTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "charter-plan-watch-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>The fast path: the agent revises the plan and the watcher says so.</summary>
    [Fact]
    public void APlanEditedWhileTheWatchIsLive_RaisesOnChange()
    {
        var plan = WritePlan("# Plan\n");
        using var told = new ManualResetEventSlim(false);
        using var watch = new PlanWatch(plan, told.Set);

        Assert.True(watch.IsArmed);

        File.WriteAllText(plan, "# Plan\n\nrevised by the agent\n");

        Assert.True(told.Wait(Budget), "an edit to the watched plan must reach the stream");
    }

    /// <summary>
    /// <b>The point of #92.</b> This watch can never be told anything — the plan's directory does not exist
    /// when it is constructed, so no <see cref="FileSystemWatcher"/> can attach on any platform, which is the
    /// permanent form of the dropped notification the whole fix exists for. The beat must still arm it and
    /// deliver the change; and deliver it EXACTLY ONCE, so a quiet session never pushes a <c>reload</c> frame
    /// (a full page navigation) on every beat.
    /// </summary>
    [Fact]
    public void Poll_ArmsAndReportsAnEditOnce_ForAWatchNothingCanNotify()
    {
        var directory = Path.Combine(_root, "not-yet-checked-out");
        var plan = Path.Combine(directory, "team.charter.md");
        using var watch = new PlanWatch(plan, () => { });

        Assert.False(watch.IsArmed);
        Assert.False(watch.Poll());

        Directory.CreateDirectory(directory);
        File.WriteAllText(plan, "# Plan\n");

        Assert.True(watch.Poll(), "the beat must report a plan the watcher chain never saw arrive");
        Assert.True(watch.IsArmed, "and must have re-armed BEFORE announcing it");
        Assert.False(watch.Poll(), "and must not report the same revision again on the next beat");
    }

    /// <summary>
    /// The dead-handle case. A watcher is bound BY HANDLE, so a plan directory removed and restored — a branch
    /// switch, a <c>git checkout</c> — leaves one that can never fire again. The beat must replace it rather
    /// than trust it, re-arm BEFORE it announces (a change we tell the client about is one we can still follow
    /// up on), and the replacement must be a LIVE handle, not a zombie that merely reads as armed — which is
    /// why the last assertion writes again and waits for the callback instead of re-reading
    /// <see cref="PlanWatch.IsArmed"/>.
    /// </summary>
    [Fact]
    public void APlanDirectoryRemovedAndRestored_ReArmsBeforeReporting_AndTheNewHandleReallyFires()
    {
        var directory = Path.Combine(_root, "wave-04");
        Directory.CreateDirectory(directory);
        var plan = Path.Combine(directory, "team.charter.md");
        File.WriteAllText(plan, "# Plan\n");

        using var told = new ManualResetEventSlim(false);
        using var watch = new PlanWatch(plan, told.Set);
        Assert.True(watch.IsArmed);

        // The branch switch takes the whole plan folder away, and a beat lands while it is gone.
        Directory.Delete(directory, recursive: true);
        Assert.True(
            BeatUntil(watch, () => !watch.IsArmed),
            "a beat landing while the plan folder is gone must drop the handle bound to it");

        // Nothing can notify from here — the watcher is gone and only the beat can bring one back.
        told.Reset();
        Directory.CreateDirectory(directory);
        File.WriteAllText(plan, "# Plan\n\nrestored on the other branch\n");

        Assert.True(watch.Poll(), "the restored plan must be reported by the beat");
        Assert.True(watch.IsArmed, "the beat must re-arm BEFORE it announces the change");
        Assert.False(told.IsSet, "nothing could have notified while the watch was unarmed");

        File.WriteAllText(plan, "# Plan\n\nedited again on the other branch\n");
        Assert.True(
            told.Wait(Budget),
            "the re-armed watcher must be a LIVE handle — an edit after the restore has to reach the stream");
    }

    /// <summary>
    /// A missing plan file is an ordinary moment in a reviewer's day (they are mid-<c>git checkout</c>), so the
    /// beat must be SILENT while it is gone: no crash, and no frame — a <c>reload</c> is a full navigation, and
    /// pushing one at a plan that is not there sends the reviewer to an error page and loses their place. The
    /// pre-#92 watcher never subscribed <c>Deleted</c> either, so this preserves that contract rather than
    /// inventing one. The restore is then reported exactly once.
    /// </summary>
    [Fact]
    public void AMissingPlanFile_IsSilentOnEveryBeat_AndTheRestoreIsReportedExactlyOnce()
    {
        var directory = Path.Combine(_root, "wave-04");
        Directory.CreateDirectory(directory);
        var plan = Path.Combine(directory, "team.charter.md");
        File.WriteAllText(plan, "# Plan\n");

        var told = 0;
        using var watch = new PlanWatch(plan, () => Interlocked.Increment(ref told));

        File.Delete(plan);
        for (var beat = 0; beat < 20; beat++)
        {
            Assert.False(watch.Poll(), "a beat over a plan that is not there must report nothing");
        }

        File.WriteAllText(plan, "# Plan\n\nback from the other branch\n");

        // EITHER arm may observe the restore, and which one wins is a genuine race the contract does not
        // constrain. Asserting `Poll()` specifically asserts the race, not the behaviour: the watcher
        // re-bases the baseline BEFORE it announces, so a notification delivered ahead of this beat
        // legitimately leaves the beat with nothing left to report. That is what made this test flaky —
        // it failed on macOS and on Windows in different runs of the same code.
        var beatReported = watch.Poll();
        Assert.True(
            beatReported || SpinWait.SpinUntil(() => Volatile.Read(ref told) > 0, Budget),
            "the restore must be reported — by the beat, or by the watcher notification it raced");

        // The half that IS a contract: whichever arm reported it, the BEAT must not report it again.
        // (A cross-arm total is deliberately not asserted. Changed() announces unconditionally once a
        // notification arrives — see the double-announce question raised alongside this fix — so a
        // beat-wins run can still take a late notification, and pinning a total here would re-introduce
        // exactly the race this test just stopped asserting.)
        Assert.False(watch.Poll(), "and the beat must not report the same restore a second time");
    }

    /// <summary>
    /// The two arms must not double-report: a watcher notification already IS the change, so it re-bases the
    /// beat's baseline BEFORE it announces. Announcing first would leave the next beat reporting the same
    /// revision a second time — a duplicate <c>reload</c>, which is a second page navigation over whatever the
    /// reviewer did in between.
    /// </summary>
    /// <remarks>
    /// The edit is a last-write-time touch rather than a content write on purpose: the stamp it changes is
    /// fully settled on disk before the notification can possibly be delivered, so the assertion cannot be
    /// satisfied (or broken) by a notification that raced the write's own metadata flush.
    /// </remarks>
    [Fact]
    public void AWatcherNotification_RebasesTheBeat_SoOneEditIsNeverReportedTwice()
    {
        var plan = WritePlan("# Plan\n");
        using var told = new ManualResetEventSlim(false);
        using var watch = new PlanWatch(plan, told.Set);
        Assert.True(watch.IsArmed);

        File.SetLastWriteTimeUtc(plan, File.GetLastWriteTimeUtc(plan).AddMinutes(5));

        Assert.True(told.Wait(Budget), "the watcher must announce the edit");
        Assert.False(watch.Poll(), "and the beat must not report the very same edit again");
    }

    /// <summary>
    /// The #88 pattern that caught the original: sweep the write across the arming window. No ordering of "arm"
    /// against "the agent writes" may LOSE the change — which is the whole argument for the beat, because no
    /// arming order can fix a best-effort notifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"Not lost" is a disjunction</b>, and the constructor names both halves of it: a write landing after
    /// the baseline read is ANNOUNCED (by the watcher, or by the beat that differs against that baseline), and
    /// one landing before it "is already in the stamp" — the baseline IS that revision, so there is nothing
    /// outstanding for this watch to announce and demanding a notification would be demanding a report of a
    /// change relative to itself. Asserting only the first half asserts a stricter contract than the feature
    /// has: at offset 0 the writer spins zero ticks and wins that race on an idle machine every time, which is
    /// why the earlier form of this test failed 25/25 in isolation on Windows and on the macOS runner, while
    /// passing under the contention of a full suite run that happened to delay the writer thread.
    /// </para>
    /// <para>
    /// Which half a run took is READ from <see cref="PlanWatch.BaselineAtArm"/>, never inferred from the
    /// offset: arming does real work (a <see cref="FileSystemWatcher"/> is syscalls, not ticks), so which side
    /// of the baseline read a given spin count lands on is a property of the machine. The two halves cannot be
    /// confused for one another — the plan starts at 7 bytes and every revision written here is longer, so a
    /// baseline equal to the landed file can only have been read after that write completed.
    /// </para>
    /// <para>
    /// The second half is not a free pass. A run taking it still has to leave a watch that is armed, that stays
    /// SILENT about the revision its baseline already holds (a spurious <c>reload</c> is a page navigation),
    /// and that delivers the NEXT one — so a watch that loses changes satisfies neither half at any offset.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AWriteSweptAcrossTheArmingWindow_IsNeverLost()
    {
        for (var offset = 0; offset < 12; offset++)
        {
            var directory = Path.Combine(_root, "sweep-" + offset);
            Directory.CreateDirectory(directory);
            var plan = Path.Combine(directory, "team.charter.md");
            File.WriteAllText(plan, "# Plan\n");

            using var told = new ManualResetEventSlim(false);
            var spin = offset;
            var writer = Task.Run(() =>
            {
                // A spin rather than a sleep: the target is the sub-millisecond window around the arm, and a
                // Task.Delay would land far past it.
                var stop = Stopwatch.StartNew();
                while (stop.ElapsedTicks < spin * 2_000)
                {
                    Thread.SpinWait(50);
                }

                File.WriteAllText(plan, "# Plan\n\nrevision " + spin + "\n");
            });

            using var watch = new PlanWatch(plan, told.Set);
            await writer;

            var landed = new FileInfo(plan);
            var alreadyInTheBaseline =
                watch.BaselineAtArm == (true, landed.Length, landed.LastWriteTimeUtc.Ticks);

            if (!alreadyInTheBaseline)
            {
                Assert.True(
                    WaitForChange(watch, told),
                    $"a write landing {offset} tick-steps into the arming window must still reach the stream");
                continue;
            }

            // The write beat the baseline read, so this watch owes the client nothing for it. That is only an
            // acceptable answer while it stays true of the NEXT revision too, so prove the watch is live and
            // quiet rather than accepting "nothing to report" from something that has stopped watching.
            Assert.True(
                watch.IsArmed,
                $"a write landing {offset} tick-steps in beat the baseline read, so the watch must be armed "
                    + "for the next revision rather than reporting nothing because it is dead");
            Assert.False(
                watch.Poll(),
                $"a write landing {offset} tick-steps in is already the baseline, so no beat may push a "
                    + "reload for it");

            told.Reset();
            File.WriteAllText(plan, "# Plan\n\nrevision " + spin + ", and one after the arm\n");
            Assert.True(
                WaitForChange(watch, told),
                $"a watch whose baseline already held the write at {offset} tick-steps must still deliver the "
                    + "revision that follows it");
        }
    }

    /// <summary>
    /// §5.0 solo primacy, guarded against the safety net itself: neither constructing the watch nor beating on
    /// it may leave a trace beside a plan the reviewer only read — nor conjure the plan's own folder.
    /// </summary>
    [Fact]
    public void NeitherConstructionNorPolling_EverCreatesAnything()
    {
        var directory = Path.Combine(_root, "wave-04");
        Directory.CreateDirectory(directory);
        var plan = Path.Combine(directory, "team.charter.md");
        File.WriteAllText(plan, "# Plan\n");

        using var beside = new PlanWatch(plan, () => { });

        var absentDirectory = Path.Combine(_root, "never-checked-out");
        using var missing = new PlanWatch(Path.Combine(absentDirectory, "team.charter.md"), () => { });

        for (var beat = 0; beat < 5; beat++)
        {
            Assert.False(beside.Poll());
            Assert.False(missing.Poll());
        }

        Assert.Equal(new[] { plan }, Directory.GetFileSystemEntries(directory));
        Assert.False(Directory.Exists(absentDirectory));
    }

    private string WritePlan(string markdown)
    {
        Directory.CreateDirectory(_root);
        var plan = Path.Combine(_root, "team.charter.md");
        File.WriteAllText(plan, markdown);
        return plan;
    }

    // The two arms, waited on together: either the watcher announces or the beat catches it. The reviewer does
    // not care which, and the contract is only that ONE of them always does.
    private static bool WaitForChange(PlanWatch watch, ManualResetEventSlim told)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < Budget)
        {
            if (told.Wait(TimeSpan.FromMilliseconds(25)) || watch.Poll())
            {
                return true;
            }
        }

        return told.IsSet || watch.Poll();
    }

    // Drive the beat until it settles the condition. Bounded, and only a FAILING run ever spends the budget —
    // the loop exists because a directory the OS still holds a handle on can stay observable for a moment
    // after Directory.Delete returns.
    private static bool BeatUntil(PlanWatch watch, Func<bool> settled)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < Budget)
        {
            watch.Poll();
            if (settled())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return settled();
    }
}
