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
    /// an agent always receives parseable JSON with <c>"session": null</c>.
    /// </summary>
    public static string Serialize(
        PollSession? session, IReadOnlyList<Annotation> annotations, IReadOnlyList<Answer> answers)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(answers);

        var payload = new
        {
            session,
            annotations,
            answers,
            drained = new { annotations = annotations.Count, answers = answers.Count },
        };

        return JsonSerializer.Serialize(payload, AnnotationApi.JsonOptions);
    }
}
