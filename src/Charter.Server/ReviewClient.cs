using System.Globalization;
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
/// native long-poll cycle, then <c>GET /api/answers?key=…</c> and <c>GET /api/review?key=…</c> (the
/// reviewer's explicit round hand-off).
/// </remarks>
public sealed class ReviewClient : IDisposable
{
    // Short bound on the liveness probe: a live loopback server answers instantly, so anything slower is
    // treated as unresponsive rather than blocking the caller.
    /// <summary>
    /// How long one <c>GET /api/sessions</c> may take before the probe gives up and reports
    /// <see cref="ProbeOutcome.Unknown"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is defence in depth, NOT the fix for Charter #217</b> — that is the three-valued
    /// <see cref="ProbeResult"/>, which changes what the probe CLAIMS when it cannot tell. Raising a timeout
    /// only changes how OFTEN it cannot tell, and the two are indistinguishable from outside because the
    /// symptom gets rarer either way. Do not let a future reader mistake this constant for the remedy.
    /// </para>
    /// <para>
    /// <b>Raising it is free in the case that matters.</b> A genuinely absent server refuses the connection
    /// and returns immediately via <c>HttpRequestException</c> — it never waits out this deadline. So the
    /// only run this lengthens is one where something IS listening but slow, which is precisely the run that
    /// deserves patience. It was 3s, which lost to a loaded machine (the same shape as the 15s readiness
    /// gates in Charter #216, one layer down).
    /// </para>
    /// </remarks>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

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
    /// Prove the session is live by calling <c>GET /api/sessions?key=…</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three outcomes, not two, and the third is the whole point (Charter #217).</b> This returned a
    /// nullable <see cref="PollSession"/>, so <i>"nothing is listening"</i> and <i>"I could not tell"</i> were
    /// the same value — and the caller pruned the session descriptor on it, with the comment
    /// <c>// stale hint — remove it</c>. Under load a probe against a LIVE review server timed out, the CLI
    /// reported exit 3 (<c>NoSession</c>) — <i>the server is not running</i> — and then <b>deleted the
    /// descriptor that proved otherwise</b>, so the first wrong answer made every later answer wrong too and
    /// the reviewer's live session became unreachable while their browser was still being served by it.
    /// </para>
    /// <para>
    /// <b>A timeout is not evidence of absence; it is evidence of not knowing.</b> Charter #147 already
    /// settled this shape once — a pid is a NEGATIVE signal only — for the same reason: weak evidence must not
    /// be treated as proof when the action taken on it destroys the thing that would disprove it.
    /// </para>
    /// <para>
    /// <b>Raising <see cref="ProbeTimeout"/> is NOT the fix</b> and must never be mistaken for one. It changes
    /// how OFTEN the probe cannot tell, never what it claims when it cannot. The two are indistinguishable
    /// from outside — the symptom gets rarer either way — which is exactly why the structural change is the
    /// one that ships.
    /// </para>
    /// </remarks>
    public async Task<ProbeResult> ProbeAsync(string? expectedSourcePath, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProbeTimeout);

        string body;
        try
        {
            using var response = await _http.GetAsync(Route("api/sessions"), cts.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                // 401 (wrong key) or any non-200. The server ANSWERED — that is positive evidence this is not
                // a session this key may drain, which is a different fact from silence.
                return ProbeResult.Absent;
            }

            body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Connection refused: nothing is listening on that port. Positive evidence of absence — the one
            // case where pruning a descriptor is sound.
            return ProbeResult.Absent;
        }
        catch (OperationCanceledException)
        {
            // Timed out, or the caller cancelled. NOTHING was learned about whether a session is there, so
            // this may not be reported as absence and may not prune the descriptor.
            return ProbeResult.Unknown;
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
            // A 200 whose body is not a session descriptor: something else holds this port. Evidence, not
            // silence — so Absent rather than Unknown.
            return ProbeResult.Absent;
        }

