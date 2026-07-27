using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Charter.Core;

/// <summary>
/// The review operations Charter understands, plus <see cref="Unknown"/> for any op token a NEWER Charter
/// wrote and this one does not recognise. Unknown is a first-class member because fold rule 6 requires an
/// unrecognised op to be retained and reported rather than dropped — see
/// <c>docs/plans/03-git-mediated-team-review.md</c> §3 (the fold's hard rules), which is normative.
/// </summary>
public enum ReviewOpKind
{
    /// <summary>An op token this Charter does not recognise: retained, reported, never applied (rule 6).</summary>
    Unknown = 0,

    /// <summary><c>create</c> — opens a comment. Carries <c>anchor</c> and <c>body</c>.</summary>
    Create,

    /// <summary><c>reply</c> — a reply in a comment's thread. Carries <c>target</c> and <c>body</c>.</summary>
    Reply,

    /// <summary><c>edit</c> — replaces the body of its <c>target</c>. A state record: carries <c>prev</c>.</summary>
    Edit,

    /// <summary><c>resolve</c> — closes its <c>target</c>. A state record: carries <c>prev</c>.</summary>
    Resolve,

    /// <summary><c>reopen</c> — re-opens its <c>target</c>. A state record: carries <c>prev</c>.</summary>
    Reopen,

    /// <summary><c>retract</c> — the author withdraws their own item. A state record: carries <c>prev</c>.</summary>
    Retract,
}

/// <summary>
/// The single source of truth mapping between <see cref="ReviewOpKind"/> and the on-the-wire <c>op</c>
/// token, so the writer and the fold cannot drift apart on the spelling of an operation.
/// </summary>
public static class ReviewOps
{
    /// <summary>The <c>op</c> token for <see cref="ReviewOpKind.Create"/>.</summary>
    public const string Create = "create";

    /// <summary>The <c>op</c> token for <see cref="ReviewOpKind.Reply"/>.</summary>
    public const string Reply = "reply";

    /// <summary>The <c>op</c> token for <see cref="ReviewOpKind.Edit"/>.</summary>
    public const string Edit = "edit";

    /// <summary>The <c>op</c> token for <see cref="ReviewOpKind.Resolve"/>.</summary>
    public const string Resolve = "resolve";

    /// <summary>The <c>op</c> token for <see cref="ReviewOpKind.Reopen"/>.</summary>
    public const string Reopen = "reopen";

    /// <summary>The <c>op</c> token for <see cref="ReviewOpKind.Retract"/>.</summary>
    public const string Retract = "retract";

    /// <summary>
    /// Maps an <c>op</c> token to its kind, yielding <see cref="ReviewOpKind.Unknown"/> for anything else —
    /// never throwing, because an unknown op is a forward-compatibility fact, not an error (rule 6).
    /// </summary>
    public static ReviewOpKind Parse(string? token) => token switch
    {
        Create => ReviewOpKind.Create,
        Reply => ReviewOpKind.Reply,
        Edit => ReviewOpKind.Edit,
        Resolve => ReviewOpKind.Resolve,
        Reopen => ReviewOpKind.Reopen,
        Retract => ReviewOpKind.Retract,
        _ => ReviewOpKind.Unknown,
    };

    /// <summary>The exact inverse of <see cref="Parse(string?)"/> for the six known kinds.</summary>
    public static string Token(ReviewOpKind kind) => kind switch
    {
        ReviewOpKind.Create => Create,
        ReviewOpKind.Reply => Reply,
        ReviewOpKind.Edit => Edit,
        ReviewOpKind.Resolve => Resolve,
        ReviewOpKind.Reopen => Reopen,
        ReviewOpKind.Retract => Retract,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown review op has no token."),
    };

    /// <summary>
    /// True for the four ops that settle an item's state and therefore carry <c>prev</c> (§4.2):
    /// <c>edit</c>, <c>resolve</c>, <c>reopen</c>, <c>retract</c>.
    /// </summary>
    public static bool IsStateOp(ReviewOpKind kind)
        => kind is ReviewOpKind.Edit or ReviewOpKind.Resolve or ReviewOpKind.Reopen or ReviewOpKind.Retract;
}

