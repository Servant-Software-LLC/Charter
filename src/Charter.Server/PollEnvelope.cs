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
    /// <summary>The envelope came from a live loopback review session — the shipped path.</summary>
    public const string SessionSource = "session";

    /// <summary>
    /// The envelope was read from the committed review logs beside the plan, with no server running — the
    /// server-less path that lets A's agent read B's committed comments while A is EXECUTING, not reviewing
    /// (<c>docs/plans/03-git-mediated-team-review.md</c> §5).
    /// </summary>
    public const string ReviewLogSource = "review-log";

    /// <summary>
    /// Build the envelope JSON. <paramref name="session"/> is <c>null</c> when no live session was found, so
    /// an agent always receives parseable JSON with <c>"session": null</c>. <paramref name="drainError"/> is
    /// <c>null</c> on a clean drain and a human-readable reason when a drain could not complete — an agent MUST
    /// treat a non-null <c>drainError</c> as "queue state unknown", never as "nothing queued" (§DA-weak-4).
    /// <paramref name="reviewSubmission"/> is the reviewer's explicit round HAND-OFF (the in-page "Send to
    /// agent" click) when one is pending, or <c>null</c> — it rides the envelope as the additive
    /// <c>reviewSubmitted</c> / <c>reviewSubmission</c> pair, so a consumer that ignores both is unaffected.
    /// <paramref name="source"/> is likewise additive and says WHERE the annotations came from: with a null
    /// session it distinguishes "no session, nothing to read" from "no session, and here is what the committed
    /// logs say".
    /// </summary>
    /// <remarks>
    /// The two additions are ORTHOGONAL and both ride here: a hand-off is a property of a LIVE session (only a
    /// running server can carry one), and <c>review-log</c> is by construction the session-less source, so a
    /// <c>review-log</c> envelope always reports <c>reviewSubmitted: false</c>. Keeping them as separate
    /// parameters — rather than folding "source" into the submission — is what lets a future session-backed
    /// read of the log report both.
    /// </remarks>
    public static string Serialize(
        PollSession? session,
        IReadOnlyList<Annotation> annotations,
        IReadOnlyList<Answer> answers,
        string? drainError = null,
        ReviewSubmission? reviewSubmission = null,
        string source = SessionSource)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(answers);

        var payload = new
        {
            session,
            source,
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
