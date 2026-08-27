using System.Text;
using System.Text.RegularExpressions;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #219 — the delegated-decision marker must be findable by the consumer it exists for.
///
/// <para>
/// Every assertion here is over the EMITTED TEXT, never over the constants. A test that asserts
/// <c>DelegatedDecisionMarker</c> is ASCII proves only that a constant is ASCII; the failure this guards
/// against is a non-ASCII byte reaching the file a consumer greps, and the constant is one refactor away from
/// stopping being the thing that lands there. Same reason <c>charter verify</c> matches producer literals
/// against the file rather than re-running <c>Emit</c>.
/// </para>
/// <para>
/// The consumer's gate, in their words: a grep, frequently PowerShell on Windows, pairing a sentinel with an
/// id and checking the total it found against a declared one.
/// </para>
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","DelegatedMarkerGrep")].
/// </summary>
[Trait("Category", "DelegatedMarkerGrep")]
public class DelegatedMarkerGrepTests
{
    private const string TwoDelegatedOneHumanOneAnswered =
        "# Cache work\n\n" +
        ":::question\n" +
        "{ \"id\": \"cache\", \"title\": \"Which cache should front it?\", \"mode\": \"single\", " +
        "\"options\": [\"Redis\", \"in-memory\"], \"target\": \"agent\", \"recommended\": \"Redis\" }\n" +
        ":::\n\n" +
        ":::question\n" +
        "{ \"id\": \"ttl\", \"title\": \"What TTL should the cache use?\", \"mode\": \"number\", " +
        "\"target\": \"agent\" }\n" +
        ":::\n\n" +
        ":::question\n" +
        "{ \"id\": \"db\", \"title\": \"Which datastore?\", \"mode\": \"single\", " +
        "\"options\": [\"Postgres\", \"DynamoDB\"], \"target\": \"human\" }\n" +
        ":::\n\n" +
        ":::question\n" +
        "{ \"id\": \"lang\", \"title\": \"Which language?\", \"mode\": \"single\", " +
        "\"options\": [\"C#\", \"F#\"], \"target\": \"agent\", \"answer\": [\"C#\"] }\n" +
        ":::";

    private const string NoDelegated =
        "# Plain\n\n" +
        ":::question\n" +
        "{ \"id\": \"db\", \"title\": \"Which datastore?\", \"mode\": \"single\", " +
        "\"options\": [\"Postgres\", \"DynamoDB\"], \"target\": \"human\" }\n" +
        ":::";

    private const string OneDelegated =
        "# One\n\n" +
        ":::question\n" +
        "{ \"id\": \"cache\", \"title\": \"Which cache?\", \"mode\": \"single\", " +
        "\"options\": [\"Redis\", \"in-memory\"], \"target\": \"agent\" }\n" +
        ":::";

    /// <summary>
    /// The gate's own regex, written the way a consumer writes it: one pass, capturing the sentinel and the id
    /// together off a single line.
    /// </summary>
    private static readonly Regex MarkerWithId =
        new(@"^> \*\*DELEGATED DECISION `([^`]+)`\*\*", RegexOptions.Multiline);