/// <summary>
/// The <c>actor</c> tokens: a record is written either by the reviewing human or by an agent given a voice
/// (§4). Kept as raw strings rather than an enum so an actor token a newer Charter introduces survives the
/// round-trip unchanged instead of collapsing into a default.
/// </summary>
public static class ReviewActors
{
    /// <summary>A record written on behalf of a human reviewer.</summary>
    public const string Human = "human";

    /// <summary>A record written by an agent.</summary>
    public const string Agent = "agent";
}

/// <summary>
/// The record's author, taken from <c>git config user.name</c>/<c>user.email</c> (§4). The email is the
/// identity: the retract-authorship rule (§4.2) compares emails, never names.
/// </summary>
/// <param name="Name">The author's display name; may be empty when git has no <c>user.name</c>.</param>
/// <param name="Email">The author's email — the identity. Required and non-empty.</param>
public sealed record ReviewAuthor(string Name, string Email)
{
    /// <summary>
    /// Unrecognised <c>author</c> members, preserved verbatim as compact JSON so an older Charter cannot
    /// destroy a newer one's data. See <see cref="ReviewRecord.Extensions"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Extensions { get; init; } = ReviewRecord.NoExtensions;
}

/// <summary>
/// Where a comment is attached. Charter resolves an anchor by <b>exact <see cref="BlockId"/> match or it is
/// orphaned</b> — there is no fuzzy ladder (§4.3, withdrawn because it re-introduced the #50 misattribution
/// class). The fold does NOT resolve anchors (that needs the plan); it carries them faithfully so a caller
/// can match exactly and render an informative orphan from <see cref="Quote"/> + <see cref="Base"/>.
/// </summary>
/// <param name="BlockId">The stable, content-derived block id the comment was written against.</param>
/// <param name="Kind">The anchor kind token (<c>element</c>/<c>text-range</c>/<c>diagram-node</c>), uninterpreted here.</param>
/// <param name="Quote">The text the reviewer selected, so an orphan is never blind.</param>
/// <param name="Base">The plan's content hash when the comment was written, for a diff against "since".</param>
public sealed record ReviewAnchor(string BlockId, string? Kind, string? Quote, string? Base)
{
    /// <summary>Unrecognised <c>anchor</c> members, preserved verbatim. See <see cref="ReviewRecord.Extensions"/>.</summary>
    public IReadOnlyDictionary<string, string> Extensions { get; init; } = ReviewRecord.NoExtensions;
}

/// <summary>
/// One line of a per-author review log: an immutable, append-only JSONL record (§4). Records are never
/// mutated — an edit/resolve/reopen/retract/reply is a NEW record, and the current state is a deterministic
/// fold (<see cref="ReviewLog.Fold(IEnumerable{ReviewLogSource})"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Forward compatibility is structural.</b> Any member this Charter does not recognise — at the top
/// level, inside <c>author</c>, or inside <c>anchor</c> — is retained in <see cref="Extensions"/> and
/// written back by <see cref="ToJson"/>, so an older Charter that ever rewrites a log cannot destroy a
/// newer Charter's data (rules 6 and 7). For the same reason <see cref="Op"/> and <see cref="Actor"/> keep
/// their raw tokens; <see cref="OpKind"/> is the interpreted view.
/// </para>
/// <para>
/// <b>Version validation is deliberately asymmetric.</b> A record whose <see cref="Version"/> is the
/// supported one is validated against the op's field schema; a record of any other version is parsed
/// leniently and NOT schema-checked, because this Charter does not know that version's shape. The fold then
/// retains it, reports it, and does not apply it (rule 7).
/// </para>
/// <para>
/// <b>Equality caveat:</b> the compiler-generated equality compares <see cref="Extensions"/> (and the
/// nested extension bags) by reference, so two independently parsed copies of the same line are not
/// <c>Equal</c>. Compare <see cref="ToJson"/> when you need value equality — that is exactly what the fold's
/// duplicate detection does.
/// </para>
/// </remarks>
public sealed record ReviewRecord
{
    /// <summary>The record-schema version this Charter writes and applies. <c>v</c> is per RECORD, not per file (rule 8).</summary>
    public const int CurrentVersion = 1;

    /// <summary>The shared empty extension bag, so an extension-free record allocates nothing.</summary>
    internal static readonly IReadOnlyDictionary<string, string> NoExtensions =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly HashSet<string> KnownRecordKeys = new(StringComparer.Ordinal)
    {
        "v", "id", "op", "ts", "author", "actor", "anchor", "target", "prev", "body",
    };

