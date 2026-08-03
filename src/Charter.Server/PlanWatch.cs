namespace Charter.Server;

/// <summary>
/// Watches the plan FILE for live reload — the change that makes <c>/events</c> push a <c>reload</c> frame —
/// with the stream's keep-alive beat behind it as a safety net.
/// </summary>
/// <remarks>
/// <para>
/// <b>The beat is the fix (Charter #92), not the arming order.</b> The shipped watch was a single
/// <see cref="FileSystemWatcher"/> armed once at SSE connect with no fallback, and
/// <see cref="FileSystemWatcher"/> is best-effort <i>by contract</i>: it drops notifications when its kernel
/// buffer overflows and delivers none at all on some network and virtualised filesystems. One dropped
/// notification therefore ended live reload for the whole life of that connection — silently, with the
/// reviewer going on reading a stale render while the agent revised the plan underneath them. #88 proved that
/// failure is reachable in practice rather than theoretical (36 in 1020 runs for the <c>.review/</c> case);
/// only the blast radius differs here, which is why the answer is the same one: no arming order can fix a
/// notifier that is allowed to say nothing, so the guarantee has to be EVENTUAL. <see cref="Poll"/> runs on
/// the stream's existing keep-alive beat — no second timer, no thread — and it is the reason a change can
/// never be lost, only delayed to the next beat.
/// </para>
/// <para>
/// <b>The change signal is the plan file's LENGTH plus its last-write time</b>, which is exactly the pair the
/// watcher's own <see cref="NotifyFilters"/> is built from (<see cref="NotifyFilters.Size"/> and
/// <see cref="NotifyFilters.LastWrite"/>, plus <see cref="NotifyFilters.FileName"/>, which the file's
/// existence covers). So the net's blind spot is a SUBSET of the fast path's: an edit that changes neither
/// length nor last-write is one the watcher would not have reported either. Its residual false negative is a
/// same-length rewrite whose last-write time lands in the same filesystem tick — 2 seconds on FAT, coarser on
/// some network mounts — or an editor that deliberately restores the previous timestamp. The alternative,
/// hashing the file's content every beat, was rejected: it reads the whole plan for the entire life of every
/// review session to buy a case the fast path is blind to anyway.
/// </para>
/// <para>
/// <b>A watcher is bound BY HANDLE.</b> This one is bound to the plan's DIRECTORY (filtered to the one file
/// name — never a tree walk, Lavish's lesson), so the common file-level round trip — an editor saving by
/// replace-and-rename, <c>git checkout -- plan.charter.md</c> — leaves it perfectly alive and is handled on
/// the fast path. What kills it is the DIRECTORY going away: a branch switch that removes a whole plan folder
/// leaves a handle that can never fire again. So the beat drops the handle when the plan vanishes with its
/// folder, and replaces it whenever the beat finds a change the watcher never reported — direct evidence that
/// the handle cannot be trusted, and the only re-arm trigger that costs nothing on a quiet stream.
/// </para>
/// <para>
/// <b>Re-arm, THEN announce.</b> Every path here re-establishes the watch and re-bases the baseline before it
/// reports, so a change the client is told about is one this watch can still follow up on. Reporting first
/// would leave the stream announcing a change from a position it cannot see the next one from — #88's bug.
/// </para>
/// <para>
/// <b>A missing plan file is silent.</b> The reviewer is mid-<c>git checkout</c>; a <c>reload</c> is a full
/// page navigation, so pushing one at a plan that is not there would send them to an error page and lose their
/// place. The beat holds the last stamp, reports nothing, and reports the restore exactly once — which also
/// preserves the pre-#92 contract, where <c>Deleted</c> was deliberately never subscribed.
/// </para>
/// <para>
/// Cost, because this runs for the whole life of every review session: ONE file stat per beat on a quiet
/// stream. Nothing here ever creates anything (§5.0 solo primacy), and no failure — a stat that throws, a
/// watcher that will not arm — may cost the reviewer their live-reload connection.
/// </para>
/// </remarks>
internal sealed class PlanWatch : IDisposable
{
    private readonly object _gate = new();
    private readonly string _planPath;
    private readonly string _directory;
    private readonly string _fileName;
    private readonly Action _onChange;
    private FileSystemWatcher? _watcher;
    private Stamp _stamp;
    private bool _disposed;

    public PlanWatch(string planPath, Action onChange)
    {
        _planPath = planPath;
        _directory = Path.GetDirectoryName(planPath) ?? Directory.GetCurrentDirectory();
        _fileName = Path.GetFileName(planPath) ?? "*";
        _onChange = onChange;

        lock (_gate)
        {
            // Arm first, then look. A write landing between the two is seen by the watcher; one landing before
            // is already in the stamp; one landing after is a stamp the next beat differs against.
            Rearm();
            _stamp = Read();
            BaselineAtArm = (_stamp.Present, _stamp.Length, _stamp.LastWriteUtcTicks);
        }
    }

