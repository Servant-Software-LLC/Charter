using System.Text.Json.Serialization;

namespace Charter.Server;

/// <summary>
/// A reviewer's submitted answer to a <c>:::question</c> block, queued in the <see cref="AnswerStore"/>
/// awaiting the author/agent handoff. Unlike an <see cref="Annotation"/> (anchored to a parsed block), an
/// answer's identity is its client-chosen <paramref name="QuestionId"/> — an opaque id, not a source-map
/// resolution — so the round-trip is a pure echo of what the reviewer submitted.
/// </summary>
/// <param name="QuestionId">Opaque, client-chosen id of the question being answered.</param>
/// <param name="Mode">The question's selection mode (e.g. <c>single-select</c>, <c>multi-select</c>).</param>
/// <param name="Values">The selected option values.</param>
/// <param name="Target">
/// Where the answer routes on handoff — <c>human</c> or <c>agent</c>. Stored and echoed verbatim for the
/// downstream handoff to route on; wave-4 does not interpret it beyond preserving it.
/// </param>
/// <param name="QuestionFingerprint">
/// <see cref="Charter.Core.QuestionIdentity"/>'s fingerprint of the <c>:::question</c> AS THE REVIEWER WAS ASKED
/// IT — its id, title, mode, target, and options, but not its answer — captured server-side when the answer was
/// submitted (Charter #75 item 3). It is the answer's counterpart to an annotation's anchor: the evidence
/// <c>charter resolve</c> / <c>poll --apply</c> check before folding a decision into a plan, so an answer written
/// against a question that has since been replaced under the same id is SURFACED rather than silently written.
/// <see langword="null"/> means no evidence — a pre-upgrade queue, or a question that was not parseable when the
/// answer was taken — and every consumer treats that as "apply exactly as before".
/// <b>Server-computed, never client-supplied:</b> the answers route overwrites whatever a client sends here, so
/// the evidence cannot be forged by the thing it is evidence about.
/// </param>
public sealed record Answer(
    string QuestionId,
    string Mode,
    IReadOnlyList<string> Values,
    string Target,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? QuestionFingerprint = null);