    private static readonly HashSet<string> KnownAuthorKeys = new(StringComparer.Ordinal) { "name", "email" };

    private static readonly HashSet<string> KnownAnchorKeys = new(StringComparer.Ordinal)
    {
        "blockId", "kind", "quote", "base",
    };

    /// <summary>The record-schema version (<c>v</c>). Per record, not per file (rule 8).</summary>
    public required int Version { get; init; }

    /// <summary>The globally-unique random record id. The fold dedupes on it (rule 1) and <c>target</c>/<c>prev</c> point at it.</summary>
    public required string Id { get; init; }

    /// <summary>The raw <c>op</c> token exactly as written; <see cref="OpKind"/> is the interpreted view.</summary>
    public required string Op { get; init; }

    /// <summary>The author (§4). Required: the retract-authorship rule (§4.2) cannot work without it.</summary>
    public required ReviewAuthor Author { get; init; }

    /// <summary>The interpreted <see cref="Op"/>; <see cref="ReviewOpKind.Unknown"/> for a token from a newer Charter.</summary>
    public ReviewOpKind OpKind => ReviewOps.Parse(Op);

    /// <summary>The raw <c>actor</c> token — see <see cref="ReviewActors"/>. Defaults to <see cref="ReviewActors.Human"/>.</summary>
    public string Actor { get; init; } = ReviewActors.Human;

    /// <summary>
    /// The raw <c>ts</c> string. <b>Presentation only</b> — the fold NEVER uses it for causality (rule 3),
    /// because clocks are unsynchronized and offline review is supported.
    /// </summary>
    public string? Ts { get; init; }

    /// <summary>
    /// <see cref="Ts"/> parsed invariantly to UTC, or null when absent/unparseable. Machine-independent (no
    /// local time zone, no culture) so it can order a thread for display without making the fold's OUTPUT
    /// depend on the machine. Never consulted for state.
    /// </summary>
    public DateTimeOffset? Timestamp => ParseTimestamp(Ts);

    /// <summary>The id this record acts on: the comment/reply for <c>reply</c>/<c>edit</c>/<c>resolve</c>/<c>reopen</c>/<c>retract</c>.</summary>
    public string? Target { get; init; }

    /// <summary>
    /// On a state record, the id of the latest state record its author had OBSERVED for
    /// <see cref="Target"/>, or null for "observed nothing" (§4.2). This is the only causality signal the
    /// fold trusts; a <c>prev</c> naming a record that has not been merged yet counts as unobserved.
    /// </summary>
    public string? Prev { get; init; }

    /// <summary>The comment/reply/edit text.</summary>
    public string? Body { get; init; }

    /// <summary>Where a <c>create</c> is attached (§4.3). Null for every other op.</summary>
    public ReviewAnchor? Anchor { get; init; }

    /// <summary>
    /// Unrecognised top-level members, keyed by name, each value the member's <b>compact JSON text</b>
    /// (canonicalised at parse time so two spacing variants of one record still compare equal). Written back
    /// verbatim by <see cref="ToJson"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Extensions { get; init; } = NoExtensions;

    /// <summary>
    /// Parse one JSONL line into a record. Throws <see cref="FormatException"/> when the line is malformed —
    /// callers folding a log must use <see cref="TryParse(string, out ReviewRecord?, out string?)"/> instead,
    /// since a malformed line is loud but NON-fatal (rule 5).
    /// </summary>
    public static ReviewRecord Parse(string line)
    {
        if (!TryParse(line, out var record, out var error))
        {
            throw new FormatException(error);
        }

        return record!;
    }

    /// <summary>
    /// The non-throwing parse the fold uses: returns true with the parsed <paramref name="record"/>, or
    /// false with a human-readable <paramref name="error"/> and a null record. Never throws.
    /// </summary>
    public static bool TryParse(string line, out ReviewRecord? record, out string? error)
    {
        (record, error) = TryParseCore(line);
        return record is not null;
    }

    /// <summary>
    /// Serialize back to one canonical JSONL line: compact (never pretty-printed — pretty JSON under a union
    /// merge fuses records, §3), ASCII-escaped, and therefore free of raw control bytes and of the NUL that
    /// would make git treat the log as binary. Unrecognised members are written back verbatim.
    /// </summary>
    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteString("id", Id);
            writer.WriteString("op", Op);
            if (Ts is not null)
            {
                writer.WriteString("ts", Ts);
            }

