using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Charter.Core;

namespace Charter.Server;

/// <summary>
/// A loopback HTTP server that serves one <see cref="ReviewSession"/>'s rendered, SDK-injected plan and
/// only honours requests presenting the session's <see cref="CapabilityKey"/>. Confinement is enforced by
/// <see cref="PathConfinement"/> so no request can escape the session's root. On top of the wave-2 read-only
/// serve it routes the annotation HTTP API — <c>/api/sessions</c>, <c>/api/{key}/prompts</c>,
/// <c>/api/poll</c>, and the <c>/events</c> reload stream — the server counterpart of the browser SDK's
/// comment-in-place loop. The in-page review panel (Charter #42) adds pre-drain management over the same
/// queue: <c>GET /api/annotations</c> (non-destructive list), <c>POST /api/{key}/annotations/{id}</c> (edit a
/// pending note) and <c>POST /api/{key}/annotations/{id}/delete</c> (retract one). Those act on the pending
/// buffer only — an id the agent has already drained answers 404 ("already handed off") — so the
/// <c>/api/poll</c> drain contract is untouched. The round HAND-OFF (the panel's "Send to agent" control)
/// adds <c>POST /api/{key}/review/submit</c>, <c>POST /api/{key}/review/ack</c> and
/// <c>GET /api/review</c>: they record and clear a signal and wake the long-poll, and they never write the
/// plan — the drafting agent stays its single writer (Architecture B). The git-mediated team review
/// (<c>docs/plans/03-git-mediated-team-review.md</c>) adds two more: <c>GET /api/review-log</c>, the fold of
/// every author's <c>&lt;plan&gt;.review/*.jsonl</c> projected server-side, and
/// <c>POST /api/{key}/annotations/{id}/resolve</c>, which appends a <c>resolve</c> record. Those are the
/// DURABLE half and are orthogonal to the round hand-off: a resolve settles one comment in the log forever,
/// a hand-off marks one round of the live session and is cleared by an ack. Note <c>/api/review</c> and
/// <c>/api/review-log</c> are matched by ORDINAL equality on the whole segment, so neither shadows the other.
/// </summary>
/// <remarks>
/// Transport is <see cref="HttpListener"/> — a BCL primitive, so Charter stays a lean, AOT-friendly binary
/// with no ASP.NET Core framework reference. The plan is re-read and re-rendered from
/// <see cref="ReviewSession.SourcePath"/> on every read request, so editing the source and refreshing shows
/// the update; the <c>/events</c> stream additionally pushes a reload event when the source file changes.
/// Each accepted request is handled on its own task so a long-poll or an open <c>/events</c> stream never
/// stalls the accept loop or another request.
/// </remarks>
public sealed class ReviewServer : IReviewServer
{
    // The serve-time annotation SDK: the real sdk/charter-annotate.js (embedded at build time), wrapped in a
    // marked <script data-charter-sdk> element. Wave-2 injected a placeholder here; wave-3 replaces the body
    // with the real SDK while keeping the injection MECHANISM and the stable data-charter-sdk marker. Read once
    // from the embedded resource at startup. Injection stays serve-time only (invariant 1): the saved artifact
    // remains SDK-free.
    private static readonly string SdkScript = SdkResource.ScriptElement;

    // How long a /api/poll long-poll waits for an annotation before returning (empty). The store fast-paths
    // when one is already queued, so this only bounds an otherwise-idle wait.
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    // How often the /events stream emits a keep-alive comment when the source file is not changing.
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    private readonly ReviewSession _session;
    private readonly AnnotationStore _store;
    private readonly AnswerStore _answers;

    // The reviewer's round hand-off ("Send to agent"): at most one pending submission, and the third wake
    // signal the long-poll waits on. Purely a SIGNAL — the server never writes the plan (Architecture B).
    private readonly ReviewSubmissionStore _handoff = new();

    // The durability sidecar, or null when durability is off (no SidecarDirectory configured). Every mutation
    // of a store is followed by _sidecar?.Persist() so a crash before drain loses nothing (§1.6).
    private readonly ReviewSidecar? _sidecar;

    // The git-mediated review log beside the plan: always READ (so the panel shows teammates' comments), and
    // WRITTEN only when an author identity was supplied. Kept alongside the AnnotationStore rather than
    // replacing it — this slice DUAL-WRITES deliberately, so the existing drain path is untouched.
    private readonly ReviewLogBridge _reviewLog;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private bool _disposed;