    [Fact]
    public void MarkerLine_sentinel_and_id_region_is_ASCII_in_the_emitted_bytes()
    {
        var output = HandoffMarkdown.Emit(TwoDelegatedOneHumanOneAnswered);

        foreach (var line in output.Split('\n'))
        {
            var start = line.IndexOf(HandoffMarkdown.DelegatedDecisionMarker, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            // Through the `**` that closes the bolded sentinel+id — everything the consumer's pattern touches.
            var close = line.IndexOf("**", start + HandoffMarkdown.DelegatedDecisionMarker.Length,
                StringComparison.Ordinal);
            Assert.True(close > 0, $"no closing '**' on marker line: {line}");

            var token = line[start..(close + 2)];
            var bytes = Encoding.UTF8.GetBytes(token);

            Assert.All(bytes, b => Assert.True(
                b < 0x80,
                $"non-ASCII byte 0x{b:x2} inside the matched token '{token}'. The consumer greps this from "
                    + "PowerShell on Windows; a multi-byte character here turns their gate into one that "
                    + "silently matches nothing."));
        }
    }

    [Fact]
    public void MarkerLine_carries_the_id_so_one_regex_captures_both()
    {
        var output = HandoffMarkdown.Emit(TwoDelegatedOneHumanOneAnswered);

        var ids = MarkerWithId.Matches(output).Select(m => m.Groups[1].Value).ToArray();

        Assert.Equal(new[] { "cache", "ttl" }, ids);
    }

    [Fact]
    public void MetadataLine_still_carries_the_id_because_verify_matches_on_it()
    {
        var output = HandoffMarkdown.Emit(TwoDelegatedOneHumanOneAnswered);

        // The duplication with the marker line is deliberate: `charter verify` cross-checks the manifest
        // against QuestionIdMarker, and charter-format documents the metadata line as the uniform shape under
        // EVERY status lead. Dropping it here to de-duplicate breaks a different consumer.
        Assert.Contains($"{HandoffMarkdown.QuestionIdMarker}cache`", output, StringComparison.Ordinal);
        Assert.Contains($"{HandoffMarkdown.QuestionIdMarker}ttl`", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CountLine_declares_the_delegated_total_and_excludes_answered_agent_questions()
    {
        var output = HandoffMarkdown.Emit(TwoDelegatedOneHumanOneAnswered);

        // `lang` is target: agent but ANSWERED, so it is not a decision anyone still owes. `db` is open but
        // targeted at a human. Two remain.
        Assert.Contains($"{HandoffMarkdown.DelegatedCountMarker}2**", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CountLine_agrees_with_the_markers_it_counts()
    {
        var output = HandoffMarkdown.Emit(TwoDelegatedOneHumanOneAnswered);

        var declared = int.Parse(Regex.Match(output, @"DECISIONS DELEGATED TO YOU: (\d+)\*\*").Groups[1].Value);

        Assert.Equal(MarkerWithId.Matches(output).Count, declared);
    }

    [Fact]
    public void CountLine_leads_the_plan_content()
    {
        var output = HandoffMarkdown.Emit(TwoDelegatedOneHumanOneAnswered);

        // Ahead of the plan's own title — the whole point is that it is not skimmed past in a ~283 KB prompt.
        Assert.StartsWith($"> {HandoffMarkdown.DelegatedCountMarker}", output, StringComparison.Ordinal);
        Assert.True(
            output.IndexOf(HandoffMarkdown.DelegatedCountMarker, StringComparison.Ordinal)
                < output.IndexOf("# Cache work", StringComparison.Ordinal));
    }

    [Fact]
    public void A_naive_count_of_the_item_sentinel_is_not_inflated_by_the_count_line()
    {
        var output = HandoffMarkdown.Emit(TwoDelegatedOneHumanOneAnswered);

        // Every PLURAL phrasing of "delegated decision" contains the singular as a substring, so the obvious
        // wording of the count line would have made `grep -c` report N+1 — a gate wrong by exactly one, which
        // is the hardest kind to notice. The count line deliberately reverses the words.
        var naive = Regex.Matches(output, "DELEGATED DECISION").Count;

        Assert.Equal(2, naive);
    }

    [Fact]
    public void No_delegated_questions_emits_no_count_line()
    {
        var output = HandoffMarkdown.Emit(NoDelegated);

        // A plan that delegates nothing should not carry a line about delegation, and its absence is
        // unambiguous because the marker lines are absent too.
        Assert.DoesNotContain(HandoffMarkdown.DelegatedCountMarker, output, StringComparison.Ordinal);
        Assert.DoesNotContain(HandoffMarkdown.DelegatedDecisionMarker, output, StringComparison.Ordinal);
    }

    [Fact]
    public void One_delegated_question_reads_as_singular()
    {
        var output = HandoffMarkdown.Emit(OneDelegated);

        Assert.Contains($"{HandoffMarkdown.DelegatedCountMarker}1**", output, StringComparison.Ordinal);
        Assert.Contains("hands 1 decision to the agent", output, StringComparison.Ordinal);
        Assert.DoesNotContain("hands 1 decisions", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answered_delegated_question_gets_no_marker_and_no_count()
    {
        var output = HandoffMarkdown.Emit(
            "# Answered\n\n" +
            ":::question\n" +
            "{ \"id\": \"lang\", \"title\": \"Which language?\", \"mode\": \"single\", " +
            "\"options\": [\"C#\", \"F#\"], \"target\": \"agent\", \"answer\": [\"C#\"] }\n" +
            ":::");

        Assert.DoesNotContain(HandoffMarkdown.DelegatedDecisionMarker, output, StringComparison.Ordinal);
        Assert.DoesNotContain(HandoffMarkdown.DelegatedCountMarker, output, StringComparison.Ordinal);
        Assert.Contains("Answered: C#", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answers_file_that_settles_a_delegated_question_lowers_the_count()
    {
        var answers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["cache"] = new[] { "Redis" },
        };

        var output = HandoffMarkdown.Emit(TwoDelegatedOneHumanOneAnswered, answers);

        // The count rides the SAME AnswerRules.Merge that decided the question is open, so it cannot disagree
        // with the markers beside it. A second walk that re-resolved the plan could.
        Assert.Contains($"{HandoffMarkdown.DelegatedCountMarker}1**", output, StringComparison.Ordinal);
        var remaining = Assert.Single(MarkerWithId.Matches(output));
        Assert.Equal("ttl", remaining.Groups[1].Value);
    }
}