            writer.WriteStartObject("author");
            writer.WriteString("name", Author.Name);
            writer.WriteString("email", Author.Email);
            WriteExtensions(writer, Author.Extensions, KnownAuthorKeys);
            writer.WriteEndObject();

            writer.WriteString("actor", Actor);

            if (Anchor is not null)
            {
                writer.WriteStartObject("anchor");
                writer.WriteString("blockId", Anchor.BlockId);
                WriteOptional(writer, "kind", Anchor.Kind);
                WriteOptional(writer, "quote", Anchor.Quote);
                WriteOptional(writer, "base", Anchor.Base);
                WriteExtensions(writer, Anchor.Extensions, KnownAnchorKeys);
                writer.WriteEndObject();
            }

            WriteOptional(writer, "target", Target);
            WriteOptional(writer, "prev", Prev);
            WriteOptional(writer, "body", Body);
            WriteExtensions(writer, Extensions, KnownRecordKeys);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Writes <paramref name="name"/> only when <paramref name="value"/> is present.</summary>
    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    /// <summary>
    /// Writes preserved unknown members in ordinal name order (so serialization is deterministic), skipping
    /// any that would collide with a known member of the object being written.
    /// </summary>
    private static void WriteExtensions(
        Utf8JsonWriter writer,
        IReadOnlyDictionary<string, string> extensions,
        HashSet<string> knownKeys)
    {
        foreach (var name in extensions.Keys.Where(k => !knownKeys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
        {
            writer.WritePropertyName(name);
            writer.WriteRawValue(extensions[name]);
        }
    }

    /// <summary>The one parse/validate kernel behind <see cref="Parse"/> and <see cref="TryParse"/>.</summary>
    private static (ReviewRecord? Record, string? Error) TryParseCore(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return (null, "The record line is empty.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            return (null, $"The record line is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "A record must be a JSON object.");
            }

            return BuildRecord(root);
        }
    }

    /// <summary>Reads every member of a well-formed JSON object into a record, or reports the first schema violation.</summary>
    private static (ReviewRecord? Record, string? Error) BuildRecord(JsonElement root)
    {
        if (!root.TryGetProperty("v", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var v))
        {
            return (null, "\"v\" is required and must be an integer.");
        }

        var (id, idError) = RequiredString(root, "id");
        if (idError is not null)
        {
            return (null, idError);
        }

        var (op, opError) = RequiredString(root, "op");
        if (opError is not null)
        {
            return (null, opError);
        }

        var (author, authorError) = ReadAuthor(root);
        if (authorError is not null)
        {
            return (null, authorError);
        }

        var (anchor, anchorError) = ReadAnchor(root);
        if (anchorError is not null)
        {
            return (null, anchorError);
        }

        var (fields, fieldsError) = OptionalStrings(root, "ts", "actor", "target", "prev", "body");
        if (fieldsError is not null)
        {
            return (null, fieldsError);
        }

        var record = new ReviewRecord
        {
            Version = v,
            Id = id!,
            Op = op!,
            Author = author!,
            Anchor = anchor,
            Ts = fields[0],
            Actor = fields[1] ?? ReviewActors.Human,
            Target = fields[2],
            Prev = fields[3],
            Body = fields[4],
            Extensions = ReadExtensions(root, KnownRecordKeys),
        };

        // Only THIS version's field schema is known, so only this version's records are schema-checked; a
        // record from another version is parsed leniently and left for the fold to retain-but-not-apply
        // (rule 7). Schema-checking it here would misreport a newer shape as malformed.
        var schemaError = v == CurrentVersion ? ValidateOpSchema(record) : null;
        return schemaError is not null ? (null, schemaError) : (record, null);
    }