    private ReviewServer(
        ReviewSession session,
        AnnotationStore store,
        AnswerStore answers,
        ReviewSidecar? sidecar,
        ReviewLogBridge reviewLog,
        HttpListener listener,
        Uri address)
    {
        _session = session;
        _store = store;
        _answers = answers;
        _sidecar = sidecar;
        _reviewLog = reviewLog;
        _listener = listener;
        Address = address;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The loopback URI the server is listening on (host <c>127.0.0.1</c>, OS-chosen port).</summary>
    public Uri Address { get; }

    /// <summary>
    /// Start a loopback HTTP server for <paramref name="session"/>, serving its rendered + SDK-injected
    /// plan and the annotation API. Uses <paramref name="options"/> (or loopback-only, ephemeral-port
    /// defaults when null).
    /// </summary>
    public static ReviewServer Start(ReviewSession session, ReviewServerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        options ??= new ReviewServerOptions();

        // Production path: the ephemeral port comes from ReserveEphemeralPort. Routed through the internal
        // StartCore seam so a test can inject a supplier that forces a first-attempt port conflict and prove
        // the bounded retry lands the server on a fresh port. Public behaviour is unchanged.
        return StartCore(session, options, () => ReserveEphemeralPort(options.BindAddress));
    }

    // Same as Start, but with the ephemeral-port supplier injected — the internal seam that makes the
    // self-healing retry path deterministically testable. The public Start() always passes the real
    // ReserveEphemeralPort supplier, so this adds no public surface and no behavioural change.
    internal static ReviewServer StartCore(
        ReviewSession session, ReviewServerOptions options, Func<int> ephemeralPortSupplier)
    {
        var host = options.BindAddress.ToString();
        var (listener, address) = BindListener(host, options, ephemeralPortSupplier);

        // One per-session annotation store, held for the server's lifetime: prompts enqueue into it and
        // long-polls drain from it.
        var store = new AnnotationStore();

        // One per-session answer store alongside it (same lifetime): POST /api/{key}/answers enqueues into
        // it, GET /api/answers PEEKS it (non-destructive report), and POST /api/{key}/answers/ack commits the
        // applied prefix. Kept separate from the annotation store so the wave-3 poll contract is untouched.
        var answers = new AnswerStore();

        // Durability (§1.6): when a sidecar directory is configured, REHYDRATE the stores from the server-owned
        // sidecar BEFORE wiring it — seeding via Enqueue re-arms the long-poll signal but does not re-persist
        // (the sidecar reference is created only after seeding) — so a review process that crashed before drain
        // comes back with its queue intact. With no sidecar directory, durability is off (in-memory only).
        ReviewSidecar? sidecar = null;
        StaleAnnotationQueue? stale = null;
        if (!string.IsNullOrEmpty(options.SidecarDirectory))
        {
            var sidecarPath = ReviewSidecar.PathForPlan(options.SidecarDirectory, session.SourcePath);
            var restored = ReviewSidecar.Rehydrate(sidecarPath);

            // The reviewer's explicit KEEP, which is what makes the notice's advice true: fold any queue a
            // previous session set aside back in. The files it read are retired only after the merged queue has
            // been persisted below.
            IReadOnlyList<string> reclaimed = Array.Empty<string>();
            if (options.KeepStaleAnnotations)
            {
                (restored, reclaimed) = ReviewSidecar.Reclaim(sidecarPath, restored);
            }

            // Charter #67: the sidecar is keyed by the plan's PATH, so a plan deleted and re-authored at the
            // same path used to rehydrate the dead document's queue — notes Charter's own anchor resolution
            // already knew were orphaned. A queue where not one anchor survives is not this document's; move it
            // aside and report it rather than delivering it or destroying it.
            var replaced = !options.KeepStaleAnnotations
                && TryReadPlan(session.SourcePath) is { } current
                && ReviewSidecar.IsStale(restored, current);
            if (replaced)
            {
                var preserved = ReviewSidecar.Quarantine(sidecarPath, session.SourcePath, restored);
                stale = new StaleAnnotationQueue(
                    restored.Annotations.Count, preserved ?? sidecarPath, DurabilityDisabled: preserved is null);
            }
            else
            {
                foreach (var annotation in restored.Annotations)
                {
                    store.Enqueue(annotation);
                }
            }

            foreach (var answer in restored.Answers)
            {
                answers.Enqueue(answer);
            }

            // Bind the sidecar unless the stale queue could not be preserved — Persist() snapshots the live
            // stores, so binding it there would overwrite the very notes the copy failed to save.
            if (stale is not { DurabilityDisabled: true })
            {
                sidecar = new ReviewSidecar(sidecarPath, session.SourcePath, store, answers);

                // Persist BEFORE retiring what was reclaimed, so the reviewer's notes exist in exactly one
                // durable place at every instant of the hand-back.
                if (reclaimed.Count > 0)
                {
                    sidecar.Persist();
                    ReviewSidecar.RetireQuarantined(reclaimed);
                }
            }
        }

        // The review log beside the plan. The .review/ directory is deliberately NOT created here: it comes
        // into existence only when a record is actually written (§5.0 — a solo reviewer who writes nothing must
        // leave no trace beside their plan), so the /events stream arms its watcher lazily instead.
        var reviewLog = new ReviewLogBridge(session.SourcePath, options.ReviewLog);

        return new ReviewServer(session, store, answers, sidecar, reviewLog, listener, address)
        {
            StaleAnnotations = stale,
        };
    }

    /// <summary>
    /// The queue this server declined to rehydrate because it belongs to a different document (Charter #67), or
    /// <see langword="null"/> when there was none. The CLI reports it; nothing is destroyed either way.
    /// </summary>
    public StaleAnnotationQueue? StaleAnnotations { get; private init; }

    // The plan's markdown at start, or NULL when it cannot be read. Null is deliberately not "empty": an
    // unreadable plan resolves no anchors, so treating it as empty would quarantine a perfectly live queue on a
    // transient read failure. No evidence means no judgement — the queue rehydrates exactly as it does today.
    private static string? TryReadPlan(string sourcePath)
    {
        try
        {
            return File.ReadAllText(sourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Stop the server and release the bound port.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception)
        {
            // The listener may already be stopped/closed; nothing further to release here.
        }

        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // The accept loop faulted or is draining on shutdown; the port is freed by Close() regardless.
        }

        _shutdown.Dispose();
    }

    // How many times to re-bind an ephemeral port that loses the reserve->bind race. ReserveEphemeralPort
    // frees the probed port before HttpListener re-registers it, so another process can grab it in that gap
    // and Start() throws a port conflict. A fresh probe on the next attempt sidesteps the collision; the bound
    // is small so a genuine, persistent bind failure still surfaces instead of looping forever.
    internal const int EphemeralBindAttempts = 5;

    // Bind the HttpListener, self-healing the ephemeral-port TOCTOU. An explicitly requested port is bound
    // once and any conflict surfaces (silently moving off the caller's chosen port would violate their
    // intent). An ephemeral port (Port = 0) is probed, bound, and — ONLY on a port-conflict exception —
    // re-probed and re-bound up to EphemeralBindAttempts times; a non-conflict failure, or a conflict that
    // persists past the bound, propagates.
    private static (HttpListener Listener, Uri Address) BindListener(
        string host, ReviewServerOptions options, Func<int> ephemeralPortSupplier)
    {
        if (options.Port > 0)
        {
            return StartOn(host, options.Port);
        }

        for (var attempt = 1; ; attempt++)
        {
            var port = ephemeralPortSupplier();
            try
            {
                return StartOn(host, port);
            }
            catch (Exception ex) when (attempt < EphemeralBindAttempts && IsPortConflict(ex))
            {
                // Lost the reserve->bind race for this ephemeral port; a fresh probe next iteration avoids it.
            }
        }
    }

    // Build and start an HttpListener on a single host:port. On a failed Start() the half-built listener is
    // closed so a retry (or the rethrow) leaves no half-open registration behind.
    private static (HttpListener Listener, Uri Address) StartOn(string host, int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{host}:{port}/");
        try
        {
            listener.Start();
        }
        catch
        {
            listener.Close();
            throw;
        }

        return (listener, new Uri($"http://{host}:{port}/"));
    }

    // Whether an exception from HttpListener.Start means "that port/prefix is already taken" — the only case
    // the ephemeral-port retry self-heals. Everything else (e.g. ERROR_ACCESS_DENIED, which needs admin) is a
    // genuine failure and must surface immediately rather than be retried away.
    internal static bool IsPortConflict(Exception ex)
    {
        // Walk the whole exception chain: the Unix (managed) HttpListener commonly wraps the socket error as
        // an INNER exception, so a top-level-only check misses it — whereas Windows HTTP.sys throws the code
        // directly. Checking the chain covers both.
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                // Windows HTTP.sys uses error CODES: ERROR_SHARING_VIOLATION (32), ERROR_ALREADY_EXISTS
                // (183), WSAEADDRINUSE (10048). The managed (Unix) HttpListener does NOT reuse those codes —
                // an in-process prefix collision throws with its own message ("conflicts with an existing
                // registration") and an OS bind race surfaces "address already in use" — so we match those
                // stable message fragments too. Safe to be permissive: this only gates a benign retry onto a
                // fresh ephemeral port (genuine failures like ERROR_ACCESS_DENIED match nothing and surface).
                case HttpListenerException hle when hle.ErrorCode is 32 or 183 or 10048
                    || hle.Message.Contains("conflicts with an existing registration", StringComparison.OrdinalIgnoreCase)
                    || hle.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase):
                    return true;
                // The robust cross-platform signal: a socket "address already in use" anywhere in the chain.
                case SocketException se when se.SocketErrorCode == SocketError.AddressAlreadyInUse:
                    return true;
            }
        }

        return false;
    }

    private static int ReserveEphemeralPort(IPAddress address)
    {
        var probe = new TcpListener(address, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }

            // Fire-and-forget: HandleAsync catches everything and always closes the response, so a long-poll
            // or an open /events stream cannot block the accept loop or another request.
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var path = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath ?? "/").Trim('/');
            var segments = path.Length == 0
                ? Array.Empty<string>()
                : path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            // ---- Annotation API routes, dispatched BEFORE the wave-2 read-only serve. Each route enforces the
            // ---- capability key itself (from the query string, or the path for the prompts route). ----
            if (segments.Length >= 1 && string.Equals(segments[0], "events", StringComparison.Ordinal))
            {
                await HandleEventsAsync(context).ConfigureAwait(false);
                return;
            }

            if (segments.Length >= 1 && string.Equals(segments[0], "api", StringComparison.Ordinal))
            {
                await HandleApiAsync(context, segments).ConfigureAwait(false);
                return;
            }

            // ---- Wave-2 read-only serve (unchanged): capability + confinement gates, then render + inject. ----
            ServeStatic(context);
        }
        catch (Exception)
        {
            TrySetInternalError(response);
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception)
            {
                // The client may have already disconnected; the response is gone either way.
            }
        }
    }

    private void ServeStatic(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        // Gate 1 — capability key. The key rides the ?key= query string (the capability-URL pattern), so
        // a process that merely guesses the ephemeral port still cannot read the served plan.
        if (!_session.Key.Matches(request.QueryString["key"]))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        // Gate 2 — path confinement (defense-in-depth; the authoritative proof is the unit test). The
        // transport normalizes ../ away, so this mainly rejects paths that canonicalize outside the root.
        var requestPath = Uri.UnescapeDataString(request.Url?.AbsolutePath ?? "/").TrimStart('/');
        if (requestPath.Length > 0 && PathConfinement.Resolve(_session.Root, requestPath) is null)
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        // Render from source per request so an edit + refresh shows the update (wave-2 live reload).
        var markdown = File.ReadAllText(_session.SourcePath);
        var served = SdkInjector.Inject(CharterRenderer.Render(markdown), SdkScript);
        var payload = Encoding.UTF8.GetBytes(served);

        // Security headers on the served page. This is the SERVED-PAGE CSP — deliberately looser than the
        // stricter export CSP (Charter.Core.ArtifactExporter): it keeps script-src 'unsafe-inline' so the
        // injected Mermaid runtime + annotation SDK run, and connect-src 'self' so the SDK can POST/poll the
        // same-origin /api/* routes. img-src is confined to 'self' + data:, and every other fetch class is
        // shut off. Referrer-Policy: no-referrer is load-bearing — the capability key rides the ?key= URL, so
        // this stops it leaking via the Referer header to any remote the plan references.
        WriteSecurityHeaders(response);

        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = payload.Length;
        response.OutputStream.Write(payload, 0, payload.Length);
    }

    private async Task HandleApiAsync(HttpListenerContext context, string[] segments)
    {
        var response = context.Response;
        var method = context.Request.HttpMethod;

        // POST /api/{key}/prompts — submit an annotation (state-changing: capability key + CSRF gated).
        if (segments.Length == 3 &&
            string.Equals(segments[2], "prompts", StringComparison.Ordinal) &&
            string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePromptsAsync(context, segments[1]).ConfigureAwait(false);
            return;
        }

        // POST /api/{key}/answers/ack?count=N — commit (remove) the front N answers the caller has peeked and
        // durably applied (state-changing: capability key + CSRF gated). This is the ONLY path that removes an
        // answer, so a plain poll can never strand one (§1.6). Checked before the 3-segment answers route.
        if (segments.Length == 4 &&
            string.Equals(segments[2], "answers", StringComparison.Ordinal) &&
            string.Equals(segments[3], "ack", StringComparison.Ordinal) &&
            string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            HandleAnswersAck(context, segments[1]);
            return;
        }

        // POST /api/{key}/review/submit — the in-page "Send to agent" hand-off: record that the reviewer
        // marked this round complete and WAKE the long-poll (state-changing: capability key + CSRF gated).
        // It signals only — the drafting agent remains the single writer of the plan.
        if (segments.Length == 4 &&
            string.Equals(segments[2], "review", StringComparison.Ordinal) &&
            string.Equals(segments[3], "submit", StringComparison.Ordinal) &&
            string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            HandleReviewSubmit(context, segments[1]);
            return;
        }

        // POST /api/{key}/review/ack?sequence=N — clear the hand-off the agent has now been told about
        // (state-changing: capability key + CSRF gated), mirroring /answers/ack.
        if (segments.Length == 4 &&
            string.Equals(segments[2], "review", StringComparison.Ordinal) &&
            string.Equals(segments[3], "ack", StringComparison.Ordinal) &&
            string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            HandleReviewAck(context, segments[1]);
            return;
        }

        // POST /api/{key}/answers — submit a :::question answer (state-changing: capability key + CSRF gated,
        // mirroring /prompts). A dedicated route, not /prompts, because an answer's shape differs and reusing
        // the poll stream would break the wave-3 annotation contract.
        if (segments.Length == 3 &&
            string.Equals(segments[2], "answers", StringComparison.Ordinal) &&
            string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            await HandleAnswersPostAsync(context, segments[1]).ConfigureAwait(false);
            return;
        }

        // POST /api/{key}/annotations/{id}/delete — RETRACT a still-pending annotation (state-changing:
        // capability key in the path + CSRF gated). FIVE segments, so it must be matched BEFORE the
        // four-segment update route below, which would otherwise never see the trailing /delete.
        if (segments.Length == 5 &&
            string.Equals(segments[2], "annotations", StringComparison.Ordinal) &&
            string.Equals(segments[4], "delete", StringComparison.Ordinal) &&
            string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            HandleAnnotationDelete(context, segments[1], segments[3]);
            return;
        }

        // POST /api/{key}/annotations/{id}/resolve — CLOSE a comment in the review log. Also five segments,
        // matched alongside /delete. There is no AnnotationStore counterpart: resolution is a property of the
        // durable log (a resolve is a record, §4.2), never of the pre-drain queue, so this route is the one
        // panel action with no dual write.
        if (segments.Length == 5 &&
            string.Equals(segments[2], "annotations", StringComparison.Ordinal) &&
            string.Equals(segments[4], "resolve", StringComparison.Ordinal) &&
            string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            HandleAnnotationResolve(context, segments[1], segments[3]);
            return;
        }

        // POST /api/{key}/annotations/{id} — EDIT a still-pending annotation's note (state-changing:
        // capability key in the path + CSRF gated). A command-POST with the id as the trailing segment, the
        // same write idiom as /answers/ack — no second HTTP verb vocabulary for a single new operation.
        if (segments.Length == 4 &&
            string.Equals(segments[2], "annotations", StringComparison.Ordinal) &&
            string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            await HandleAnnotationUpdateAsync(context, segments[1], segments[3]).ConfigureAwait(false);
            return;
        }

        // GET /api/annotations — LIST the pending annotations without removing any (capability key on the
        // query string, like /api/answers). This is the in-page review panel's read: everything it returns is
        // still awaiting handoff, so listing can never consume what `charter poll` has yet to drain.
        if (segments.Length == 2 &&
            string.Equals(segments[1], "annotations", StringComparison.Ordinal) &&
            string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            HandleAnnotationsList(context);
            return;
        }

        // GET /api/review — the round's hand-off status + live pending counts (capability key on the query
        // string, like /api/answers). Non-destructive: only /review/ack clears a hand-off.
        if (segments.Length == 2 &&
            string.Equals(segments[1], "review", StringComparison.Ordinal) &&
            string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            HandleReviewStatus(context);
            return;
        }

        // GET /api/review-log — the FOLDED review state of every author's log beside the plan (capability key
        // on the query string, like /api/annotations). The logs are read and folded server-side and only this
        // projection crosses the wire; there is deliberately NO static-file branch for `.review/`, because the
        // served root is the plan's own DIRECTORY — one such branch would make every sibling file under
        // docs/plans/ a key-gated HTTP-readable resource. Distinct from /api/review above by ORDINAL equality
        // on the whole segment, so the round hand-off's status and the durable log can never shadow each other.
        if (segments.Length == 2 &&
            string.Equals(segments[1], "review-log", StringComparison.Ordinal) &&
            string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            HandleReviewLog(context);
            return;
        }

        // GET /api/sessions — the current session descriptor.
        if (segments.Length == 2 &&
            string.Equals(segments[1], "sessions", StringComparison.Ordinal) &&
            string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            HandleSessions(context);
            return;
        }

        // GET /api/poll — long-poll for queued annotations.
        if (segments.Length == 2 &&
            string.Equals(segments[1], "poll", StringComparison.Ordinal) &&
            string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePollAsync(context).ConfigureAwait(false);
            return;
        }

        // GET /api/answers — PEEK queued :::question answers WITHOUT removing them (capability key on the query
        // string, like /poll). Report-don't-remove: the caller reports/applies, then commits via /answers/ack.
        if (segments.Length == 2 &&
            string.Equals(segments[1], "answers", StringComparison.Ordinal) &&
            string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            HandleAnswersPeek(context);
            return;
        }

        response.StatusCode = (int)HttpStatusCode.NotFound;
    }

    private void HandleSessions(HttpListenerContext context)
    {
        var response = context.Response;
        if (!_session.Key.Matches(context.Request.QueryString["key"]))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        var descriptor = JsonSerializer.Serialize(
            new
            {
                sourcePath = _session.SourcePath,
                sourceFile = Path.GetFileName(_session.SourcePath),
            },
            AnnotationApi.JsonOptions);

        WriteJson(response, descriptor);
    }

    private async Task HandlePromptsAsync(HttpListenerContext context, string keyFromPath)
    {
        var request = context.Request;
        var response = context.Response;

        // Gate — capability key. For this route the key travels in the path (/api/{key}/prompts).
        if (!_session.Key.Matches(keyFromPath))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        // Gate — CSRF / same-origin. A state-changing POST must not be forgeable from a foreign origin,
        // even with a valid key (the loopback + capability invariant, extended to writes).
        if (!AnnotationApi.IsAllowedOrigin(request.Headers["Origin"], Address))
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        AnnotationApi.PromptSubmission? submission;
        try
        {
            submission = JsonSerializer.Deserialize<AnnotationApi.PromptSubmission>(body, AnnotationApi.JsonOptions);
        }
        catch (JsonException)
        {
            // Malformed JSON is a client error (400), not a server fault (500) — the same guard the answers
            // route already applies, so both state-changing POST routes reject bad input consistently.
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        if (submission is null || string.IsNullOrEmpty(submission.AnchorId))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        // Resolve the anchor to its 1-based markdown source line — the deterministic half of the round-trip.
        // This is the SUBMIT-TIME value: it backs the reviewer's in-page panel, which is showing the page as
        // rendered right now. It is deliberately NOT the value the agent receives — the drain re-resolves
        // against the plan as it is at handoff, because any edit above the block moves it (Charter #49).
        var markdown = await File.ReadAllTextAsync(_session.SourcePath).ConfigureAwait(false);
        var sourceLine = SourceMap.Build(markdown).LineForAnchor(submission.AnchorId);

        // DUAL WRITE (deliberately temporary — see the ReviewLogBridge remarks): the comment becomes a durable
        // `create` record in this author's log AND stays in the pre-drain AnnotationStore, so the shipped
        // drain path is untouched by this slice. The log record's id BECOMES the annotation's id, so the
        // panel's later edit / retract / resolve address one identity rather than needing a side map.
        var created = _reviewLog.Create(
            new ReviewAnchor(
                submission.AnchorId,
                AnnotationApi.KindToken(AnnotationApi.ParseKind(submission.Kind)),
                submission.Quote,
                // The plan's content hash AS THE REVIEWER SAW IT. Written now because records are immutable:
                // it is what lets a later Charter show "you commented on «…»; here is what changed since".
                ReviewLogBridge.PlanHash(markdown)),
            submission.Note ?? string.Empty);

        var annotation = new Annotation(
            Id: created?.Id ?? Guid.NewGuid().ToString("N"),
            Kind: AnnotationApi.ParseKind(submission.Kind),
            AnchorId: submission.AnchorId,
            Note: submission.Note ?? string.Empty,
            SourceLine: sourceLine,
            // Carry the sub-part fidelity payload verbatim so the drain tells the agent WHICH part of the block
            // was flagged: quote/start/end for a text-range selection, nodeId for a diagram node (all null for a
            // whole-block element annotation). Anchor->source-line resolution above is unchanged; these are
            // additive. WriteDrainedJson serializes the whole Annotation, so these flow to /api/poll unmodified.
            Quote: submission.Quote,
            Start: submission.Start,
            End: submission.End,
            NodeId: submission.NodeId);

        _store.Enqueue(annotation);

        // Persist BEFORE acknowledging so a crash after the 200 cannot lose an annotation the reviewer was
        // told was accepted (§1.6). A no-op when durability is off.
        _sidecar?.Persist();

        WriteJson(response, JsonSerializer.Serialize(annotation, AnnotationApi.JsonOptions));
    }

    private async Task HandlePollAsync(HttpListenerContext context)
    {
        var response = context.Response;
        if (!_session.Key.Matches(context.Request.QueryString["key"]))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        // `wait=0` skips the long-poll wait and drains whatever is queued right now (returns [] fast when
        // empty). The browser SDK never sends it, so the default long-poll and every existing test are
        // unchanged; `charter poll` uses wait=0 for its non-blocking drain and omits it only under --wait.
        var immediate = string.Equals(context.Request.QueryString["wait"], "0", StringComparison.Ordinal);
        if (!immediate)
        {
            try
            {
                await WaitForReviewWorkAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The server is shutting down; return whatever is currently drained (typically empty).
            }
        }

        // Drain, then write with requeue-on-failure: if the client disconnected mid-write the drained batch is
        // re-enqueued (at the front) so a subsequent poll re-fetches it rather than losing it (at-least-once).
        // WriteDrainedJson rethrows on a write failure (after requeueing in-memory), so the durability persist
        // below runs ONLY on a successful delivery — until then the sidecar still lists the annotations, so a
        // crash after a failed write rehydrates them (never a loss; at most an at-least-once re-delivery).
        var drained = _store.Drain();

        // THE handoff moment: re-resolve every anchor against the plan as it is RIGHT NOW (Charter #49). The
        // line stored at submit time is a snapshot for the reviewer's panel; by the time an agent drains, an
        // edit above the block (or a rehydrate across a change made while the server was down) may have moved
        // it. An anchor that no longer resolves drains with sourceLine null + anchorStatus "orphaned".
        var resolved = await AnchorResolution
            .ResolveAgainstFileAsync(drained, _session.SourcePath)
            .ConfigureAwait(false);

        // The REQUEUE puts the store's own records back — the submit-time line the panel shows — not the
        // handoff projection; a re-drain re-resolves them anyway.
        WriteDrainedJson(response, resolved, _ => _store.Requeue(drained));
        _sidecar?.Persist();
    }

    /// <summary>
    /// The long-poll wait: return as soon as ANY of the three review stores has work for the agent — a queued
    /// annotation, a queued <c>:::question</c> answer, or the reviewer's explicit round hand-off — or when the
    /// shared poll window elapses. Waiting on the annotation store alone (Charter #62) meant the reviewer's
    /// DECISIONS, the highest-value signal Charter carries, were also its slowest: an answer woke nothing and
    /// sat queued until this timeout expired.
    /// </summary>
    private async Task WaitForReviewWorkAsync(CancellationToken cancellationToken)
    {
        // One shared window: each wait carries the same timeout, so when the first to settle reports "nothing"
        // the window has elapsed for all three. Cancelling on the way out releases the losers' timers at once
        // (they settle Canceled, never Faulted, so an abandoned wait cannot surface as an unobserved fault).
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var annotations = _store.WaitForPendingAsync(PollTimeout, window.Token);
        var answers = _answers.WaitForPendingAsync(PollTimeout, window.Token);
        var handoff = _handoff.WaitForPendingAsync(PollTimeout, window.Token);

        try
        {
            var settled = await Task.WhenAny(annotations, answers, handoff).ConfigureAwait(false);
            await settled.ConfigureAwait(false);
        }
        finally
        {
            window.Cancel();
        }
    }

    /// <summary>
    /// <c>POST /api/{key}/review/submit</c> — the in-page <b>Send to agent</b> control: the reviewer marks
    /// this round of feedback complete. It RECORDS the hand-off (with the counts pending at that moment) and
    /// WAKES the long-poll; it does not touch the plan file, apply anything, or drain anything. The agent
    /// still does all the draining and all the writing.
    /// </summary>
    private void HandleReviewSubmit(HttpListenerContext context, string keyFromPath)
    {
        var request = context.Request;
        var response = context.Response;

        // Gate — capability key. For this route the key travels in the path, like prompts and answers.
        if (!_session.Key.Matches(keyFromPath))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        // Gate — CSRF / same-origin, exactly as the other state-changing POSTs.
        if (!AnnotationApi.IsAllowedOrigin(request.Headers["Origin"], Address))
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        var submission = _handoff.Submit(_store.Snapshot().Count, _answers.Peek().Count);
        WriteJson(response, JsonSerializer.Serialize(submission, AnnotationApi.JsonOptions));
    }

    /// <summary>
    /// <c>POST /api/{key}/review/ack?sequence=N</c> — the agent has been TOLD about hand-off <c>N</c>, so
    /// clear it and re-arm the wake signal. A compare-and-clear by sequence: a hand-off the reviewer made
    /// after this one was reported is newer than <c>N</c> and survives, rather than being silently swallowed.
    /// </summary>
    private void HandleReviewAck(HttpListenerContext context, string keyFromPath)
    {
        var request = context.Request;
        var response = context.Response;

        if (!_session.Key.Matches(keyFromPath))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        if (!AnnotationApi.IsAllowedOrigin(request.Headers["Origin"], Address))
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        // A missing/invalid sequence acks nothing (a no-op ack is harmless, never a wrong clear).
        var acked = long.TryParse(request.QueryString["sequence"], out var sequence) && _handoff.Ack(sequence);
        WriteJson(response, JsonSerializer.Serialize(new { acked }, AnnotationApi.JsonOptions));
    }

    /// <summary>
    /// <c>GET /api/review?key=…</c> — the round's status: whether the reviewer has handed it off (and the
    /// hand-off record), plus the LIVE pending counts. Non-destructive, key-gated on the query string like
    /// <c>/api/poll</c> and <c>/api/answers</c>. The in-page panel reads it to decide whether <b>Send to
    /// agent</b> has anything to send; <c>charter poll</c> reads it to fill the envelope's marker.
    /// </summary>
    private void HandleReviewStatus(HttpListenerContext context)
    {
        var response = context.Response;
        if (!_session.Key.Matches(context.Request.QueryString["key"]))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        var submission = _handoff.Peek();
        var status = new
        {
            submitted = submission is not null,
            submission,
            pending = new { annotations = _store.Snapshot().Count, answers = _answers.Peek().Count },
        };

        WriteJson(response, JsonSerializer.Serialize(status, AnnotationApi.JsonOptions));
    }

    private void HandleAnnotationsList(HttpListenerContext context)
    {
        var response = context.Response;

        // Gate — capability key on the query string, like /api/answers and /api/poll: the pre-drain queue must
        // not leak the reviewer's notes to an unauthorized reader (a guessed ephemeral port is not enough).
        if (!_session.Key.Matches(context.Request.QueryString["key"]))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        // LIST, don't drain: Snapshot() is non-destructive, so the panel showing the queue never competes with
        // the agent's poll for it. A non-destructive read needs no requeue-on-write-failure — a dropped write
        // loses nothing.
        WriteJson(response, JsonSerializer.Serialize(_store.Snapshot(), AnnotationApi.JsonOptions));
    }

    private async Task HandleAnnotationUpdateAsync(
        HttpListenerContext context, string keyFromPath, string annotationId)
    {
        var request = context.Request;
        var response = context.Response;

        if (!_session.Key.Matches(keyFromPath))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        // Gate — CSRF / same-origin, exactly as the prompts and answers writes: a state-changing POST must not
        // be forgeable from a foreign origin even with a valid key.
        if (!AnnotationApi.IsAllowedOrigin(request.Headers["Origin"], Address))
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        AnnotationApi.NoteUpdate? submission;
        try
        {
            submission = JsonSerializer.Deserialize<AnnotationApi.NoteUpdate>(body, AnnotationApi.JsonOptions);
        }
        catch (JsonException)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        if (submission?.Note is null)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        // DUAL WRITE: append an `edit` record to the durable log AND update the pre-drain queue. The two are
        // independent — the log outlives the drain, so an edit is still meaningful for a comment the agent has
        // already taken, whereas the queue's 404 ("already handed off") only speaks for the queue.
        var logged = _reviewLog.Edit(annotationId, submission.Note);
        var queued = _store.Update(annotationId, submission.Note);

        // 404 only when NEITHER store could act — the id is unknown to both, so there is nothing to edit.
        if (!logged && !queued)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        // Persist BEFORE acknowledging, exactly as the create path does: an edit the reviewer was told was
        // accepted must survive a crash before the agent drains it.
        _sidecar?.Persist();

        WriteJson(response, JsonSerializer.Serialize(new { updated = true }, AnnotationApi.JsonOptions));
    }

    private void HandleAnnotationResolve(HttpListenerContext context, string keyFromPath, string commentId)
    {
        var request = context.Request;
        var response = context.Response;

        if (!_session.Key.Matches(keyFromPath))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        if (!AnnotationApi.IsAllowedOrigin(request.Headers["Origin"], Address))
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        // Resolve is open to ANYONE (review is collaborative) and always attributed in the panel — unlike
        // retract, which only the comment's own author may write. 404 means no such comment in the fold; the
        // bridge never guesses which comment was meant.
        if (!_reviewLog.Resolve(commentId))
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        WriteJson(response, JsonSerializer.Serialize(new { resolved = true }, AnnotationApi.JsonOptions));
    }

    private void HandleReviewLog(HttpListenerContext context)
    {
        var response = context.Response;

        // Gate — capability key on the query string, like /api/annotations: a plan's review is as sensitive as
        // the plan itself, so a guessed ephemeral port must not read it.
        if (!_session.Key.Matches(context.Request.QueryString["key"]))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        // Fold on every read (rather than caching) so a `git pull` landing a teammate's log mid-session is
        // reflected the moment the panel asks. Anchors resolve against the plan AS IT IS NOW: exact block-id
        // match, or orphaned (§4.3) — never a fuzzy re-attachment.
        var markdown = ReadPlanOrEmpty();
        WriteJson(response, JsonSerializer.Serialize(_reviewLog.BuildView(markdown), AnnotationApi.JsonOptions));
    }

    /// <summary>
    /// The plan's markdown, or empty when it cannot be read. An unreadable plan means no anchor can honestly
    /// be resolved, so every comment renders as an ORPHAN — the neutral, recoverable answer — rather than
    /// carrying a line number nothing verified.
    /// </summary>
    private string ReadPlanOrEmpty()
    {
        try
        {
            return File.ReadAllText(_session.SourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private void HandleAnnotationDelete(HttpListenerContext context, string keyFromPath, string annotationId)
    {
        var request = context.Request;
        var response = context.Response;

        if (!_session.Key.Matches(keyFromPath))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        if (!AnnotationApi.IsAllowedOrigin(request.Headers["Origin"], Address))
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        // DUAL WRITE: append a `retract` record to the durable log AND remove from the pre-drain queue.
        // Removing a PENDING annotation is a true retraction: the store's _gate lock serializes
        // delete-vs-drain, so the note either reaches the agent or is retracted, never both. The log's retract
        // is the DURABLE half — it hides the body but keeps the thread, because replies are other people's
        // words (§4.2) — and it is refused for anyone but the comment's own author.
        var logged = _reviewLog.Retract(annotationId);
        var queued = _store.Remove(annotationId);

        // 404 only when NEITHER store could act: an unknown id, or someone else's comment (which only its own
        // author may withdraw).
        if (!logged && !queued)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        _sidecar?.Persist();

        WriteJson(response, JsonSerializer.Serialize(new { deleted = true }, AnnotationApi.JsonOptions));
    }

    private async Task HandleAnswersPostAsync(HttpListenerContext context, string keyFromPath)
    {
        var request = context.Request;
        var response = context.Response;

        // Gate — capability key. For this route the key travels in the path (/api/{key}/answers), like prompts.
        if (!_session.Key.Matches(keyFromPath))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        // Gate — CSRF / same-origin. A state-changing POST must not be forgeable from a foreign origin, even
        // with a valid key (the loopback + capability invariant, extended to writes).
        if (!AnnotationApi.IsAllowedOrigin(request.Headers["Origin"], Address))
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        Answer? submission;
        try
        {
            submission = JsonSerializer.Deserialize<Answer>(body, AnnotationApi.JsonOptions);
        }
        catch (JsonException)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        if (submission is null || string.IsNullOrEmpty(submission.QuestionId))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        // Unlike an annotation there is no anchor to resolve: an answer's identity is its client-chosen
        // questionId, so this is a pure echo. Preserve the target (human/agent) verbatim for the downstream
        // handoff, and default the values to empty so the drain always serializes a values array.
        var answer = submission with { Values = submission.Values ?? Array.Empty<string>() };

        _answers.Enqueue(answer);

        // Persist BEFORE acknowledging: an answer the reviewer was told was accepted must survive a crash
        // before any agent drains it — the core of solo-review durability (§1.6). A no-op when durability off.
        _sidecar?.Persist();

        WriteJson(response, JsonSerializer.Serialize(answer, AnnotationApi.JsonOptions));
    }

    private void HandleAnswersPeek(HttpListenerContext context)
    {
        var response = context.Response;

        // Gate — capability key on the query string, like /poll: the peek must not leak queued answers to an
        // unauthorized reader (a guessed ephemeral port is not enough).
        if (!_session.Key.Matches(context.Request.QueryString["key"]))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        // PEEK, don't remove (§1.6): report the queued answers but leave them in the store so a plain poll can
        // never strand one. They are removed only by a subsequent /answers/ack after a durable inline write.
        // A non-destructive read needs no requeue-on-write-failure — a dropped write loses nothing.
        WriteJson(response, JsonSerializer.Serialize(_answers.Peek(), AnnotationApi.JsonOptions));
    }

    private void HandleAnswersAck(HttpListenerContext context, string keyFromPath)
    {
        var request = context.Request;
        var response = context.Response;

        // Gate — capability key. For this route the key travels in the path (/api/{key}/answers/ack).
        if (!_session.Key.Matches(keyFromPath))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        // Gate — CSRF / same-origin. A state-changing commit must not be forgeable from a foreign origin, even
        // with a valid key (the CLI sends no Origin, which IsAllowedOrigin permits).
        if (!AnnotationApi.IsAllowedOrigin(request.Headers["Origin"], Address))
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        // The caller commits the front N answers it peeked and durably applied; N rides the query string.
        // A missing/invalid count commits nothing (a no-op ack is harmless, never a wrong removal).
        if (!int.TryParse(request.QueryString["count"], out var count) || count <= 0)
        {
            WriteJson(response, JsonSerializer.Serialize(new { committed = 0 }, AnnotationApi.JsonOptions));
            return;
        }

        var committed = _answers.CommitFront(count);
        _sidecar?.Persist();
        WriteJson(response, JsonSerializer.Serialize(new { committed = committed.Count }, AnnotationApi.JsonOptions));
    }

    private async Task HandleEventsAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        if (!_session.Key.Matches(request.QueryString["key"]))
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        var ct = _shutdown.Token;

        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.SendChunked = true;

        var output = response.OutputStream;

        // Emit an initial event so the stream's Content-Type and liveness are observable immediately.
        await output.WriteAsync(AnnotationApi.SseEvent("ping", "connected"), ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);

        // Edge-triggered wake signal fed by two watchers. Each names the event it wants pushed, because a plan
        // edit and a teammate's log arriving are DIFFERENT things: the first needs a page reload, the second
        // only needs the panel to re-read. Coalesced through a set, so a burst of file events (an editor's
        // write-truncate-rename dance, or a `git pull` touching several logs) pushes one frame per kind.
        using var wake = new SemaphoreSlim(0);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        void Signal(string eventName)
        {
            lock (pending)
            {
                if (!pending.Add(eventName))
                {
                    return;
                }
            }

            try
            {
                wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already saturated with pending wakes; the loop will coalesce them.
            }
            catch (ObjectDisposedException)
            {
                // The stream is tearing down; the change no longer matters.
            }
        }

        // Watch the plan FILE, never its tree — a large parent directory saturates the event loop.
        var directory = Path.GetDirectoryName(_session.SourcePath) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileName(_session.SourcePath) ?? "*";
        using var planWatcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        planWatcher.Changed += (_, _) => Signal(ReloadEvent);
        planWatcher.Created += (_, _) => Signal(ReloadEvent);
        planWatcher.Renamed += (_, _) => Signal(ReloadEvent);
        planWatcher.EnableRaisingEvents = true;

        // ...and the .review/ DIRECTORY, filtered to *.jsonl. Without this, a `git pull` landing a teammate's
        // log mid-session would leave the panel silently showing the fold taken at startup. The directory is
        // created lazily (only when a record is actually written), so this ALSO watches for its arrival.
        using var logWatcher = new ReviewLogWatch(_reviewLog.Directory, () => Signal(ReviewLogEvent));

        // Push the named events whenever something changed; otherwise a periodic keep-alive comment keeps the
        // connection observably alive. Exits on server shutdown or client disconnect.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await wake.WaitAsync(KeepAliveInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            string[] due;
            lock (pending)
            {
                due = pending.OrderBy(name => name, StringComparer.Ordinal).ToArray();
                pending.Clear();
            }

            var frames = due.Length == 0
                ? new[] { AnnotationApi.SseComment("keep-alive") }
                : due.Select(name => AnnotationApi.SseEvent(name, name + "-changed")).ToArray();

            try
            {
                foreach (var frame in frames)
                {
                    await output.WriteAsync(frame, ct).ConfigureAwait(false);
                }

                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                break; // The client disconnected or the server is shutting down.
            }
        }
    }

    // The SSE event names. `reload` navigates the page (the plan itself changed); `review-log` only tells the
    // panel to re-read the fold, so a teammate's comment arriving never discards a half-typed note.
    private const string ReloadEvent = "reload";
    private const string ReviewLogEvent = "review-log";

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
    /// Both watches are name-filtered and non-recursive — the plan directory is watched for one directory NAME,
    /// exactly as the plan watcher watches it for one file name. Never a tree walk (Lavish's lesson). Every arm
    /// is best-effort: a watcher this stream could not create must never cost the reviewer their live-reload
    /// connection.
    /// </para>
    /// </remarks>
    private sealed class ReviewLogWatch : IDisposable
    {
        private readonly object _gate = new();
        private readonly string _directory;
        private readonly Action _onChange;
        private FileSystemWatcher? _logs;
        private FileSystemWatcher? _arrival;
        private bool _disposed;

        public ReviewLogWatch(string directory, Action onChange)
        {
            _directory = directory;
            _onChange = onChange;

            lock (_gate)
            {
                ArmLogs();
                if (_logs is null)
                {
                    ArmArrival();
                }
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
            if (_disposed || _logs is not null || string.IsNullOrEmpty(_directory))
            {
                return;
            }

            try
            {
                if (!Directory.Exists(_directory))
                {
                    return;
                }

                var watcher = new FileSystemWatcher(_directory, ReviewLogPaths.LogSearchPattern)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                        | NotifyFilters.CreationTime,
                };
                watcher.Changed += (_, _) => _onChange();
                watcher.Created += (_, _) => _onChange();
                watcher.Deleted += (_, _) => _onChange();
                watcher.Renamed += (_, _) => _onChange();
                watcher.EnableRaisingEvents = true;
                _logs = watcher;
            }
            catch (Exception)
            {
                _logs = null;
            }
        }

        // The bridge: the plan's own directory, filtered to the ONE directory name, so the log watch arms the
        // instant .review/ is created instead of on the next page load. Deliberately left armed afterwards —
        // disposing a watcher from inside its own callback is a needless hazard, and a `.review/` removed and
        // restored (a branch switch) then re-arms the log watch for free.
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
                ArmLogs();
            }

            _onChange();
        }
    }

    // The served-page Content-Security-Policy. Distinct from the export CSP: it keeps script-src
    // 'unsafe-inline' (injected Mermaid runtime + annotation SDK) and connect-src 'self' (same-origin /api/*
    // POST + poll), while confining images to self + data: and denying every other remote fetch class.
    private const string ServedPageCsp =
        "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; " +
        "connect-src 'self'; font-src 'self' data:; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";

    private static void WriteSecurityHeaders(HttpListenerResponse response)
    {
        response.Headers["Content-Security-Policy"] = ServedPageCsp;
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static void WriteJson(HttpListenerResponse response, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = payload.Length;
        response.OutputStream.Write(payload, 0, payload.Length);
    }

    /// <summary>
    /// Serialize a DRAINED batch as the JSON body and write it, re-enqueuing the batch via
    /// <paramref name="requeue"/> if the write fails (a disconnected client) — the at-least-once guarantee for
    /// the poll/answers drains, which clear the buffer under lock BEFORE this write. Without the requeue a
    /// mid-write disconnect would lose the exact review artifact the drain exists to carry. Callers set the
    /// response headers; only the body write can fail here, and the exception is rethrown so the shared
    /// handler closes the broken response as before.
    /// </summary>
    private static void WriteDrainedJson<T>(
        HttpListenerResponse response, IReadOnlyList<T> drained, Action<IReadOnlyList<T>> requeue)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(drained, AnnotationApi.JsonOptions));
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = payload.Length;
        WriteBodyOrRequeue(response.OutputStream, payload, () => requeue(drained));
    }

    /// <summary>
    /// Write <paramref name="payload"/> to <paramref name="output"/>; on ANY write failure invoke
    /// <paramref name="onWriteFailure"/> (the drain re-enqueue) and rethrow. The transport-independent core of
    /// the drain write path, so the requeue-on-write-failure is unit-testable against a throwing stream rather
    /// than a flaky client abort.
    /// </summary>
    internal static void WriteBodyOrRequeue(Stream output, byte[] payload, Action onWriteFailure)
    {
        try
        {
            output.Write(payload, 0, payload.Length);
        }
        catch (Exception)
        {
            onWriteFailure();
            throw;
        }
    }

    private static void TrySetInternalError(HttpListenerResponse response)
    {
        try
        {
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
        catch (Exception)
        {
            // Headers were already flushed; the status can no longer be changed.
        }
    }
}
