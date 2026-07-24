using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Schema tests for the <see cref="QuestionSpec.Answer"/> field — the on-disk open/resolved marker of a
/// living-document <c>:::question</c> (Architecture B §1.2). A non-empty <c>answer</c> array means the
/// question is RESOLVED and carries its chosen value(s); an absent or empty <c>answer</c> means it is still
/// OPEN. The field is purely additive: a body with no <c>answer</c> key must parse exactly as it did before
/// the field existed, so nothing in the earlier schema surface regresses.
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","QuestionAnswerSchema")].
/// </summary>
[Trait("Category", "QuestionAnswerSchema")]
public class QuestionAnswerSchemaTests
{
    [Fact]
    public void Parse_BodyWithoutAnswer_IsOpen()
    {
        // As authored: no answer key. The question is OPEN — Answer is an empty (never null) list.
        const string body = """
            { "id": "db-choice", "title": "Which datastore?", "mode": "single",
              "options": ["Postgres", "DynamoDB"], "target": "human" }
            """;

        var spec = QuestionSpec.Parse(body);

        Assert.NotNull(spec.Answer);
        Assert.Empty(spec.Answer);
    }

    [Fact]
    public void Parse_BodyWithEmptyAnswerArray_IsOpen()
    {
        // An explicit but empty answer array is STILL open — emptiness, not absence, is the open marker.
        const string body = """
            { "id": "db-choice", "title": "Which datastore?", "mode": "single",
              "options": ["Postgres", "DynamoDB"], "target": "human", "answer": [] }
            """;

        var spec = QuestionSpec.Parse(body);

        Assert.Empty(spec.Answer);
    }

    [Fact]
    public void Parse_ResolvedSingleSelect_CarriesTheChosenValue()
    {
        // A resolved single-select: one element. Every other schema field must still parse unchanged.
        const string body = """
            { "id": "db-choice", "title": "Which datastore?", "mode": "single",
              "options": ["Postgres", "DynamoDB"], "target": "human", "answer": ["Postgres"] }
            """;

        var spec = QuestionSpec.Parse(body);

        Assert.Equal(new[] { "Postgres" }, spec.Answer);
        Assert.Equal("db-choice", spec.Id);
        Assert.Equal(QuestionMode.SingleSelect, spec.Mode);
        Assert.Equal(new[] { "Postgres", "DynamoDB" }, spec.Options);
    }

    [Fact]
    public void Parse_ResolvedMultiSelect_CarriesEveryChosenValue()
    {
        // A resolved multi-select carries every selected value, in order.
        const string body = """
            { "id": "regions", "title": "Which regions?", "mode": "multi",
              "options": ["us", "eu", "ap"], "target": "human", "answer": ["us", "eu"] }
            """;

        var spec = QuestionSpec.Parse(body);

        Assert.Equal(new[] { "us", "eu" }, spec.Answer);
    }

    [Fact]
    public void Parse_ResolvedFreeText_CarriesTheTextAsOneElement()
    {
        // Free-text needs no options; its answer is the text as a single element.
        const string body = """
            { "id": "notes", "title": "Any notes?", "mode": "free-text", "target": "human",
              "answer": ["ship it on Friday"] }
            """;

        var spec = QuestionSpec.Parse(body);

        Assert.Equal(QuestionMode.FreeText, spec.Mode);
        Assert.Equal(new[] { "ship it on Friday" }, spec.Answer);
    }

    [Theory]
    [InlineData("\"answer\": \"Postgres\"")]   // a bare string, not an array
    [InlineData("\"answer\": [1, 2]")]           // an array of numbers, not strings
    [InlineData("\"answer\": [\"ok\", 3]")]      // a mixed array with a non-string element
    [InlineData("\"answer\": {}")]                // an object, not an array
    public void Validate_MalformedAnswer_IsRejected(string answerFragment)
    {
        // answer must be an array of strings when present. A wrong-typed answer is a schema violation, not a
        // silently-ignored key — the resolved marker has to be trustworthy.
        var body =
            "{ \"id\": \"q\", \"title\": \"T\", \"mode\": \"single\", " +
            "\"options\": [\"A\", \"B\"], \"target\": \"human\", " + answerFragment + " }";

        var (ok, error) = QuestionSpec.Validate(body);

        Assert.False(ok, "a malformed answer must be rejected");
        Assert.NotNull(error);
    }

    [Fact]
    public void Parse_NullAnswer_IsTreatedAsOpen()
    {
        // A JSON null answer is absence, not a violation — the question is open.
        const string body = """
            { "id": "q", "title": "T", "mode": "bool", "target": "human", "answer": null }
            """;

        var spec = QuestionSpec.Parse(body);

        Assert.Empty(spec.Answer);
    }
}
