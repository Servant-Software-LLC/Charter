using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Charter.Server;

/// <summary>
/// Stateless helpers for the annotation HTTP API that <see cref="ReviewServer"/> routes: the shared JSON
/// contract, the submitted-prompt payload shape, the CSRF/same-origin decision, and the tiny Server-Sent
/// Events frame encoders. The routing and per-session state (the <see cref="AnnotationStore"/>) live on
/// <see cref="ReviewServer"/>; only the pure, side-effect-free pieces live here.
/// </summary>
internal static class AnnotationApi
{
    /// <summary>
    /// One shared serializer contract for every annotation endpoint: web defaults (camelCase names,
    /// case-insensitive reads) plus enums as their camelCase names, so an <see cref="Annotation"/> round-trips
    /// as <c>{ "anchorId": ..., "sourceLine": ..., "kind": "element" }</c> — the shape the browser SDK and the
    /// API tests read.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The JSON body a reviewer POSTs to <c>/api/{key}/prompts</c>: the anchor and the note.</summary>
    /// <param name="Kind">The annotation kind (<c>element</c>, <c>text-range</c>, <c>diagram-node</c>).</param>
    /// <param name="AnchorId">The stable, content-derived block anchor the annotation targets.</param>
    /// <param name="Note">The reviewer's free-text note.</param>
    public sealed record PromptSubmission(string? Kind, string? AnchorId, string? Note);

    /// <summary>Parse a submitted kind string to an <see cref="AnnotationKind"/>, defaulting to Element.</summary>
    public static AnnotationKind ParseKind(string? kind)
        => Enum.TryParse<AnnotationKind>(kind, ignoreCase: true, out var parsed) ? parsed : AnnotationKind.Element;

    /// <summary>
    /// The CSRF gate for the state-changing prompts route: allow a request whose <c>Origin</c> is same-origin
    /// with the loopback server (or absent — a non-browser client such as the CLI never sends one), and refuse
    /// any request carrying a foreign, cross-site <c>Origin</c> even when it presents a valid capability key.
    /// </summary>
    public static bool IsAllowedOrigin(string? origin, Uri serverAddress)
    {
        // No Origin header — not a cross-site browser POST (a same-origin fetch may omit it; curl/the CLI do).
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
            && string.Equals(
                parsed.GetLeftPart(UriPartial.Authority),
                serverAddress.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Encode a named Server-Sent Event with a data payload (<c>event: …\ndata: …\n\n</c>).</summary>
    public static byte[] SseEvent(string name, string data)
        => Encoding.UTF8.GetBytes($"event: {name}\ndata: {data}\n\n");

    /// <summary>Encode a Server-Sent Events comment line (<c>: …\n\n</c>) — a no-op keep-alive heartbeat.</summary>
    public static byte[] SseComment(string text)
        => Encoding.UTF8.GetBytes($": {text}\n\n");
}
