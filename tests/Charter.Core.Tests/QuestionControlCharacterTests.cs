using System.Globalization;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #212 — control characters are refused FORMAT-WIDE, not only on the paths an answer enters through.
///
/// <para>
/// #202 closed every ENTRY channel (the server's answer route, <c>--answers</c>, the inline write) and its
/// claim was exact: <i>no answer that entered through Charter can carry a forbidden character.</i> A
/// <c>:::question</c> hand-authored straight into a <c>.charter.md</c> is the channel those gates do not sit
/// on, and it reproduced the original symptom.
/// </para>
/// <para>
/// <b>The rule is not uniform, and the asymmetry is the point.</b> <c>id</c> · <c>title</c> ·
/// <c>options[]</c> · <c>recommended</c> are emitted onto a SINGLE line and forbid <c>U+000A</c> as well;
/// <c>answer</c> and <c>rationale</c> keep #202's carve-out.
/// </para>
/// <para>
/// <b>No control character is typed literally into this file.</b> Every case is built from a code point and
/// spelled into the JSON as a <c>uXXXX</c> escape, because a raw one in a <c>.cs</c> file is exactly the
/// invisible-payload problem under test — and the next editor, diff tool or line-ending normalisation would
/// rewrite it without a word.
/// </para>
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","QuestionControlCharacter")].
/// </summary>
[Trait("Category", "QuestionControlCharacter")]
public class QuestionControlCharacterTests
{
    private const char CarriageReturn = (char)0x000D;
    private const char LineFeed = (char)0x000A;
    private const char Tab = (char)0x0009;
    private const char Nul = (char)0x0000;
    private const char EscapeChar = (char)0x001B;
    private const char LineSeparator = (char)0x2028;

    /// <summary>The JSON escape for a code point, so no raw control character sits in this source file.</summary>
    private static string J(char value)
        => "\\u" + ((int)value).ToString("x4", CultureInfo.InvariantCulture);

    private static (QuestionSpec? Spec, string? Error) Parse(string json)
    {
        var ok = QuestionSpec.TryParse(json, out var spec, out var error);
        Assert.Equal(ok, spec is not null);
        return (spec, error);
    }

    private static string Question(string fields)
        => "{\"id\":\"q\",\"title\":\"T\",\"mode\":\"free-text\",\"target\":\"human\"" + fields + "}";

    // ---- the single-line fields: NO control character, U+000A included --------------------------------

