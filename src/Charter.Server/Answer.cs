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
public sealed record Answer(string QuestionId, string Mode, IReadOnlyList<string> Values, string Target);
