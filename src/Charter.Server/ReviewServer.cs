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
/// comment-in-place loop.
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
    // Wave-2 SDK is a minimal marked placeholder; the wave-3 annotation SDK replaces the body. The
    // deliverable here is the injection MECHANISM plus the stable data-charter-sdk marker.
    private const string SdkScript =
        "<script data-charter-sdk>/* Charter review SDK — wave-2 placeholder; wave-3 wires the annotation loop. */</script>";

    // How long a /api/poll long-poll waits for an annotation before returning (empty). The store fast-paths
    // when one is already queued, so this only bounds an otherwise-idle wait.
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    // How often the /events stream emits a keep-alive comment when the source file is not changing.
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    private readonly ReviewSession _session;
    private readonly AnnotationStore _store;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private bool _disposed;

    private ReviewServer(ReviewSession session, AnnotationStore store, HttpListener listener, Uri address)
    {
        _session = session;
        _store = store;
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

        var host = options.BindAddress.ToString();
        var port = options.Port > 0 ? options.Port : ReserveEphemeralPort(options.BindAddress);

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{host}:{port}/");
        listener.Start();

        var address = new Uri($"http://{host}:{port}/");

        // One per-session annotation store, held for the server's lifetime: prompts enqueue into it and
        // long-polls drain from it.
        var store = new AnnotationStore();
        return new ReviewServer(session, store, listener, address);
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

        var submission = JsonSerializer.Deserialize<AnnotationApi.PromptSubmission>(body, AnnotationApi.JsonOptions);
        if (submission is null || string.IsNullOrEmpty(submission.AnchorId))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        // Resolve the anchor to its 1-based markdown source line — the deterministic half of the round-trip.
        // Read the plan from the session source and build the same content-derived source map the anchor came
        // from, so the annotation carries the exact line an agent would edit.
        var markdown = await File.ReadAllTextAsync(_session.SourcePath).ConfigureAwait(false);
        var sourceLine = SourceMap.Build(markdown).LineForAnchor(submission.AnchorId);

        var annotation = new Annotation(
            Id: Guid.NewGuid().ToString("N"),
            Kind: AnnotationApi.ParseKind(submission.Kind),
            AnchorId: submission.AnchorId,
            Note: submission.Note ?? string.Empty,
            SourceLine: sourceLine);

        _store.Enqueue(annotation);

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

        try
        {
            await _store.WaitForPendingAsync(PollTimeout, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The server is shutting down; return whatever is currently drained (typically empty).
        }

        var drained = _store.Drain();
        WriteJson(response, JsonSerializer.Serialize(drained, AnnotationApi.JsonOptions));
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

        // Edge-triggered wake signal fed by a watcher on the source FILE (not the whole tree): a change
        // releases the semaphore and the loop pushes a reload event.
        using var reloadSignal = new SemaphoreSlim(0);
        void Signal()
        {
            try
            {
                reloadSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already saturated with pending reloads; the loop will coalesce them.
            }
            catch (ObjectDisposedException)
            {
                // The stream is tearing down; the change no longer matters.
            }
        }

        var directory = Path.GetDirectoryName(_session.SourcePath) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileName(_session.SourcePath) ?? "*";
        using var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        watcher.Changed += (_, _) => Signal();
        watcher.Created += (_, _) => Signal();
        watcher.Renamed += (_, _) => Signal();
        watcher.EnableRaisingEvents = true;

        // Push a reload event whenever the source file changes; otherwise a periodic keep-alive comment keeps
        // the connection observably alive. Exits on server shutdown or client disconnect.
        while (!ct.IsCancellationRequested)
        {
            bool changed;
            try
            {
                changed = await reloadSignal.WaitAsync(KeepAliveInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var frame = changed
                ? AnnotationApi.SseEvent("reload", "source-changed")
                : AnnotationApi.SseComment("keep-alive");

            try
            {
                await output.WriteAsync(frame, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                break; // The client disconnected or the server is shutting down.
            }
        }
    }

    private static void WriteJson(HttpListenerResponse response, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = payload.Length;
        response.OutputStream.Write(payload, 0, payload.Length);
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
