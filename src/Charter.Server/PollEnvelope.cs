using System.Text.Json;

namespace Charter.Server;

/// <summary>
/// The live session a <c>charter poll</c> envelope reports on: the keyless loopback address and the source
/// plan under review. The capability key is deliberately absent — it never appears in <c>poll</c> stdout.
/// </summary>
/// <param name="Address">The keyless loopback base address, e.g. <c>http://127.0.0.1:PORT/</c>.</param>
/// <param name="SourcePath">The canonical path of the plan under review.</param>
/// <param name="SourceFile">The plan's file name.</param>
public sealed record PollSession(string Address, string SourcePath, string SourceFile);

/// <summary>
/// Serializes the single JSON envelope <c>charter poll</c> always writes to stdout: the live
/// <see cref="PollSession"/> (or <c>null</c> for no session), the drained annotations and answers as the
/// VERBATIM server wire shapes, and the drained counts. Using <see cref="AnnotationApi.JsonOptions"/> keeps
/// the nested annotation/answer objects byte-identical to what the server itself emits (DRY — no reshaping),
/// so the hyphenated <c>kind</c> tokens and camelCase fields match the annotation API exactly.
/// </summary>
public static class PollEnvelope
{
    /// <summary>
    /// Build the envelope JSON. <paramref name="session"/> is <c>null</c> when no live session was found, so
    /// an agent always receives parseable JSON with <c>"session": null</c>. <paramref name="drainError"/> is
    /// <c>null</c> on a clean drain and a human-readable reason when a drain could not complete — an agent MUST
    /// treat a non-null <c>drainError</c> as "queue state unknown", never as "nothing queued" (§DA-weak-4).
    /// <paramref name="reviewSubmission"/> is the reviewer's explicit round HAND-OFF (the in-page "Send to
    /// agent" click) when one is pending, or <c>null</c> — it rides the envelope as the additive
    /// <c>reviewSubmitted</c> / <c>reviewSubmission</c> pair, so a consumer that ignores both is unaffected.
    /// </summary>
    public static string Serialize(
        PollSession? session,
        IReadOnlyList<Annotation> annotations,
        IReadOnlyList<Answer> answers,
        string? drainError = null,
        ReviewSubmission? reviewSubmission = null)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(answers);

        var payload = new
        {
            session,
            annotations,
            answers,
            drained = new { annotations = annotations.Count, answers = answers.Count },
            drainError,

            // "The human explicitly handed me this round" vs "I woke because one more comment arrived". The
            // flag is the cheap check; the record carries when the reviewer clicked and how much was queued at
            // that moment. Reported once per hand-off: `charter poll` acks it after emitting this envelope.
            reviewSubmitted = reviewSubmission is not null,
            reviewSubmission,
        };

        return JsonSerializer.Serialize(payload, AnnotationApi.JsonOptions);
    }
}