    /// <summary>The per-op required-field schema for <see cref="CurrentVersion"/> records (§4).</summary>
    private static string? ValidateOpSchema(ReviewRecord record) => record.OpKind switch
    {
        ReviewOpKind.Create when record.Anchor is null => "A \"create\" record requires an \"anchor\".",
        ReviewOpKind.Create when record.Body is null => "A \"create\" record requires a \"body\".",
        ReviewOpKind.Reply when record.Body is null => "A \"reply\" record requires a \"body\".",
        ReviewOpKind.Edit when record.Body is null => "An \"edit\" record requires a \"body\".",
        ReviewOpKind.Reply or ReviewOpKind.Edit or ReviewOpKind.Resolve or ReviewOpKind.Reopen or ReviewOpKind.Retract
            when string.IsNullOrEmpty(record.Target) => $"A \"{record.Op}\" record requires a \"target\".",
        _ => null,
    };

    /// <summary>Reads the required <c>author</c> object; the email is mandatory because it is the identity.</summary>
    private static (ReviewAuthor? Author, string? Error) ReadAuthor(JsonElement root)
    {
        if (!root.TryGetProperty("author", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return (null, "\"author\" is required and must be an object.");
        }

        var (email, emailError) = RequiredString(element, "email");
        if (emailError is not null)
        {
            return (null, "\"author.email\" is required and must be a non-empty string.");
        }

        var (fields, fieldsError) = OptionalStrings(element, "name");
        if (fieldsError is not null)
        {
            return (null, "\"author.name\" must be a string.");
        }

        var author = new ReviewAuthor(fields[0] ?? string.Empty, email!)
        {
            Extensions = ReadExtensions(element, KnownAuthorKeys),
        };
        return (author, null);
    }

    /// <summary>Reads the optional <c>anchor</c> object; when present it must carry a non-empty <c>blockId</c> (§4.3).</summary>
    private static (ReviewAnchor? Anchor, string? Error) ReadAnchor(JsonElement root)
    {
        if (!root.TryGetProperty("anchor", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return (null, null);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return (null, "\"anchor\" must be an object.");
        }

        var (blockId, blockIdError) = RequiredString(element, "blockId");
        if (blockIdError is not null)
        {
            return (null, "\"anchor.blockId\" is required and must be a non-empty string.");
        }

        var (fields, fieldsError) = OptionalStrings(element, "kind", "quote", "base");
        if (fieldsError is not null)
        {
            return (null, $"\"anchor\" member is not a string: {fieldsError}");
        }

        var anchor = new ReviewAnchor(blockId!, fields[0], fields[1], fields[2])
        {
            Extensions = ReadExtensions(element, KnownAnchorKeys),
        };
        return (anchor, null);
    }

    /// <summary>Reads a required non-empty string member.</summary>
    private static (string? Value, string? Error) RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return (null, $"\"{name}\" is required and must be a non-empty string.");
        }

        var text = value.GetString();
        return string.IsNullOrEmpty(text)
            ? (null, $"\"{name}\" is required and must be a non-empty string.")
            : (text, null);
    }

    /// <summary>
    /// Reads optional string members positionally: absent or JSON null yields null, a non-string value is an
    /// error (a silently-ignored wrong type is exactly the kind of quiet data loss this design rejects).
    /// </summary>
    private static (string?[] Values, string? Error) OptionalStrings(JsonElement element, params string[] names)
    {
        var values = new string?[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            if (!element.TryGetProperty(names[i], out var value) || value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                return (values, $"\"{names[i]}\" must be a string.");
            }

            values[i] = value.GetString();
        }

        return (values, null);
    }

    /// <summary>Collects every member not in <paramref name="knownKeys"/> as canonical compact JSON text.</summary>
    private static IReadOnlyDictionary<string, string> ReadExtensions(JsonElement element, HashSet<string> knownKeys)
    {
        Dictionary<string, string>? extensions = null;
        foreach (var property in element.EnumerateObject())
        {
            if (knownKeys.Contains(property.Name))
            {
                continue;
            }

            extensions ??= new Dictionary<string, string>(StringComparer.Ordinal);
            extensions[property.Name] = Compact(property.Value);
        }

        return extensions ?? NoExtensions;
    }

    /// <summary>Renders <paramref name="element"/> as compact JSON text — the canonical form kept in an extension bag.</summary>
    private static string Compact(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Parses <paramref name="ts"/> invariantly and normalises it to UTC. <c>AssumeUniversal</c> keeps an
    /// offset-less stamp off the local time zone, so the parsed value — and therefore any display ordering
    /// derived from it — is identical on every machine.
    /// </summary>
    private static DateTimeOffset? ParseTimestamp(string? ts)
        => DateTimeOffset.TryParse(
            ts,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
}
