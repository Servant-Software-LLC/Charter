namespace Charter.Server;

/// <summary>
/// Watches the plan's <c>.review/</c> directory for logs arriving (a teammate's <c>git pull</c>, or this
/// reviewer's own first comment) — and, because that directory is created <b>lazily</b>, watches the plan's
/// own directory for the moment it appears and arms itself then.
/// </summary>
/// <remarks>
/// <para>
/// The laziness is §5.0's requirement, not an optimisation: a solo reviewer who writes nothing must leave no
/// trace beside their plan, so <c>charter review</c> can no longer materialise <c>.review/</c> up front and
/// a stream that armed only at start would be permanently blind on the very first session that needs it.
/// </para>
/// <para>
/// <b>The arming ORDER is load-bearing (Charter #88).</b> The arrival bridge is armed FIRST and the inner
/// <c>*.jsonl</c> watch second. Arming them the other way round — look for the directory, and only if it is
/// missing arm the bridge — leaves a window between the existence check and the bridge going live, and a
/// <c>.review/</c> created inside that window is seen by neither: the inner watch never arms, and the bridge
/// never fires again because the directory is only ever created once. The stream is then blind for its whole
/// life, so a teammate's pulled log silently never refreshes the panel. Arming the bridge first has no window
/// at all — a directory that appears before the bridge is live is found by the <see cref="ArmLogs"/> that
/// follows it, and one that appears after raises <c>Created</c> on the bridge.
/// </para>
/// <para>
/// <b><see cref="Poll"/> is the safety net, and it is needed regardless of how carefully we arm.</b>
/// <see cref="FileSystemWatcher"/> is best-effort <i>by contract</i>: it drops notifications when its kernel
/// buffer overflows, and delivers none at all on some network and virtualised filesystems. The review stream
/// runs for the whole life of a session, so a design that assumes every notification arrives is wrong. The
/// stream's existing keep-alive beat therefore re-checks the directory: it arms an inner watch that is still
/// unarmed, and reports an append the watcher never told us about. It costs one directory stat per beat, and
/// for the solo reviewer with no <c>.review/</c> at all it is a single <see cref="Directory.Exists"/> — §5.0's
/// "solo must not get heavier" holds, and nothing here ever CREATES the directory.
/// </para>
/// <para>
/// Both watches are name-filtered and non-recursive — the plan directory is watched for one directory NAME,
/// exactly as the plan watcher watches it for one file name. Never a tree walk (Lavish's lesson). Every arm
/// is best-effort: a watcher this stream could not create must never cost the reviewer their live-reload
/// connection.
/// </para>
/// </remarks>
internal sealed class ReviewLogWatch : IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly Action _onChange;
    private FileSystemWatcher? _logs;
    private FileSystemWatcher? _arrival;
    private long _fingerprint;
    private bool _disposed;

    public ReviewLogWatch(string directory, Action onChange)
    {
        _directory = directory;
        _onChange = onChange;

        lock (_gate)
        {
            // Bridge first, inner watch second — see the remarks. Every ordering of "the directory appears"
            // against these two lines is covered by one of them.
            ArmArrival();
            ArmLogs();
            _fingerprint = Fingerprint();
        }
    }

    /// <summary>
    /// Whether the inner <c>*.jsonl</c> watch is live. Exposed so the regression tests can assert the ARM
    /// itself rather than infer it from a notification that may or may not arrive.
    /// </summary>
    public bool IsArmed
    {
        get
        {
            lock (_gate)
            {
                return _logs is not null;
            }
        }
    }

    /// <summary>
    /// Re-check <c>.review/</c> on the stream's keep-alive beat: arm the inner watch if it is still unarmed,
    /// and report whether the log set has changed since the last look. Returns <see langword="true"/> when the
    /// panel should re-read — an append the OS never notified us about, or the first sight of a directory the
    /// watcher chain missed. Creates nothing, and never throws: a stat that fails is reported as "no change"
    /// rather than costing the reviewer their stream.
    /// </summary>
    public bool Poll()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            ArmLogs();

            var current = Fingerprint();
            if (current == _fingerprint)
            {
                return false;
            }

            _fingerprint = current;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _logs?.Dispose();
            _logs = null;
            _arrival?.Dispose();
            _arrival = null;
        }
    }

    // The real watch: *.jsonl inside .review/. Only possible once the directory exists.
    private void ArmLogs()
    {
        if (_disposed || string.IsNullOrEmpty(_directory))
        {
            return;
        }

        try
        {
            // A `.review/` that went away and came back — a branch switch, or a teammate's `git clean` — leaves
            // the watcher bound BY HANDLE to a directory that no longer exists, so it can never fire again.
            // Drop it here rather than trusting it, or the re-arm below is skipped and the stream stays blind.
            if (_logs is not null && !Directory.Exists(_directory))
            {
                _logs.Dispose();
                _logs = null;
            }

            if (_logs is not null || !Directory.Exists(_directory))
            {
                return;
            }

            var watcher = new FileSystemWatcher(_directory, ReviewLogPaths.LogSearchPattern)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                    | NotifyFilters.CreationTime,
            };
            watcher.Changed += (_, _) => Changed();
            watcher.Created += (_, _) => Changed();
            watcher.Deleted += (_, _) => Changed();
            watcher.Renamed += (_, _) => Changed();
            watcher.EnableRaisingEvents = true;
            _logs = watcher;
        }
        catch (Exception)
        {
            _logs = null;
        }
    }

    // The bridge: the plan's own directory, filtered to the ONE directory name, so the log watch arms the
    // instant .review/ is created instead of on the next page load. Armed unconditionally and left armed —
    // disposing a watcher from inside its own callback is a needless hazard, and a `.review/` removed and
    // restored then re-arms the log watch for free.
    private void ArmArrival()
    {
        try
        {
            var parent = Path.GetDirectoryName(_directory);
            var name = Path.GetFileName(_directory);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name) || !Directory.Exists(parent))
            {
                return;
            }

            var watcher = new FileSystemWatcher(parent, name)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
            };
            watcher.Created += (_, _) => OnArrived();
            watcher.Renamed += (_, _) => OnArrived();
            watcher.EnableRaisingEvents = true;
            _arrival = watcher;
        }
        catch (Exception)
        {
            _arrival = null;
        }
    }

    private void OnArrived()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // `.review/` was just (re)created, so whatever the inner watch was bound to is a DIFFERENT
            // directory now — and a handle to the old one can never fire again. Replace it rather than
            // trusting it: by the time this callback runs the path exists once more, so the liveness check in
            // ArmLogs would wave the dead watcher through.
            _logs?.Dispose();
            _logs = null;
            ArmLogs();
            _fingerprint = Fingerprint();
        }

        // Announced only AFTER the re-arm above: a change we tell the panel about is one we are already able
        // to follow up on. (The `Deleted` notifications the removal itself raises still reach the panel
        // through Changed() — the comments really did vanish, so a re-read then is correct.)
        _onChange();
    }

    // A watcher event already IS the change notification, so the fingerprint is re-based here too — otherwise
    // the next keep-alive beat would report the same append a second time and the panel would re-read twice.
    private void Changed()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _fingerprint = Fingerprint();
        }

        _onChange();
    }

    /// <summary>
    /// A cheap summary of the log set — which files, how long, last written when. Every append grows its file,
    /// so a notification the OS dropped still shows up here on the next beat. An absent <c>.review/</c>
    /// enumerates to nothing (§5.0: this must never create it), and any I/O failure keeps the previous value
    /// so a transient error is not reported as a change.
    /// </summary>
    private long Fingerprint()
    {
        try
        {
            var hash = 17L;
            foreach (var log in ReviewLogPaths.EnumerateLogs(_directory))
            {
                var info = new FileInfo(log);
                hash = unchecked((hash * 31) + string.GetHashCode(
                    Path.GetFileName(log.AsSpan()), StringComparison.Ordinal));
                hash = unchecked((hash * 31) + (info.Exists ? info.Length : -1L));
                hash = unchecked((hash * 31) + (info.Exists ? info.LastWriteTimeUtc.Ticks : 0L));
            }

            return hash;
        }
        catch (Exception)
        {
            return _fingerprint;
        }
    }
}
