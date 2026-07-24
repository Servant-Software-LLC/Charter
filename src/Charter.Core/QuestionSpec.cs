using System.Text.Json;

namespace Charter.Core;

/// <summary>
/// The answer mode of a <c>:::question</c> block — the five shapes a question can take. The C# names are
/// the stable in-code surface; the wire/token mapping (which authoring-format string selects each member)
/// is pinned by <see cref="QuestionSpec.Parse(string)"/> and the schema tests.
/// </summary>
public enum QuestionMode
{
    /// <summary><c>single</c> — pick exactly one of <see cref="QuestionSpec.Options"/>.</summary>
    SingleSelect,

    /// <summary><c>multi</c> — pick one or more of <see cref="QuestionSpec.Options"/>.</summary>
    MultiSelect,

    /// <summary><c>free-text</c> — a free-form textual answer; options are not used.</summary>
    FreeText,

    /// <summary><c>bool</c> — a yes/no answer; options are not used.</summary>
    Bool,

    /// <summary><c>number</c> — a numeric answer; options are not used.</summary>
    Number,
}

/// <summary>
/// Who a <c>:::question</c> is addressed to: the reviewing <c>human</c>, or the downstream <c>agent</c>
/// that consumes the handed-off charter.
/// </summary>
public enum QuestionTarget
{
    /// <summary><c>human</c> — answered by the human reviewer in the review loop.</summary>
    Human,

    /// <summary><c>agent</c> — answered by the downstream agent at handoff time.</summary>
    Agent,
}

