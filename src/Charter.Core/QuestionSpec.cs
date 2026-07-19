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
/// STUB (TDD red). <see cref="QuestionSpec"/> is a <em>behavioral</em> type: it parses and validates. The
/// two entry points below are the seams under test and throw <see cref="NotImplementedException"/> for now
/// so the authored schema tests compile against this type and fail (red) until a later task fills them in.
/// The schema the implementer must honor:
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
/// </list>
/// Contract the tests pin: <see cref="Parse(string)"/> throws <see cref="FormatException"/> on any invalid
/// body (malformed JSON or a schema violation); <see cref="Validate(string)"/> never throws for a
/// well-formed-JSON body — it returns <c>(false, error)</c> for a schema violation and <c>(true, null)</c>
/// when the body is a valid question.
/// </remarks>
public sealed record QuestionSpec(
    string Id,
    string Title,
    QuestionMode Mode,
    IReadOnlyList<string> Options,
    QuestionTarget Target)
{
    /// <summary>
    /// Parse and validate a question <paramref name="body"/> (JSON/YAML) into a <see cref="QuestionSpec"/>.
    /// Throws <see cref="FormatException"/> if the body is malformed or violates the schema (missing id,
    /// unknown mode, a select mode with no options, and so on).
    /// </summary>
    public static QuestionSpec Parse(string body) => throw new NotImplementedException();

    /// <summary>
    /// Validate a question <paramref name="body"/> without throwing on a schema violation: returns
    /// <c>(true, null)</c> for a well-formed question and <c>(false, error)</c> when the body breaks the
    /// schema. The load-bearing negative surface the schema tests assert against.
    /// </summary>
    public static (bool Ok, string? Error) Validate(string body) => throw new NotImplementedException();
}
