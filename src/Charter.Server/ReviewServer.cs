using System.Net;
using System.Net.Sockets;
using System.Text;
using Charter.Core;

namespace Charter.Server;

/// <summary>
/// A loopback HTTP server that serves one <see cref="ReviewSession"/>'s rendered, SDK-injected plan and
/// only honours requests presenting the session's <see cref="CapabilityKey"/>. Confinement is enforced by
/// <see cref="PathConfinement"/> so no request can escape the session's root.
/// </summary>
/// <remarks>
/// Transport is <see cref="HttpListener"/> — a BCL primitive, so Charter stays a lean, AOT-friendly binary
/// with no ASP.NET Core framework reference. The plan is re-read and re-rendered from
/// <see cref="ReviewSession.SourcePath"/> on every request, so editing the source and refreshing shows the
/// update (wave-2 "live reload"; the push-based SSE reload lands in wave 3).
/// </remarks>
public sealed class ReviewServer : IReviewServer
{
    // Wave-2 SDK is a minimal marked placeholder; the wave-3 annotation SDK replaces the body. The
    // deliverable here is the injection MECHANISM plus the stable data-charter-sdk marker.
    private const string SdkScript =
        "<script data-charter-sdk>/* Charter review SDK — wave-2 placeholder; wave-3 wires the annotation loop. */</script>";

    private readonly ReviewSession _session;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private bool _disposed;

    private ReviewServer(ReviewSession session, HttpListener listener, Uri address)
    {
        _session = session;
        _listener = listener;
        Address = address;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The loopback URI the server is listening on (host <c>127.0.0.1</c>, OS-chosen port).</summary>
    public Uri Address { get; }

    /// <summary>
    /// Start a loopback HTTP server for <paramref name="session"/>, serving its rendered + SDK-injected
    /// plan. Uses <paramref name="options"/> (or loopback-only, ephemeral-port defaults when null).
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
        return new ReviewServer(session, listener, address);
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

            Handle(context);
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            // Gate 1 — capability key. The key rides the ?key= query string (the capability-URL pattern), so
            // a process that merely guesses the ephemeral port still cannot read the served plan.
            if (!_session.Key.Matches(context.Request.QueryString["key"]))
            {
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                return;
            }

            // Gate 2 — path confinement (defense-in-depth; the authoritative proof is the unit test). The
            // transport normalizes ../ away, so this mainly rejects paths that canonicalize outside the root.
            var requestPath = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath ?? "/").TrimStart('/');
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
