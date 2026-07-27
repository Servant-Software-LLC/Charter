using System.Text;
using System.Text.Json;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Builds review-log JSONL lines the way the writer will: one compact JSON object per line. The tests
/// deliberately drive the fold through REAL lines rather than hand-built <see cref="ReviewRecord"/>s, so
/// every fold test also exercises the parser the on-disk logs will meet.
/// </summary>
internal static class Rec
{
    /// <summary>A fixed stamp, so a test that does not care about time cannot accidentally depend on it.</summary>
    public const string DefaultTs = "2026-07-26T10:45:12Z";

    public const string Alice = "alice@example.com";
    public const string Bob = "bob@example.com";
    public const string Carol = "carol@example.com";

    /// <summary>One record line, with every field the caller asked for and nothing else.</summary>
    public static string Record(
        string op,
        string id,
        string author,
        string? target = null,
        string? prev = null,
        string? body = null,
        string? blockId = null,
        string? ts = DefaultTs,
        int v = 1,
        string actor = "human",
        string? extraJson = null)
    {
        var fields = new List<string>
        {
            $"\"v\":{v}",
            $"\"id\":{J(id)}",
            $"\"op\":{J(op)}",
        };

        if (ts is not null)
        {
            fields.Add($"\"ts\":{J(ts)}");
        }

        fields.Add($"\"author\":{{\"name\":{J(NameOf(author))},\"email\":{J(author)}}}");
        fields.Add($"\"actor\":{J(actor)}");

        if (blockId is not null)
        {
            fields.Add($"\"anchor\":{{\"blockId\":{J(blockId)},\"kind\":\"element\",\"quote\":\"the read path\",\"base\":\"sha256:1f4c\"}}");
        }

        if (target is not null)
        {
            fields.Add($"\"target\":{J(target)}");
        }

        if (prev is not null)
        {
            fields.Add($"\"prev\":{J(prev)}");
        }

        if (body is not null)
        {
            fields.Add($"\"body\":{J(body)}");
        }

        if (extraJson is not null)
        {
            fields.Add(extraJson);
        }

        return "{" + string.Join(",", fields) + "}";
    }

    public static string Create(string id, string author, string body = "Is Postgres right here?", string blockId = "b92bb0c5", int v = 1, string actor = "human", string? ts = DefaultTs, string? extraJson = null)
        => Record("create", id, author, body: body, blockId: blockId, v: v, actor: actor, ts: ts, extraJson: extraJson);

    public static string Reply(string id, string target, string author, string body = "Good point.", int v = 1, string actor = "human", string? ts = DefaultTs)
        => Record("reply", id, author, target: target, body: body, v: v, actor: actor, ts: ts);

    public static string Edit(string id, string target, string author, string body, string? prev = null, int v = 1)
        => Record("edit", id, author, target: target, prev: prev, body: body, v: v);

    public static string Resolve(string id, string target, string author, string? prev = null, int v = 1, string? ts = DefaultTs)
        => Record("resolve", id, author, target: target, prev: prev, v: v, ts: ts);

    public static string Reopen(string id, string target, string author, string? prev = null, int v = 1, string? ts = DefaultTs)
        => Record("reopen", id, author, target: target, prev: prev, v: v, ts: ts);

    public static string Retract(string id, string target, string author, string? prev = null, int v = 1)
        => Record("retract", id, author, target: target, prev: prev, v: v);

    /// <summary>A log file with the given lines, in the given order.</summary>
    public static ReviewLogSource Source(string fileName, params string[] lines)
        => new(fileName, lines);

    /// <summary>The single-file fold shorthand most tests want.</summary>
    public static ReviewLogState Fold(params string[] lines)
        => ReviewLog.Fold([Source("alice.4f2a91c8.jsonl", lines)]);

    /// <summary>The one comment the fold produced, failing loudly when there is not exactly one.</summary>
    public static ReviewComment OnlyComment(this ReviewLogState state)
        => Assert.Single(state.Comments);

    /// <summary>Every diagnostic of one kind.</summary>
    public static IReadOnlyList<ReviewDiagnostic> OfKind(this ReviewLogState state, ReviewDiagnosticKind kind)
        => state.Diagnostics.Where(d => d.Kind == kind).ToList();

    /// <summary>
    /// A deterministic textual projection of a fold result, for the order-independence property test.
    /// <paramref name="includeLocations"/> is off by default because a record's file and line legitimately
    /// change when git merges the logs in another order — the STATE is what must not.
    /// </summary>
    public static string Canonical(ReviewLogState state, bool includeLocations = false)
    {
        var text = new StringBuilder();
        foreach (var comment in state.Comments)
        {
            text.Append("comment ").Append(comment.Id)
                .Append(" by ").Append(comment.Author.Email)
                .Append(" actor=").Append(comment.Actor)
                .Append(" anchor=").Append(comment.Anchor.BlockId)
                .Append(" status=").Append(comment.Status)
                .Append(" resolution=").Append(comment.Resolution)
                .Append(" body=").Append(comment.Body ?? "<none>")
                .Append(" settledBy=[")
                .Append(string.Join("|", comment.ResolutionRecords.Select(r => $"{r.Id}:{r.Op}:{r.Author.Email}")))
                .Append("] retractedBy=").Append(comment.RetractRecord?.Id ?? "<none>")
                .Append('\n');

            foreach (var reply in comment.Replies)
            {
                text.Append("  reply ").Append(reply.Id)
                    .Append(" by ").Append(reply.Author.Email)
                    .Append(" inReplyTo=").Append(reply.InReplyTo)
                    .Append(" retracted=").Append(reply.IsRetracted)
                    .Append(" body=").Append(reply.Body ?? "<none>")
                    .Append('\n');
            }
        }

        foreach (var diagnostic in state.Diagnostics)
        {
            text.Append("diagnostic ").Append(diagnostic.Kind)
                .Append(' ').Append(diagnostic.RecordId ?? "-")
                .Append(' ').Append(diagnostic.TargetId ?? "-")
                .Append(' ').Append(diagnostic.Message);

            if (includeLocations)
            {
                text.Append(" at ").Append(diagnostic.FileName).Append(':').Append(diagnostic.LineNumber);
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>A display name derived from the email, so records carry a name without the tests naming one.</summary>
    private static string NameOf(string email) => email.Split('@')[0];

    /// <summary>A JSON string literal (or <c>null</c>) for <paramref name="value"/>.</summary>
    private static string J(string? value) => value is null ? "null" : JsonSerializer.Serialize(value);
}
