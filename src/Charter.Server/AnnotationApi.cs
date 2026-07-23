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
    /// case-insensitive reads). The <see cref="AnnotationKindConverter"/> is listed FIRST so it wins over the
    /// generic camelCase enum converter for <see cref="AnnotationKind"/> — the kind serializes as the SDK's
    /// hyphenated wire token (<c>element</c> / <c>text-range</c> / <c>diagram-node</c>), so an
    /// <see cref="Annotation"/> round-trips as <c>{ "anchorId": ..., "sourceLine": ..., "kind": "text-range" }</c>
    /// exactly as the browser SDK sent it. The camelCase enum converter still covers every other enum.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new AnnotationKindConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
    };

    /// <summary>
    /// The JSON body a reviewer POSTs to <c>/api/{key}/prompts</c>: the anchor, the note, and the optional
    /// sub-part fidelity payload that distinguishes WHICH part of the block was flagged. The fidelity members
    /// are nullable with defaults so a block-level submission (<c>{ kind, anchorId, note }</c>) still binds and
    /// leaves them null; because they are now declared, System.Text.Json binds them instead of skipping them.
    /// </summary>
    /// <param name="Kind">The annotation kind (<c>element</c>, <c>text-range</c>, <c>diagram-node</c>).</param>
    /// <param name="AnchorId">The stable, content-derived block anchor the annotation targets.</param>
    /// <param name="Note">The reviewer's free-text note.</param>
    /// <param name="Quote">Text-range only: the selected text within the block, or <c>null</c>.</param>
    /// <param name="Start">Text-range only: the selection's start offset within the block, or <c>null</c>.</param>
    /// <param name="End">Text-range only: the selection's end offset within the block, or <c>null</c>.</param>
    /// <param name="NodeId">Diagram-node only: the flagged node's identity within the diagram, or <c>null</c>.</param>
    public sealed record PromptSubmission(
        string? Kind,
        string? AnchorId,
        string? Note,
        string? Quote = null,
        int? Start = null,
        int? End = null,
        string? NodeId = null);

    /// <summary>
    /// Parse a submitted kind string to an <see cref="AnnotationKind"/> using the SAME map that serializes it
    /// back out (<see cref="AnnotationKindConverter"/>), so inbound and outbound share one source of truth: the
    /// SDK's hyphenated tokens (<c>element</c> / <c>text-range</c> / <c>diagram-node</c>, case-insensitive) map
    /// to the matching kind, and an unknown/missing token defaults leniently to <see cref="AnnotationKind.Element"/>.
    /// </summary>
    public static AnnotationKind ParseKind(string? kind) => AnnotationKindConverter.Parse(kind);

    /// <summary>
    /// The single source of truth mapping between <see cref="AnnotationKind"/> and the browser SDK's hyphenated
    /// wire tokens (<c>element</c> / <c>text-range</c> / <c>diagram-node</c> — see
    /// <c>sdk/charter-annotate.js</c>'s <c>KIND</c>). Registered in <see cref="JsonOptions"/> so the kind
    /// serializes as its SDK token and deserializes back from it, and reused by <see cref="ParseKind"/> so the
    /// inbound string parse cannot drift from the outbound token. An unrecognized token reads as
    /// <see cref="AnnotationKind.Element"/> — the same leniency the raw <c>Enum.TryParse</c> gave.
    /// </summary>
    private sealed class AnnotationKindConverter : JsonConverter<AnnotationKind>
    {
        private static readonly IReadOnlyDictionary<AnnotationKind, string> ToToken =
            new Dictionary<AnnotationKind, string>
            {
                [AnnotationKind.Element] = "element",
                [AnnotationKind.TextRange] = "text-range",
                [AnnotationKind.DiagramNode] = "diagram-node",
            };

        private static readonly IReadOnlyDictionary<string, AnnotationKind> FromToken =
            ToToken.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

        /// <summary>Map an SDK token to its kind, defaulting an unknown/missing token to Element.</summary>
        public static AnnotationKind Parse(string? token)
            => token is not null && FromToken.TryGetValue(token, out var kind) ? kind : AnnotationKind.Element;

        public override AnnotationKind Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => Parse(reader.GetString());

        public override void Write(Utf8JsonWriter writer, AnnotationKind value, JsonSerializerOptions options)
            => writer.WriteStringValue(ToToken[value]);
    }

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
