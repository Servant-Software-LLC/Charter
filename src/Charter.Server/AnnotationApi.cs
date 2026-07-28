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
    /// The JSON body a reviewer POSTs to <c>/api/{key}/annotations/{id}</c> to EDIT a still-pending
    /// annotation's note (Charter #42's in-page panel). Only the note is editable — the anchor, kind and
    /// resolved source line are the submission's identity and stay immutable, so the edit cannot re-point a
    /// note at a different block.
    /// </summary>
    /// <param name="Note">The reviewer's replacement note text.</param>
    public sealed record NoteUpdate(string? Note);

    /// <summary>
    /// The accepted <c>kind</c> wire tokens, in catalog order, for an error message that TELLS a client what it
    /// should have sent. Read from the same map that parses and serializes them, so the message can never name a
    /// token the server does not actually accept.
    /// </summary>
    public static IReadOnlyList<string> KindTokens => AnnotationKindConverter.Tokens;

    /// <summary>
    /// The STRICT inbound parse, used at the one ingress where a client chooses the kind
    /// (<c>POST /api/{key}/prompts</c>): <c>true</c> with the matching kind for one of the SDK's hyphenated
    /// tokens (<c>element</c> / <c>text-range</c> / <c>diagram-node</c>, case-insensitive), <c>true</c> with
    /// <see cref="AnnotationKind.Element"/> for an ABSENT/empty token (unspecified means the whole block — the
    /// documented <c>{ anchorId, note }</c> submission shape), and <c>false</c> for anything else.
    /// </summary>
    /// <remarks>
    /// Charter #79: this used to fall back to <see cref="AnnotationKind.Element"/> for an unrecognized token, so
    /// a client sending the camelCase <c>"textRange"</c> — the spelling the skill documented for a while, and the
    /// one a default C#/JS enum-name convention produces — got <b>HTTP 200 and a whole-element annotation</b>,
    /// while its <c>quote</c>/<c>start</c>/<c>end</c> stayed on the record contradicting the stored kind. A
    /// malformed annotation is a client bug worth surfacing; storing a different kind than the client asked for
    /// is the failure mode this repo has repeatedly paid for. The camelCase spelling is deliberately NOT accepted
    /// as an alias: a second legal spelling forks the token vocabulary that the SDK emits and the skills document,
    /// and a 400 naming the three tokens fixes the client permanently in one round trip.
    /// </remarks>
    public static bool TryParseKind(string? kind, out AnnotationKind parsed)
        => AnnotationKindConverter.TryParse(kind, out parsed);

    /// <summary>
    /// The LENIENT parse, for reading a kind token off a DURABLE record Charter already holds — a review-log
    /// entry (<c>ReviewLogDrain</c>). Unknown tokens degrade to <see cref="AnnotationKind.Element"/> rather than
    /// throwing, because the alternative there is dropping a comment somebody actually wrote; there is no client
    /// to answer with a 400. New client input must go through <see cref="TryParseKind"/> instead.
    /// </summary>
    public static AnnotationKind ParseKind(string? kind) => AnnotationKindConverter.Parse(kind);

    /// <summary>
    /// The wire token for <paramref name="kind"/>, from the SAME map that serializes it — so a review-log
    /// record's <c>anchor.kind</c> and the annotation API's <c>kind</c> can never drift apart.
    /// </summary>
    public static string KindToken(AnnotationKind kind) => AnnotationKindConverter.Token(kind);

    /// <summary>
    /// The single source of truth mapping between <see cref="AnnotationKind"/> and the browser SDK's hyphenated
    /// wire tokens (<c>element</c> / <c>text-range</c> / <c>diagram-node</c> — see
    /// <c>sdk/charter-annotate.js</c>'s <c>KIND</c>). Registered in <see cref="JsonOptions"/> so the kind
    /// serializes as its SDK token and deserializes back from it, and reused by <see cref="TryParseKind"/> and
    /// <see cref="ParseKind"/> so no inbound string parse can drift from the outbound token.
    /// </summary>
    /// <remarks>
    /// <see cref="Read"/> stays LENIENT on purpose even though the HTTP ingress does not. It deserializes state
    /// Charter itself wrote (the durability sidecar), and <c>ReviewSidecar.Rehydrate</c> answers ANY exception
    /// with <c>State.Empty</c> — so a converter that threw on one bad token would silently discard the
    /// reviewer's entire queue. Losing notes to a strict read is far worse than reading one corrupt kind as
    /// <c>element</c>.
    /// </remarks>
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

        /// <summary>The accepted tokens in catalog order — the vocabulary a rejection message quotes.</summary>
        public static IReadOnlyList<string> Tokens { get; } = ToToken.Values.ToArray();

        /// <summary>
        /// Strict: an SDK token maps to its kind, an absent/blank token means the whole block
        /// (<see cref="AnnotationKind.Element"/>), and anything else is a REFUSAL — never a silent downgrade.
        /// </summary>
        public static bool TryParse(string? token, out AnnotationKind kind)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                kind = AnnotationKind.Element;
                return true;
            }

            return FromToken.TryGetValue(token, out kind);
        }

        /// <summary>Map an SDK token to its kind, defaulting an unknown/missing token to Element.</summary>
        public static AnnotationKind Parse(string? token)
            => token is not null && FromToken.TryGetValue(token, out var kind) ? kind : AnnotationKind.Element;

        /// <summary>Map a kind back to its SDK token — the exact inverse of <see cref="Parse"/>.</summary>
        public static string Token(AnnotationKind kind) => ToToken[kind];

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
