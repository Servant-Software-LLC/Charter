using System.Net;
using System.Text.Json;

namespace Charter.Server;

/// <summary>
/// The loopback HTTP client <c>charter poll</c> uses to talk to a running <see cref="ReviewServer"/>: it
/// proves a descriptor is LIVE (and serves the expected source), then drains queued annotations and answers.
/// Transport is the BCL <see cref="HttpClient"/> only (zero telemetry, no analytics), scoped to one session's
/// keyless base address plus its capability key.
/// </summary>
/// <remarks>
/// Liveness is proven against <c>GET /api/sessions?key=&lt;key&gt;</c> — the cheapest authenticated route
/// that both validates the key (a wrong key is 401) and identifies the session (the response echoes
/// <c>sourcePath</c>). A connection refusal, timeout, non-200, or a source-path mismatch all mean "not the
/// live session I expected", so the descriptor is treated as stale. Drains use
/// <c>GET /api/poll?key=…&amp;wait=0</c> (immediate, the non-blocking default) or, under <c>--wait</c>, one
/// native long-poll cycle, then <c>GET /api/answers?key=…</c>.
/// </remarks>
public sealed class ReviewClient : IDisposable
{
    // Short bound on the liveness probe: a live loopback server answers instantly, so anything slower is
    // treated as unresponsive rather than blocking the caller.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly string _key;

    /// <summary>Create a client for the session at <paramref name="address"/> holding <paramref name="key"/>.</summary>
    public ReviewClient(Uri address, string key)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrEmpty(key);

        // Normalize to the keyless base (scheme+authority, trailing slash) — the query/key is applied per route.
        _base = new Uri(address.GetLeftPart(UriPartial.Authority) + "/");
        _key = key;

        // Deadlines are enforced per request via CancellationToken; the client timeout is disabled so a
        // deliberate --wait long-poll is not cut short by a global HttpClient timeout.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The keyless loopback base address this client targets.</summary>
    public Uri Address => _base;

    /// <summary>
    /// Build a client from a capability URL of the form <c>http://127.0.0.1:PORT/?key=KEY</c> (the
    /// <c>--url</c> escape hatch). Throws <see cref="FormatException"/> for a malformed URL or a missing key.
    /// </summary>
    public static ReviewClient FromCapabilityUrl(string capabilityUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(capabilityUrl);

        if (!Uri.TryCreate(capabilityUrl, UriKind.Absolute, out var uri))
        {
            throw new FormatException($"'{capabilityUrl}' is not a valid capability URL.");
        }

        var key = ParseKey(uri.Query);
        if (string.IsNullOrEmpty(key))
        {
            throw new FormatException($"The capability URL '{capabilityUrl}' is missing its ?key= parameter.");
        }

        return new ReviewClient(uri, key);
    }

    /// <summary>
    /// Prove the session is live by calling <c>GET /api/sessions?key=…</c>. Returns the live
    /// <see cref="PollSession"/> on success, or <c>null</c> when the server is unreachable, rejects the key,
    /// or (when <paramref name="expectedSourcePath"/> is supplied) serves a different source than the
    /// descriptor claimed.
    /// </summary>
    public async Task<PollSession?> ProbeAsync(string? expectedSourcePath, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProbeTimeout);

        string body;
        try
        {
            using var response = await _http.GetAsync(Route("api/sessions"), cts.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null; // 401 (wrong key) or any non-200 — not a session this key can drain.
            }

            body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null; // Connection refused: nothing listening on that port (stale descriptor).
        }
        catch (OperationCanceledException)
        {
            return null; // Timed out / unresponsive — treat as not live.
        }

        string? sourcePath;
        string? sourceFile;
        try
        {
            using var descriptor = JsonDocument.Parse(body);
            sourcePath = ReadString(descriptor.RootElement, "sourcePath");
            sourceFile = ReadString(descriptor.RootElement, "sourceFile");
        }
        catch (JsonException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(sourcePath))
        {
            return null;
        }

        // A recycled port could land the descriptor's key on a different session; require the live server to
        // confirm it serves the same source before trusting the descriptor.
        if (expectedSourcePath is not null && !PathsEqual(sourcePath, expectedSourcePath))
        {
            return null;
        }

        return new PollSession(_base.ToString(), sourcePath, sourceFile ?? Path.GetFileName(sourcePath));
    }

    /// <summary>
    /// Drain queued annotations. When <paramref name="wait"/> is false (the default), uses <c>wait=0</c> for
    /// an immediate, non-blocking drain; when true, runs one native long-poll cycle. Any transport/parse
    /// failure yields an empty list rather than throwing — a drain that could not complete is "nothing", not
    /// a crash.
    /// </summary>
    public async Task<IReadOnlyList<Annotation>> DrainAnnotationsAsync(bool wait, CancellationToken cancellationToken)
    {
        var route = wait
            ? $"api/poll?key={Escaped(_key)}"
            : $"api/poll?key={Escaped(_key)}&wait=0";
        return await DrainAsync<Annotation>(route, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drain queued <c>:::question</c> answers via <c>GET /api/answers?key=…</c>.</summary>
    public async Task<IReadOnlyList<Answer>> DrainAnswersAsync(CancellationToken cancellationToken)
        => await DrainAsync<Answer>($"api/answers?key={Escaped(_key)}", cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<T>> DrainAsync<T>(string route, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(_base, route), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return Array.Empty<T>();
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<T>>(body, AnnotationApi.JsonOptions) ?? new List<T>();
        }
        catch (HttpRequestException)
        {
            return Array.Empty<T>();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<T>();
        }
        catch (JsonException)
        {
            return Array.Empty<T>();
        }
    }

    /// <summary>Dispose the underlying <see cref="HttpClient"/>.</summary>
    public void Dispose() => _http.Dispose();

    private Uri Route(string pathWithoutQuery) => new(_base, $"{pathWithoutQuery}?key={Escaped(_key)}");

    private static string Escaped(string value) => Uri.EscapeDataString(value);

    // Extract the ?key= value from a URL query string (which includes the leading '?').
    private static string? ParseKey(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            if (string.Equals(pair[..equals], "key", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static bool PathsEqual(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparison);
    }
}
