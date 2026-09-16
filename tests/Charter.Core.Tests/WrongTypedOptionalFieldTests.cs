using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #245 — a wrong-typed optional <c>:::question</c> field is dropped silently.
///
/// <para>
/// <c>QuestionSpec</c> reads <c>recommended</c> and <c>rationale</c> through a helper that returns null both
/// when the key is absent and when it is present but not a string. So <c>"rationale": ["the reason"]</c> — the
/// shape a stray trailing comma in a generator produces — parses exactly like no rationale at all. The question
/// renders as an ordinary answerable form, the reasoning appears nowhere, and <c>render</c> exits 0 with an
/// empty stderr. An author told NOT to put reasoning beside the block loses it completely and cannot tell.
/// </para>
/// <para>
/// These pin <see cref="QuestionResolution.FindWrongTypedOptionalFields"/>, the lint that makes the drop
/// visible: what it reports, and — as load-bearing as the reports — the three places it must stay quiet.
/// </para>
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","WrongTypedOptionalField")].
/// </summary>
[Trait("Category", "WrongTypedOptionalField")]
public class WrongTypedOptionalFieldTests
{
    // The `:::question` fence sits on line 7: three frontmatter lines, a blank, the heading, a blank.
    private const int QuestionLine = 7;

    private static string Plan(params string[] questionBodies)
    {
        var plan = "---\ncharter-format-version: 1\n---\n\n# Plan\n\n";
        foreach (var body in questionBodies)
        {
            plan += ":::question\n" + body + "\n:::\n\n";
        }

        return plan;
    }

    private static string Question(string extra, string id = "q1")
        => "{\"id\":\"" + id + "\",\"title\":\"T\",\"mode\":\"single\",\"options\":[\"A\",\"B\"],\"target\":\"human\""
            + extra + "}";

    [Fact]
    public void The_premise_holds_the_parser_really_does_drop_a_wrong_typed_rationale()
    {
        // If the parser ever learns to coerce or refuse this, the lint's reason for existing changes — and this
        // test says so, rather than leaving a lint reporting a drop that no longer happens.
        var parsed = QuestionSpec.Parse(Question(",\"rationale\":[\"the reason\"]"));

        Assert.Null(parsed.Rationale);
    }

    [Theory]
    [InlineData("[\"an array\"]", "array")]
    [InlineData("42", "number")]
    [InlineData("{\"why\":\"an object\"}", "object")]
    [InlineData("true", "boolean")]
    public void A_wrong_typed_rationale_is_reported_with_its_type_its_line_and_its_question(string value, string type)
    {
        var dropped = Assert.Single(
            QuestionResolution.FindWrongTypedOptionalFields(Plan(Question(",\"rationale\":" + value))));

        Assert.Equal("q1", dropped.QuestionId);
        Assert.Equal("rationale", dropped.Field);
        Assert.Equal(type, dropped.ActualType);
        Assert.Equal(QuestionLine, dropped.SourceLine);
    }

    [Fact]
    public void A_wrong_typed_recommended_is_reported_too()
    {
        // The issue asked whether the silent drop reached other optional string fields. It does: `recommended`
        // goes through the same helper.
        var dropped = Assert.Single(
            QuestionResolution.FindWrongTypedOptionalFields(Plan(Question(",\"recommended\":42"))));

        Assert.Equal("recommended", dropped.Field);
        Assert.Equal("number", dropped.ActualType);
    }

    [Fact]
    public void A_string_rationale_is_not_reported()
    {
        Assert.Empty(QuestionResolution.FindWrongTypedOptionalFields(
            Plan(Question(",\"recommended\":\"A\",\"rationale\":\"a plain string\""))));
    }

    [Fact]
    public void Json_null_is_not_wrong_typed_because_it_is_the_documented_opt_out()
    {
        // `"recommended": null` is the deliberate, documented "I considered a lean and declined". A lint that
        // flagged it would punish exactly the author who read the docs.
        Assert.Empty(QuestionResolution.FindWrongTypedOptionalFields(
            Plan(Question(",\"recommended\":null,\"rationale\":null"))));
    }

    [Fact]
    public void An_absent_field_is_not_reported_here()
    {
        // Absence is honest, and a missing `recommended` already has its own warning. This lint is only about a
        // value that was WRITTEN and then thrown away.
        Assert.Empty(QuestionResolution.FindWrongTypedOptionalFields(Plan(Question(string.Empty))));
    }

    [Fact]
    public void A_question_the_parser_refuses_is_not_reported()
    {
        // No id: the renderer draws a visible malformed-question placeholder. A warning about one of its optional
        // fields would be noise beside that louder, truer signal.
        var noId = "{\"title\":\"T\",\"mode\":\"single\",\"options\":[\"A\",\"B\"],\"target\":\"human\","
            + "\"rationale\":[\"x\"]}";

        Assert.False(QuestionSpec.TryParse(noId, out _, out _));
        Assert.Empty(QuestionResolution.FindWrongTypedOptionalFields(Plan(noId)));
    }

    [Fact]
    public void Both_fields_wrong_in_one_question_are_both_reported()
    {
        var dropped = QuestionResolution.FindWrongTypedOptionalFields(
            Plan(Question(",\"recommended\":[\"A\"],\"rationale\":{\"why\":1}")));

        Assert.Equal(
            new[] { ("recommended", "array"), ("rationale", "object") },
            dropped.Select(d => (d.Field, d.ActualType)));
    }

    [Fact]
    public void Each_question_is_reported_on_its_own_line_in_document_order()
    {
        var dropped = QuestionResolution.FindWrongTypedOptionalFields(Plan(
            Question(",\"rationale\":[\"x\"]", id: "first"),
            Question(",\"rationale\":\"fine\"", id: "clean"),
            Question(",\"rationale\":7", id: "third")));

        Assert.Equal(new[] { "first", "third" }, dropped.Select(d => d.QuestionId));
        Assert.True(dropped[0].SourceLine < dropped[1].SourceLine, "the later question must report a later line");
        Assert.Equal(QuestionLine, dropped[0].SourceLine);
    }

    [Fact]
    public void The_consequence_says_what_the_reader_actually_loses_for_each_field()
    {
        var dropped = QuestionResolution.FindWrongTypedOptionalFields(
            Plan(Question(",\"recommended\":1,\"rationale\":2")));

        Assert.Contains("reasoning", dropped.Single(d => d.Field == "rationale").Consequence, StringComparison.Ordinal);
        Assert.Contains("recommendation", dropped.Single(d => d.Field == "recommended").Consequence, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_plan_reports_nothing()
    {
        Assert.Empty(QuestionResolution.FindWrongTypedOptionalFields(string.Empty));
    }
}