/// <summary>
/// The parsed, validated schema of a <c>:::question</c> block — the single source of truth for a question's
/// shape (invariant: format single-sourced). A well-formed body (JSON, which is a subset of YAML, so the
/// parser stays dependency-agnostic) parses to a <see cref="QuestionSpec"/>; a malformed or schema-invalid
/// body is rejected.
/// </summary>
/// <remarks>
/// <see cref="QuestionSpec"/> is a <em>behavioral</em> type: it parses and validates. The schema it honors:
/// <list type="bullet">
///   <item><description><see cref="Id"/> — required, non-empty.</description></item>
///   <item><description><see cref="Title"/> — required, non-empty.</description></item>
///   <item><description><see cref="Mode"/> — one of the five <see cref="QuestionMode"/> members, selected
///     by the body token <c>single</c>/<c>multi</c>/<c>free-text</c>/<c>bool</c>/<c>number</c>. An unknown
///     token is invalid.</description></item>
///   <item><description><see cref="Options"/> — required and non-empty for
///     <see cref="QuestionMode.SingleSelect"/>/<see cref="QuestionMode.MultiSelect"/>; absent/ignored for
///     the other three modes.</description></item>
///   <item><description><see cref="Target"/> — one of <see cref="QuestionTarget"/> (<c>human</c>/<c>agent</c>).</description></item>
///   <item><description><see cref="Answer"/> — optional. Absent or empty ⇒ the question is <em>open</em>;
///     a non-empty array ⇒ the question is <em>resolved</em> and carries the chosen value(s). When present
///     it must be an array of strings (the same shape as <c>Charter.Server.Answer.Values</c>): a
///     single/bool/number answer is one element, a multi-select is the selected values, and free-text is the
///     text as one element.</description></item>
/// </list>
/// Contract the tests pin: <see cref="Parse(string)"/> throws <see cref="FormatException"/> on any invalid
/// body (malformed JSON or a schema violation); <see cref="Validate(string)"/> never throws for a
/// well-formed-JSON body — it returns <c>(false, error)</c> for a schema violation and <c>(true, null)</c>
/// when the body is a valid question. The <see cref="Answer"/> field is purely additive — a body with no
/// <c>answer</c> key parses exactly as before, as an open question.
/// </remarks>
public sealed record QuestionSpec(
    string Id,
    string Title,
    QuestionMode Mode,
    IReadOnlyList<string> Options,
    QuestionTarget Target)
{
    /// <summary>
    /// The resolved answer value(s), or an empty list when the question is still open. A non-empty
    /// <see cref="Answer"/> is the on-disk marker that a <c>:::question</c> has been decided; it is spliced
    /// into the block's JSON body by <see cref="QuestionResolution.Apply(string, IReadOnlyDictionary{string, IReadOnlyList{string}})"/>.
    /// Shape matches <c>Charter.Server.Answer.Values</c>.
    /// </summary>
    public IReadOnlyList<string> Answer { get; init; } = Array.Empty<string>();
    /// <summary>
    /// Parse and validate a question <paramref name="body"/> (JSON/YAML) into a <see cref="QuestionSpec"/>.
    /// Throws <see cref="FormatException"/> if the body is malformed or violates the schema (missing id,
    /// unknown mode, a select mode with no options, and so on).
    /// </summary>
    public static QuestionSpec Parse(string body)
    {
        var (spec, error) = TryParse(body);
        if (spec is null)
        {
            throw new FormatException(error ?? "The :::question body is not a valid question.");
        }

        return spec;
    }

    /// <summary>
    /// Validate a question <paramref name="body"/> without throwing on a schema violation: returns
    /// <c>(true, null)</c> for a well-formed question and <c>(false, error)</c> when the body breaks the
    /// schema. The load-bearing negative surface the schema tests assert against.
    /// </summary>
    public static (bool Ok, string? Error) Validate(string body)
    {
        var (spec, error) = TryParse(body);
        return (spec is not null, error);
    }

    /// <summary>
    /// The non-throwing parse the renderer and handoff use to DEGRADE a malformed <c>:::question</c> to a
    /// visible placeholder instead of aborting the whole render/emit: returns <c>true</c> with the parsed
    /// <paramref name="spec"/> for a well-formed question, or <c>false</c> with <paramref name="spec"/> null
    /// and a human-readable <paramref name="error"/> reason for a malformed or schema-invalid body.
    /// </summary>
    public static bool TryParse(string body, out QuestionSpec? spec, out string? error)
    {
        (spec, error) = TryParse(body);
        return spec is not null;
    }

    /// <summary>
    /// The single validation kernel behind both entry points: parses the JSON body and checks every schema
    /// rule, returning either the built spec (<c>error</c> null) or a null spec with a human-readable reason.
    /// </summary>
    private static (QuestionSpec? Spec, string? Error) TryParse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, "The :::question body is empty.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            return (null, $"The :::question body is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "The :::question body must be a JSON object.");
            }

            var id = ReadString(root, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                return (null, "\"id\" is required and must be a non-empty string.");
            }

            var title = ReadString(root, "title");
            if (string.IsNullOrWhiteSpace(title))
            {
                return (null, "\"title\" is required and must be a non-empty string.");
            }

            var modeToken = ReadString(root, "mode");
            if (string.IsNullOrWhiteSpace(modeToken))
            {
                return (null, "\"mode\" is required and must be a non-empty string.");
            }

            if (!TryParseMode(modeToken, out var mode))
            {
                return (null, $"\"mode\" value \"{modeToken}\" is not one of single/multi/free-text/bool/number.");
            }

            var targetToken = ReadString(root, "target");
            if (string.IsNullOrWhiteSpace(targetToken))
            {
                return (null, "\"target\" is required and must be a non-empty string.");
            }

            if (!TryParseTarget(targetToken, out var target))
            {
                return (null, $"\"target\" value \"{targetToken}\" is not one of human/agent.");
            }

            var (options, optionsError) = ReadOptions(root);
            if (optionsError is not null)
            {
                return (null, optionsError);
            }

            var requiresOptions = mode is QuestionMode.SingleSelect or QuestionMode.MultiSelect;
            if (requiresOptions && options.Count == 0)
            {
                return (null, $"\"options\" is required and must be non-empty for mode \"{modeToken}\".");
            }

            var (answer, answerError) = ReadStringArray(root, "answer");
            if (answerError is not null)
            {
                return (null, answerError);
            }

            return (new QuestionSpec(id, title, mode, options, target) { Answer = answer }, null);
        }
    }

    /// <summary>Reads the string value of <paramref name="name"/>, or null when absent or not a string.</summary>
    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>
    /// Reads the optional <c>options</c> array. Returns an empty list when the field is absent or JSON null;
    /// returns an error when it is present but not an array of strings.
    /// </summary>
    private static (IReadOnlyList<string> Options, string? Error) ReadOptions(JsonElement root)
    {
        if (!root.TryGetProperty("options", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return (Array.Empty<string>(), null);
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return (Array.Empty<string>(), "\"options\" must be an array of strings.");
        }

        var options = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return (Array.Empty<string>(), "\"options\" must contain only strings.");
            }

            options.Add(item.GetString()!);
        }

        return (options, null);
    }

    /// <summary>
    /// Reads an optional array-of-strings field <paramref name="name"/> (used for <c>answer</c>). Returns an
    /// empty list when the field is absent or JSON null; returns an error when it is present but not an array
    /// of strings.
    /// </summary>
    private static (IReadOnlyList<string> Values, string? Error) ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return (Array.Empty<string>(), null);
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return (Array.Empty<string>(), $"\"{name}\" must be an array of strings.");
        }

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return (Array.Empty<string>(), $"\"{name}\" must be an array of strings.");
            }

            values.Add(item.GetString()!);
        }

        return (values, null);
    }

    /// <summary>Maps a body <paramref name="token"/> to its <see cref="QuestionMode"/>; false when unknown.</summary>
    private static bool TryParseMode(string token, out QuestionMode mode)
    {
        switch (token)
        {
            case "single":
                mode = QuestionMode.SingleSelect;
                return true;
            case "multi":
                mode = QuestionMode.MultiSelect;
                return true;
            case "free-text":
                mode = QuestionMode.FreeText;
                return true;
            case "bool":
                mode = QuestionMode.Bool;
                return true;
            case "number":
                mode = QuestionMode.Number;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    /// <summary>Maps a body <paramref name="token"/> to its <see cref="QuestionTarget"/>; false when unknown.</summary>
    private static bool TryParseTarget(string token, out QuestionTarget target)
    {
        switch (token)
        {
            case "human":
                target = QuestionTarget.Human;
                return true;
            case "agent":
                target = QuestionTarget.Agent;
                return true;
            default:
                target = default;
                return false;
        }
    }
}
