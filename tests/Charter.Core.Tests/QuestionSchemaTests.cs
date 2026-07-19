using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Schema tests for the <c>:::question</c> block's <see cref="QuestionSpec"/> — the single source of truth
/// for a question's shape. These pin the two behaviors that make the schema trustworthy: a well-formed body
/// PARSES to the expected id/title/mode/options/target, and a known-bad body is REJECTED by validation (the
/// anti-tautology teeth of a schema task). Bodies are JSON — a subset of YAML, so a JSON-first parser needs
/// no new dependency and a YAML superset stays possible later.
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","QuestionSchema")].
/// </summary>
[Trait("Category", "QuestionSchema")]
public class QuestionSchemaTests
{
    [Fact]
    public void Parse_WellFormedSingleSelectBody_YieldsExpectedFields()
    {
        // A well-formed :::question body (JSON). Parsing must recover every schema field.
        const string body = """
            {
              "id": "q-primary-color",
              "title": "Which primary color do you prefer?",
              "mode": "single",
              "options": ["Red", "Green", "Blue"],
              "target": "human"
            }
            """;

        var spec = QuestionSpec.Parse(body);

        Assert.Equal("q-primary-color", spec.Id);
        Assert.Equal("Which primary color do you prefer?", spec.Title);
        Assert.Equal(QuestionMode.SingleSelect, spec.Mode);
        Assert.Equal(QuestionTarget.Human, spec.Target);
        Assert.Equal(new[] { "Red", "Green", "Blue" }, spec.Options);
    }

    [Theory]
    [InlineData("single", QuestionMode.SingleSelect)]
    [InlineData("multi", QuestionMode.MultiSelect)]
    [InlineData("free-text", QuestionMode.FreeText)]
    [InlineData("bool", QuestionMode.Bool)]
    [InlineData("number", QuestionMode.Number)]
    public void Parse_AcceptsEachMode(string modeToken, QuestionMode expected)
    {
        // Every one of the five modes must round-trip its token to the matching QuestionMode member.
        // single/multi carry options; free-text/bool/number omit them.
        var spec = QuestionSpec.Parse(BodyForMode(modeToken));

        Assert.Equal(expected, spec.Mode);
    }

    [Theory]
    [InlineData("agent", QuestionTarget.Agent)]
    [InlineData("human", QuestionTarget.Human)]
    public void Parse_ResolvesTarget(string targetToken, QuestionTarget expected)
    {
        const string template = """
            { "id": "q-target", "title": "Who answers?", "mode": "bool", "target": "__T__" }
            """;
        var body = template.Replace("__T__", targetToken, StringComparison.Ordinal);

        var spec = QuestionSpec.Parse(body);

        Assert.Equal(expected, spec.Target);
    }

    [Theory]
    [MemberData(nameof(KnownBadBodies))]
    public void Validate_RejectsKnownBadBody_ReturnsNotOk(string reason, string knownBadBody)
    {
        // The load-bearing negative assertion: a deliberately INVALID body (missing id, unknown mode, a
        // select mode with no options, an empty options list, or an unknown target) is REJECTED. A valid
        // body would return Ok; this must not.
        var (ok, error) = QuestionSpec.Validate(knownBadBody);

        Assert.False(ok, $"expected the {reason} body to be rejected, but validation reported it as valid");
        Assert.NotNull(error);
    }

    [Fact]
    public void Parse_KnownBadBody_MissingId_Throws()
    {
        // The parse entry point rejects an invalid body by throwing (the contract QuestionSpec pins:
        // FormatException on a malformed or schema-invalid body). Complements the non-throwing Validate.
        const string missingId = """
            { "title": "No id here", "mode": "single", "options": ["A", "B"], "target": "human" }
            """;

        Assert.Throws<FormatException>(() => QuestionSpec.Parse(missingId));
    }

    /// <summary>
    /// Deliberately invalid <c>:::question</c> bodies, each tagged with the reason it is unknown/missing/
    /// invalid so a failure names the offending case.
    /// </summary>
    public static IEnumerable<object[]> KnownBadBodies() => new[]
    {
        new object[] { "missing-id", """
            { "title": "No id", "mode": "single", "options": ["A", "B"], "target": "human" }
            """ },
        new object[] { "missing-title", """
            { "id": "q-no-title", "mode": "bool", "target": "human" }
            """ },
        new object[] { "unknown-mode", """
            { "id": "q-bad-mode", "title": "Bad mode", "mode": "carousel", "options": ["A"], "target": "human" }
            """ },
        new object[] { "single-without-options", """
            { "id": "q-no-opts", "title": "Select but no options", "mode": "single", "target": "human" }
            """ },
        new object[] { "multi-with-empty-options", """
            { "id": "q-empty-opts", "title": "Empty options", "mode": "multi", "options": [], "target": "human" }
            """ },
        new object[] { "unknown-target", """
            { "id": "q-bad-target", "title": "Bad target", "mode": "free-text", "target": "robot" }
            """ },
    };

    /// <summary>A well-formed question body for <paramref name="modeToken"/>; select modes carry options.</summary>
    private static string BodyForMode(string modeToken)
    {
        var optionsField = modeToken is "single" or "multi"
            ? ", \"options\": [\"Alpha\", \"Beta\"]"
            : string.Empty;

        return $$"""
            { "id": "q-{{modeToken}}", "title": "A {{modeToken}} question", "mode": "{{modeToken}}", "target": "agent"{{optionsField}} }
            """;
    }
}
