namespace Charter.Server;

/// <summary>
/// What a starting review server rehydrated from its durability sidecar (Charter #120).
/// </summary>
/// <remarks>
/// <para>
/// The sidecar's guarantee — "a <c>charter review</c> crash before drain must lose nothing" — held perfectly
/// in the incident that produced this type. A reviewer's server was force-killed, the plan was re-served on a
/// new port, and all six of their answers were still there. They just could not SEE any of it, so they
/// concluded their work had been destroyed. That is the failure this reports against: recovery that nobody
/// mentions is indistinguishable from loss, and the rational response to apparent loss is to re-enter
/// everything or to stop trusting the tool.
/// </para>
/// <para>
/// Counts only what was actually restored INTO this session. A queue quarantined as belonging to a different
/// document is not restored, and <see cref="StaleAnnotationQueue"/> speaks for that case with its own message.
/// </para>
/// </remarks>
/// <param name="Annotations">Queued comments rehydrated into the live store.</param>
/// <param name="Answers">Queued <c>:::question</c> answers rehydrated into the live store.</param>
public sealed record RestoredQueue(int Annotations, int Answers);