        if (string.IsNullOrEmpty(sourcePath))
        {
            return ProbeResult.Absent;
        }

        // A recycled port could land the descriptor's key on a different session; require the live server to
        // confirm it serves the same source before trusting the descriptor. A live server serving a DIFFERENT
        // source is positive evidence that this descriptor is stale.
        if (expectedSourcePath is not null && !PathsEqual(sourcePath, expectedSourcePath))
        {
            return ProbeResult.Absent;
        }

        return ProbeResult.Live(
            new PollSession(_base.ToString(), sourcePath, sourceFile ?? Path.GetFileName(sourcePath)));
    }

    /// <summary>
    /// Drain queued annotations. When <paramref name="wait"/> is false (the default), uses <c>wait=0</c> for
    /// an immediate, non-blocking drain; when true, runs one native long-poll cycle. A transport/parse failure
    /// is reported in <see cref="DrainOutcome{T}.Error"/> (NOT swallowed to an empty list), so the caller can
    /// tell a genuinely empty queue apart from a drain that could not complete and never proceeds on a false
    /// "nothing queued" (§DA-weak-4).
    /// </summary>
    public async Task<DrainOutcome<Annotation>> DrainAnnotationsAsync(bool wait, CancellationToken cancellationToken)
    {
        var route = wait
            ? $"api/poll?key={Escaped(_key)}"
            : $"api/poll?key={Escaped(_key)}&wait=0";
        return await DrainAsync<Annotation>(route, "annotations", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// PEEK queued <c>:::question</c> answers via <c>GET /api/answers?key=…</c> — a non-destructive report
    /// that leaves the answers in the store. They are removed server-side only by
    /// <see cref="CommitAnswersAsync"/>, after the caller has durably applied them. A transport/parse failure
    /// is surfaced in <see cref="DrainOutcome{T}.Error"/> rather than swallowed to empty.
    /// </summary>
    public async Task<DrainOutcome<Answer>> PeekAnswersAsync(CancellationToken cancellationToken)
        => await DrainAsync<Answer>($"api/answers?key={Escaped(_key)}", "answers", cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Read the reviewer's pending round HAND-OFF via <c>GET /api/review?key=…</c> — the in-page "Send to
    /// agent" click, which tells the agent "the human is done with this round" rather than "one more comment
    /// arrived". Non-destructive: the hand-off is cleared only by <see cref="AckReviewSubmissionAsync"/>, so a
    /// poll that dies before acking re-reports it (the safe direction) instead of losing it.
    /// </summary>
    /// <remarks>
    /// A transport/parse failure is surfaced in <see cref="DrainOutcome{T}.Error"/> like the other drains — a
    /// hand-off Charter could not read must not be reported as "the human has not handed off". A <c>404</c> is
    /// the ONE exception: it means the running server predates this route (a session started by an older
    /// <c>charter review</c>), which is a capability gap, not a failed drain, so it reads as "no hand-off".
    /// </remarks>
    public async Task<DrainOutcome<ReviewSubmission>> PeekReviewSubmissionAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http
                .GetAsync(Route("api/review"), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return DrainOutcome<ReviewSubmission>.Success(Array.Empty<ReviewSubmission>());
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return DrainOutcome<ReviewSubmission>.Failure(
                    $"the review server returned {(int)response.StatusCode} reading the review hand-off");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("submission", out var submission) ||
                submission.ValueKind != JsonValueKind.Object)
            {
                return DrainOutcome<ReviewSubmission>.Success(Array.Empty<ReviewSubmission>());
            }

            var handoff = submission.Deserialize<ReviewSubmission>(AnnotationApi.JsonOptions);
            return DrainOutcome<ReviewSubmission>.Success(
                handoff is null ? Array.Empty<ReviewSubmission>() : new[] { handoff });
        }
        catch (HttpRequestException ex)
        {
            return DrainOutcome<ReviewSubmission>.Failure(
                $"could not reach the review server reading the review hand-off ({ex.Message})");
        }
        catch (OperationCanceledException)
        {
            return DrainOutcome<ReviewSubmission>.Failure(
                "timed out reading the review hand-off from the review server");
        }
        catch (JsonException ex)
        {
            return DrainOutcome<ReviewSubmission>.Failure(
                $"could not parse the review hand-off response ({ex.Message})");
        }
    }

    /// <summary>
    /// Clear the reported hand-off via <c>POST /api/{key}/review/ack?sequence=…</c>, so it does not re-fire on
    /// every later poll. Compare-and-clear by <paramref name="sequence"/>: a round the reviewer handed off
    /// AFTER this one was reported is newer and survives. Returns <c>true</c> on a 200. A failure is NON-fatal
    /// — the hand-off simply stays pending and is reported again next poll (at-least-once, the safe
    /// direction) — so this returns <c>false</c> rather than throwing.
    /// </summary>
    public async Task<bool> AckReviewSubmissionAsync(long sequence, CancellationToken cancellationToken)
    {
        var uri = new Uri(_base, $"api/{Escaped(_key)}/review/ack?sequence={sequence}");
        try
        {
            using var content = new StringContent(string.Empty);
            using var response = await _http.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Commit (remove) the front <paramref name="count"/> answers — the prefix the caller peeked and has now
    /// durably written into the plan — via <c>POST /api/{key}/answers/ack?count=…</c>. Returns <c>true</c> on
    /// a 200. A failure is NON-fatal to the caller: the answers simply stay queued and a re-run re-applies
    /// them idempotently, so this returns <c>false</c> rather than throwing.
    /// </summary>
    public async Task<bool> CommitAnswersAsync(int count, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return true;
        }

        var uri = new Uri(_base, $"api/{Escaped(_key)}/answers/ack?count={count}");
        try
        {
            using var content = new StringContent(string.Empty);
            using var response = await _http.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<DrainOutcome<T>> DrainAsync<T>(
        string route, string label, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(_base, route), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return DrainOutcome<T>.Failure($"the review server returned {(int)response.StatusCode} draining {label}");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var items = JsonSerializer.Deserialize<List<T>>(body, AnnotationApi.JsonOptions) ?? new List<T>();

            // #117 — the ack sequence rides a header so the body stays the bare array every consumer parses.
            // Absent (an older server, or an empty batch) means 0, i.e. nothing to acknowledge.
            long sequence = 0;
            if (response.Headers.TryGetValues(ReviewServer.DrainSequenceHeader, out var values))
            {
                foreach (var value in values)
                {
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence))
                    {
                        break;
                    }
                }
            }

            return DrainOutcome<T>.Success(items) with { Sequence = sequence };
        }
        catch (HttpRequestException ex)
        {
            return DrainOutcome<T>.Failure($"could not reach the review server draining {label} ({ex.Message})");
        }
        catch (OperationCanceledException)
        {
            return DrainOutcome<T>.Failure($"timed out draining {label} from the review server");
        }
        catch (JsonException ex)
        {
            return DrainOutcome<T>.Failure($"could not parse the {label} drain response ({ex.Message})");
        }
    }

    /// <summary>
    /// Release the annotation batch identified by <paramref name="sequence"/> — the caller has the envelope
    /// and it is safe to forget (#117). Best-effort: a failed ack leaves the batch IN FLIGHT and the next
    /// drain re-delivers it, which is the safe direction and mirrors how the answers commit already behaves.
    /// </summary>
    public async Task AckAnnotationsAsync(long sequence, CancellationToken cancellationToken)
    {
        if (sequence <= 0)
        {
            return;
        }

        try
        {
            var route = $"api/{Escaped(_key)}/annotations/ack?sequence={sequence}";
            using var content = new StringContent(string.Empty);
            using var response = await _http
                .PostAsync(new Uri(_base, route), content, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // The batch stays in flight and is re-delivered. Never fatal.
        }
        catch (OperationCanceledException)
        {
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