    [Fact]
    public void Id_RefusesEveryControlCharacter_IncludingNewline()
    {
        var forbidden = new[]
        {
            (Char: CarriageReturn, Name: "carriage return"),
            (Char: LineFeed, Name: "line feed"),
            (Char: Tab, Name: "tab"),
            (Char: Nul, Name: "NUL"),
            (Char: EscapeChar, Name: "escape"),
            (Char: LineSeparator, Name: "line separator"),
        };

        foreach (var (character, name) in forbidden)
        {
            var (spec, error) = Parse(
                "{\"id\":\"a" + J(character) + "b\",\"title\":\"T\",\"mode\":\"free-text\","
                    + "\"target\":\"human\"}");

            Assert.True(spec is null, $"an id carrying a {name} must be refused.");
            Assert.Contains("\"id\"", error!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Title_RefusesANewline_BecauseNoAffordanceProducesOne()
    {
        var (spec, error) = Parse(
            "{\"id\":\"q\",\"title\":\"one" + J(LineFeed) + "two\",\"mode\":\"free-text\","
                + "\"target\":\"human\"}");

        Assert.Null(spec);
        Assert.Contains("\"title\"", error!, StringComparison.Ordinal);

        // The message must say WHY a newline specifically is refused here, since #202 permits one in an answer
        // and a reader hitting this will have that rule in mind.
        Assert.Contains("single line", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOption_RefusesAControlCharacter()
    {
        var (spec, error) = Parse(
            "{\"id\":\"q\",\"title\":\"T\",\"mode\":\"single\",\"target\":\"human\","
                + "\"options\":[\"Redis\",\"in" + J(Tab) + "memory\"]}");

        Assert.Null(spec);
        Assert.Contains("\"options\"", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Recommended_IsRefusedOutright_NotSilentlyDropped()
    {
        // It would ALSO be dropped by the verbatim-options match -- but that is an ordering dependency
        // between two rules, not a property of this field, and this test exists so relaxing that match cannot
        // silently reopen the hole.
        var (spec, error) = Parse(
            "{\"id\":\"q\",\"title\":\"T\",\"mode\":\"single\",\"target\":\"human\","
                + "\"options\":[\"Redis\"],\"recommended\":\"Re" + J(Nul) + "dis\"}");

        Assert.Null(spec);
        Assert.Contains("\"recommended\"", error!, StringComparison.Ordinal);
    }

    // ---- the carve-out fields: #202's rule, unchanged --------------------------------------------------

    [Fact]
    public void AnAnswer_MayStillCarryANewline()
    {
        // The #202 carve-out, and this test is what stops #212 quietly revoking it: a free-text answer is
        // typed into a textarea, which is a reviewer affordance that legitimately produces line breaks.
        var (spec, error) = Parse(Question(",\"answer\":[\"line one" + J(LineFeed) + "line two\"]"));

        Assert.Null(error);
        Assert.NotNull(spec);
        Assert.Equal("line one" + LineFeed + "line two", Assert.Single(spec!.Answer));
    }

    [Fact]
    public void AnAnswer_StillRefusesEveryOtherControlCharacter()
    {
        var (spec, error) = Parse(Question(",\"answer\":[\"alpha" + J(CarriageReturn) + "beta\"]"));

        Assert.Null(spec);
        Assert.Contains("\"answer\"", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Rationale_MayCarryANewline_ButNotANul()
    {
        // #212 listed rationale as already safe "because HandoffMarkdown.Inline collapses it". True of LINE
        // BREAKS only -- Inline replaces CR/LF with a space and touches nothing else, so a NUL travels
        // through it untouched and reaches the flatten.
        var (withNewline, newlineError) =
            Parse(Question(",\"rationale\":\"because" + J(LineFeed) + "reasons\""));
        Assert.Null(newlineError);
        Assert.NotNull(withNewline);

        var (withNul, nulError) = Parse(Question(",\"rationale\":\"because" + J(Nul) + "reasons\""));
        Assert.Null(withNul);
        Assert.Contains("\"rationale\"", nulError!, StringComparison.Ordinal);
    }

    // ---- what reaches the flatten ---------------------------------------------------------------------

    [Fact]
    public void TheFlatten_DegradesToAPlaceholder_InsteadOfEmittingACorruptMarkerLine()
    {
        // The reproduction from #212's own comment, on the shape #219 made structural: before this rule, a CR
        // in `id` landed INSIDE the delegated-decision marker line, and because CommonMark ends a line on a
        // lone CR the line SPLIT -- so the Guardrails gate's regex, which needs both of the id's backticks on
        // one line, matched nothing while the plan genuinely carried a delegated decision.
        var output = HandoffMarkdown.Emit(
            "# Plan\n\n:::question\n"
                + "{\"id\":\"ca" + J(CarriageReturn) + "che\",\"title\":\"Which cache?\",\"mode\":\"single\","
                + "\"target\":\"agent\",\"options\":[\"Redis\",\"in-memory\"],\"recommended\":\"Redis\"}\n"
                + ":::\n");

        Assert.Contains("Malformed question", output, StringComparison.Ordinal);
        Assert.DoesNotContain(HandoffMarkdown.DelegatedDecisionMarker, output, StringComparison.Ordinal);

        // And no count line, because there is now no delegated decision to count -- the declared total and
        // the markers must agree, and they do here by both being absent.
        Assert.DoesNotContain(HandoffMarkdown.DelegatedCountMarker, output, StringComparison.Ordinal);

        // THE LOAD-BEARING ONE: the offending character must not reach the handed-off document at all --
        // not in the placeholder's echo of the value, not anywhere.
        Assert.DoesNotContain(CarriageReturn, output);
    }

    [Fact]
    public void ThePlaceholder_EchoesTheValueESCAPED_NeverRaw()
    {
        // Printing the character being complained about would tear the line explaining it -- the rule #202
        // established for the answer gates' messages, reached here from the format side.
        var (_, error) = Parse(
            "{\"id\":\"ca" + J(CarriageReturn) + "che\",\"title\":\"T\",\"mode\":\"free-text\","
                + "\"target\":\"human\"}");

        Assert.Contains("ca\\rche", error!, StringComparison.Ordinal);
        Assert.DoesNotContain(CarriageReturn, error!);
    }
}
