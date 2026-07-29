using System.Net;

namespace Charter.Server;

/// <summary>
/// Configuration for a <see cref="ReviewServer"/>. Plain data with safe defaults — this is not a
/// behavioral stub: it deliberately binds loopback-only on an OS-chosen ephemeral port and does not open a
/// browser unless asked.
/// </summary>
public sealed class ReviewServerOptions
{
    /// <summary>
    /// The address to bind. Defaults to <see cref="IPAddress.Loopback"/> so the review server is reachable
    /// only from the local machine.
    /// </summary>
    public IPAddress BindAddress { get; set; } = IPAddress.Loopback;

    /// <summary>The TCP port to bind. <c>0</c> (the default) lets the OS choose an ephemeral port.</summary>
    public int Port { get; set; }

    /// <summary>Whether to open the system browser at the served capability URL on start.</summary>
    public bool OpenBrowser { get; set; }

    /// <summary>
    /// The directory the server writes its durable review sidecar into (§1.6). When set, the server persists
    /// the queued annotations/answers to a server-owned file under this directory on every change and
    /// rehydrates from it on start, so a <c>charter review</c> crash before drain loses nothing. When
    /// <c>null</c> (the default), durability is off and the queues are purely in-memory — the pre-sidecar
    /// behaviour, kept so unit tests that do not exercise durability neither persist nor touch the state dir.
    /// Production (<c>charter review</c>) sets this to <see cref="StateDirectory.Sidecars"/>.
    /// </summary>
    public string? SidecarDirectory { get; set; }

    /// <summary>
    /// The per-author review-log writer (<c>docs/plans/03-git-mediated-team-review.md</c>). When set, the
    /// server APPENDS a record beside the plan for every reviewer action — a <c>create</c> on a new comment,
    /// an <c>edit</c>, a <c>retract</c>, a <c>resolve</c> — so the review travels by git and a teammate's panel
    /// shows it. When <c>null</c> (the default), the review log is READ-ONLY: the panel still folds and shows
    /// whatever logs already sit beside the plan, but this Charter contributes nothing. The writer is supplied
    /// rather than derived so the author identity is resolved ONCE, by the caller (which is also what warns a
    /// reviewer when git could not name them), and so a test can pin an author without depending on the
    /// machine's git config.
    /// </summary>
    public ReviewLogWriter? ReviewLog { get; set; }

    /// <summary>
    /// Rehydrate a queue that <see cref="ReviewSidecar.IsStale"/> judges to belong to a different document,
    /// instead of quarantining it (Charter #67). <see langword="false"/> by default: a plan replaced at a path
    /// must not silently inherit the previous plan's notes. The reviewer sets it — <c>charter review
    /// --keep-annotations</c> — to say "this really is the same document, give them back", which is the explicit
    /// half of the keep-or-discard choice.
    /// </summary>
    public bool KeepStaleAnnotations { get; set; }

    /// <summary>
    /// How often the <c>/events</c> stream beats: it emits a keep-alive comment and re-checks the plan file and
    /// the <c>.review/</c> directory, which is the safety net behind both best-effort
    /// <see cref="FileSystemWatcher"/>s (Charter #88, #92). 15 seconds in production.
    /// </summary>
    /// <remarks>
    /// INTERNAL — a test seam, not a knob for callers, exactly like the ephemeral-port supplier
    /// <c>ReviewServer.StartCore</c> takes. It lets a test prove that a beat's report really does reach the
    /// browser as a frame without spending three real beats waiting for it; the public
    /// <see cref="ReviewServer.Start(ReviewSession, ReviewServerOptions?)"/> path always gets the default.
    /// </remarks>
    internal TimeSpan EventStreamBeat { get; set; } = TimeSpan.FromSeconds(15);
}
