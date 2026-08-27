using System.Text.Json;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// The flattened handoff's IN-BAND provenance stamp and the delegated-question instruction — the two things
/// Charter #172 adds to the flatten itself, both because on that path there is no parser and prose is the
/// whole interface.
/// </summary>
/// <remarks>
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","HandoffProvenance")].
/// </remarks>
[Trait("Category", "HandoffProvenance")]
public class HandoffProvenanceTests
{
    private const string Plan = "---\ncharter-format-version: 1\n---\n\n# Plan\n\nSome prose.\n";

    [Fact]
    public void TheFlattenedPlan_CarriesThePlansSha256_AsATrailingComment()
    {
        var output = HandoffMarkdown.Emit(Plan);

        Assert.Contains(HandoffMarkdown.Stamp(Plan), output, StringComparison.Ordinal);
        Assert.EndsWith(HandoffMarkdown.Stamp(Plan), output.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStamp_IsTheSameHashTheHeadlessRecordCalls_planSha256()
    {
        // The whole point of the stamp: it answers "did the plan Charter recorded match the plan Guardrails
        // consumed". If the two hashed different text -- say this one hashed the line-ending-normalized source
        // -- the join would be silently meaningless, which is worse than not having it.
        var record = JsonDocument.Parse(
            HeadlessRecord.Build(Plan, "p.charter.md", "p.charter.html", "0.0.0-test").ToJson());

        var recorded = record.RootElement.GetProperty("planSha256").GetString();

        Assert.Equal($"<!-- {HandoffMarkdown.StampPrefix}{recorded} -->", HandoffMarkdown.Stamp(Plan));
    }

    [Fact]
    public void TheStamp_IsDeterministic_AndChangesOnlyWhenThePlanDoes()
    {
        Assert.Equal(HandoffMarkdown.Emit(Plan), HandoffMarkdown.Emit(Plan));
        Assert.NotEqual(HandoffMarkdown.Stamp(Plan), HandoffMarkdown.Stamp(Plan + "\nOne more line.\n"));
    }

    [Fact]
    public void TheStamp_IsAnHtmlComment_AndNeverReopensADirective()
    {
        var output = HandoffMarkdown.Emit(Plan);

        // A CommonMark leaf HTML block, so wherever raw HTML is rendered -- GitHub's diff and blob views, and
        // any consumer using an ordinary CommonMark pipeline -- it renders as NOTHING. That invisibility is
        // what makes it acceptable to put in a document a human reads.
        Assert.StartsWith("<!--", HandoffMarkdown.Stamp(Plan), StringComparison.Ordinal);
        Assert.EndsWith("-->", HandoffMarkdown.Stamp(Plan), StringComparison.Ordinal);

        // The output still self-parses and no line reopens a directive (invariant 5).
        Assert.DoesNotMatch(@"(?m)^:::", output);
        Assert.Null(Record.Exception(() => CharterRenderer.Render(output)));
    }

    [Fact]
    public void ChartersOwnRenderer_ESCAPESTheStamp_BecauseItEscapesAllProseHtml()
    {
        // Pinned as a known, deliberate consequence rather than left unasserted. Charter's pipeline sets
        // DisableHtml so bare prose HTML can never run or phone home -- the ONE sanctioned exception is a
        // :::custom-html block -- so re-rendering a FLATTENED plan through Charter shows the stamp as visible
        // escaped text. That is the security posture working, not the stamp misbehaving: the flattened .md is
        // Guardrails' input, not a .charter.md, and every consumer that matters renders it as CommonMark.
        var html = CharterRenderer.Render(HandoffMarkdown.Emit(Plan));

        Assert.DoesNotContain("<!-- charter:", html, StringComparison.Ordinal);
        Assert.Contains("&lt;!-- charter: plan-sha256=", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStamp_CarriesNoLocalPathAndNoClock()
    {
        // Same discipline as the forensic record: the flattened plan must stay as safe to hand on as the
        // artifact, so the only thing the stamp adds is a hash.
        var stamp = HandoffMarkdown.Stamp(Plan);

        Assert.Matches(@"^<!-- charter: plan-sha256=[0-9a-f]{64} -->$", stamp);
    }

    [Fact]
    public void ADelegatedQuestion_TellsTheAgentToDecideIt_RatherThanCallingItOpen()
    {
        // "Open question (unresolved)" reads as something SOMEONE ELSE will settle -- exactly wrong for a block
        // whose `target` says the reader settles it. On this path there is no routing table to honour; the
        // prose is the interface, so it has to say what to do.
        var output = HandoffMarkdown.Emit(
            "# Plan\n\n:::question\n{\"id\":\"cache\",\"title\":\"Which cache?\",\"mode\":\"single\","
            + "\"target\":\"agent\",\"options\":[\"Redis\",\"in-memory\"],\"recommended\":\"Redis\"}\n:::\n");

        // Bound to the PRODUCER's constant, never a re-typed literal (Charter #219). This assertion was
        // written against "Delegated decision" and survived the marker changing shape underneath it by
        // being about a string nobody emits any more.
        Assert.Contains(HandoffMarkdown.DelegatedDecisionMarker, output, StringComparison.Ordinal);
        Assert.Contains("`cache`", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Open question (unresolved)", output, StringComparison.Ordinal);

        // The instruction names the mode's actual action and the author's lean.
        Assert.Contains("choose exactly one of the options above", output, StringComparison.Ordinal);
        Assert.Contains("leans `Redis`", output, StringComparison.Ordinal);

        // And the metadata line is unchanged in shape -- it is the routing interface, pinned elsewhere.
        Assert.Contains("id: `cache`", output, StringComparison.Ordinal);
        Assert.Contains("target: `agent`", output, StringComparison.Ordinal);
        Assert.Contains("options: `Redis`, `in-memory`", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("free-text", "write the answer yourself")]
    [InlineData("bool", "decide yes or no")]
    [InlineData("number", "supply a number")]
    public void TheDelegatedInstruction_IsPhrasedForTheQuestionsOwnMode(string mode, string expected)
    {
        var output = HandoffMarkdown.Emit(
            "# Plan\n\n:::question\n{\"id\":\"x\",\"title\":\"T\",\"mode\":\"" + mode
            + "\",\"target\":\"agent\"}\n:::\n");

        Assert.Contains(expected, output, StringComparison.Ordinal);
        Assert.Contains("The plan's author gave no lean.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AHumanQuestion_IsStillFlaggedOpen_AndGainsNoInstruction()
    {
        // The attended path is untouched: an open human question is exactly what it was.
        var output = HandoffMarkdown.Emit(
            "# Plan\n\n:::question\n{\"id\":\"db\",\"title\":\"Which database?\",\"mode\":\"single\","
            + "\"target\":\"human\",\"options\":[\"Postgres\",\"MySQL\"]}\n:::\n");

        Assert.Contains("Open question (unresolved)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Delegated decision", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Decide:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnANSWEREDAgentQuestion_IsStillJustAnswered()
    {
        // A settled decision needs no instruction, whoever it was routed to.
        var output = HandoffMarkdown.Emit(
            "# Plan\n\n:::question\n{\"id\":\"cache\",\"title\":\"Which cache?\",\"mode\":\"single\","
            + "\"target\":\"agent\",\"options\":[\"Redis\",\"in-memory\"],\"answer\":[\"Redis\"]}\n:::\n");

        Assert.Contains("Answered: Redis", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Delegated decision", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDelegatedInstruction_NeverReopensADirective_HoweverTheOptionsAreSpelled()
    {
        var output = HandoffMarkdown.Emit(
            "# Plan\n\n:::question\n{\"id\":\"x\",\"title\":\"T\",\"mode\":\"single\",\"target\":\"agent\","
            + "\"options\":[\":::note\",\"::: still not a fence\"],\"recommended\":\":::note\"}\n:::\n");

        Assert.DoesNotMatch(@"(?m)^:::", output);
        Assert.Null(Record.Exception(() => CharterRenderer.Render(output)));
    }
}