    /// <summary>
    /// Whether the plan watcher is live. Exposed so the regression tests can assert the ARM itself rather than
    /// infer it from a notification that may or may not arrive.
    /// </summary>
    public bool IsArmed
    {
        get
        {
            lock (_gate)
            {
                return _watcher is not null;
            }
        }
    }

    /// <summary>
    /// The revision this watch's baseline was established at: the plan exactly as the constructor found it,
    /// captured once and never re-based.
    /// </summary>
    /// <remarks>
    /// INTERNAL — a test seam, for the same reason <see cref="IsArmed"/> is one. "The change was not lost" is a
    /// DISJUNCTION (the constructor states both halves): a write landing after this baseline was read gets
    /// ANNOUNCED, and one landing before it "is already in the stamp" — nothing is outstanding, and there is
    /// nothing to announce. From outside, a watch reporting nothing because it took the second half is
    /// indistinguishable from one reporting nothing because the notification was LOST, so a test sweeping a
    /// write across the arming window can only assert the real contract if it can tell which half it got.
    /// Deliberately NOT the live baseline, which reads as "already had it" the instant an announce re-bases
    /// <c>_stamp</c> — that would let a watch which re-bases WITHOUT announcing pass.
    /// </remarks>
    internal (bool Present, long Length, long LastWriteUtcTicks) BaselineAtArm { get; }

    /// <summary>
    /// Re-check the plan on the stream's keep-alive beat. Returns <see langword="true"/> when the client should
    /// reload — a revision the OS never notified us about, or the first sight of a plan the watcher chain could
    /// not attach to. Creates nothing, and never throws: a stat that fails is reported as "no change" rather
    /// than costing the reviewer their stream.
    /// </summary>
    public bool Poll()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            // The one stat a quiet beat costs.
            var current = Read();

            if (!current.Present)
            {
                // Mid-checkout. Say nothing and keep the last stamp, so the restore is judged against the
                // revision the reviewer is actually looking at. If the plan's FOLDER went with it, the handle
                // is bound to something that can never fire again — drop it and let the next beat re-arm.
                if (!DirectoryStillThere())
                {
                    Drop();
                }

                return false;
            }

            if (_watcher is not null && current == _stamp)
            {
                return false;
            }

            // Either nothing is armed, or the beat just found a revision the watcher never reported — which is
            // evidence enough that the handle is not to be trusted. Re-arm FIRST...
            Rearm();

            if (current == _stamp)
            {
                return false;
            }

            // ...and only then announce.
            _stamp = current;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Drop();
        }
    }

    // Replace the watcher outright. Called only where the handle is unproven — at construction, and on a beat
    // that found a change it should have been told about — so a live stream never pays for this.
    private void Rearm()
    {
        Drop();
        if (_disposed)
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(_directory, _fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            watcher.Changed += (_, _) => Changed();
            watcher.Created += (_, _) => Changed();
            watcher.Renamed += (_, _) => Changed();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception)
        {
            // A directory that does not exist (or cannot be watched) throws here. Best-effort by design: the
            // beat re-arms as soon as the plan is back, and delivers the change either way.
            _watcher = null;
        }
    }

    private void Drop()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    // Only consulted when the plan is missing, so a quiet beat never pays for it.
    private bool DirectoryStillThere()
    {
        try
        {
            return Directory.Exists(_directory);
        }
        catch (Exception)
        {
            return true; // No evidence the handle is dead — leave a working watcher alone.
        }
    }

    // A watcher notification already IS the change, so the baseline is re-based here BEFORE the announce —
    // otherwise the next beat would report the very same revision a second time and the reviewer's page would
    // navigate twice. Deliberately does NOT re-base over a plan that has just vanished: holding the last known
    // stamp is what makes a restore-to-identical-bytes silent instead of a spurious reload.
    private void Changed()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var current = Read();
            if (current.Present)
            {
                _stamp = current;
            }
        }

        _onChange();
    }

    private Stamp Read()
    {
        try
        {
            var info = new FileInfo(_planPath);
            return info.Exists
                ? new Stamp(true, info.Length, info.LastWriteTimeUtc.Ticks)
                : default;
        }
        catch (Exception)
        {
            // Unreadable reads as absent: hold the last stamp and say nothing, so a transient I/O failure is
            // never mistaken for a revision.
            return default;
        }
    }

    /// <summary>
    /// What the beat compares. Two fields rather than one hashed <c>long</c> so equality is exact — a
    /// collision here would be a revision silently dropped, which is the failure this whole type exists to
    /// remove.
    /// </summary>
    private readonly record struct Stamp(bool Present, long Length, long LastWriteUtcTicks);
}
