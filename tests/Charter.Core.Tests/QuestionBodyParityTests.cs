using System.Text.Json;
using System.Text.RegularExpressions;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// The two verbs that read a <c>:::question</c>'s body — <c>charter handoff</c> (via
/// <see cref="HandoffMarkdown.Emit"/>) and <c>charter headless</c> (via <see cref="HeadlessRecord.Build"/>) —
/// must agree on WHETHER A BODY IS READABLE and on WHAT IS IN IT. This test is the pin (Charter #172).
/// </summary>
/// <remarks>
/// <para>
/// They did not agree, in three different container shapes, and the disagreement had teeth: strict
/// <c>handoff</c> refuses to write a handoff the emitter would have produced perfectly, and the forensic
/// record escalates a question a human can plainly read on the rendered page — or, in the opposite
/// direction, the record reports a routable <c>target: human</c> question that the handoff has already
/// silently reduced to <c>&gt; **Malformed question …**</c>, deleting its title, id and target.
/// </para>
/// <para>
/// The <b>definition</b> of the body was single-sourced (<c>charter-format</c> says what a
/// <c>:::question</c> is); the <b>parse</b> was not. <c>HeadlessRecord</c> read bodies through
/// <c>QuestionResolution.QuestionBody</c>, which REQUIRED a closing <c>:::</c> fence and looked only for
/// <c>\n</c>; <c>HandoffMarkdown</c> read them through its own private <c>InnerLines</c>, which normalized
/// line endings and stripped a closing fence only IF PRESENT but recognised an opening fence only as
/// <c>^:::\w+</c> (so a <c>::::question</c> fence stayed in the body and broke the JSON parse).
/// </para>
/// <para>
/// The assertion is deliberately BEHAVIOURAL, not structural: it does not check that both call the same
/// method, it checks that both reach the same verdict on the same container. A future refactor that
/// re-forks the parse fails here regardless of how it is spelled.
/// </para>
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","QuestionBodyParity")].
/// </remarks>
[Trait("Category", "QuestionBodyParity")]
public class QuestionBodyParityTests
{
    private const string Json =
        "{\"id\": \"db\", \"title\": \"Which database?\", \"mode\": \"single\", \"target\": \"human\", "
        + "\"options\": [\"Postgres\", \"MySQL\"]}";

    /// <summary>
    /// Every container shape a plan can carry, paired with whether the body is genuinely readable. A shape
    /// Markdig accepts as a <c>:::question</c> container and whose body is valid JSON IS readable, however the
    /// fences are spelled — the renderer already reads all of these (it slices the inner blocks' spans and
    /// never looks at a fence at all), so a reviewer sees a real form on the page in every readable row.
    /// </summary>
    private static readonly (string Shape, string Markdown, bool Readable)[] Rows =
    {
        // The canonical shape.
        ("closed", "# Plan\n\n:::question\n" + Json + "\n:::\n", true),

        // Unclosed at EOF. Markdig closes the container itself, so this renders as an ordinary question form.
        ("unclosed-at-eof", "# Plan\n\n:::question\n" + Json + "\n", true),

        // The same, CRLF — the line ending a Windows editor writes.
        ("unclosed-at-eof-crlf", "# Plan\r\n\r\n:::question\r\n" + Json + "\r\n", true),

        // CR-ONLY line endings, which no row covered before Charter #187. It matters here and not merely for
        // completeness: HandoffMarkdown.Emit NORMALIZES line endings and then parses, while PlanInventory (and
        // so the gate, and so the manifest) parses the RAW string — a real seam, and CR-only is the shape most
        // likely to be handled by one and not the other. The old §3 table records this row diverging before the
        // parse was single-sourced: the flatten was perfect and the record called it malformed.
        ("cr-only", "# Plan\r\r:::question\r" + Json + "\r:::\r", true),

        // A four-colon container fence. CommonMark directive containers nest by fence length, and Charter's
        // own docs use ::::note around an inner :::block, so an author reaching for :::: is not exotic.
        ("four-colon-fence", "# Plan\n\n::::question\n" + Json + "\n::::\n", true),

        // A body that is not JSON at all: unreadable, and BOTH verbs must say so.
        ("not-json", "# Plan\n\n:::question\nnot json at all\n:::\n", false),

        // A trailing comma — the single most common hand-authoring slip, and not valid JSON.
        ("trailing-comma", "# Plan\n\n:::question\n{\"id\": \"db\", \"title\": \"t\",}\n:::\n", false),

        // An EMPTY container: closed, but declaring nothing. Both verbs must call it unreadable for the same
        // reason (the schema parse rejects an empty body), not one of them because it could not find a body.
        ("empty-body", "# Plan\n\n:::question\n:::\n", false),
    };

    /// <summary>The container shapes, as theory data.</summary>
    public static TheoryData<string, string, bool> Containers()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var row in Rows)
        {
            data.Add(row.Shape, row.Markdown, row.Readable);
        }

        return data;
    }

    /// <summary>
    /// Every container shape, run BOTH with and without an <c>--answers</c> file — which this file never did
    /// before Charter #187, so nothing here exercised the merge at all.
    /// </summary>
    /// <remarks>
    /// The answers file FILLS the open question rather than replacing anything, because since Charter #186 an
    /// inline answer and a file entry for the same id is a REFUSAL, not an override. #187's own repro was
    /// written the other way round and is no longer expressible.
    /// </remarks>
    public static TheoryData<string, string, string?> Resolutions()
    {
        var data = new TheoryData<string, string, string?>();
        foreach (var row in Rows)
        {
            data.Add(row.Shape, row.Markdown, null);
            data.Add(row.Shape + "+answers", row.Markdown, "{\"db\": [\"Postgres\"]}");
        }

        // Two questions, resolved from DIFFERENT inputs in one pass: `db` from the plan, `cache` from the file.
        // A single-question row cannot catch a manifest that pairs the right answers with the wrong ids.
        data.Add("two-questions-two-sources", TwoQuestions, "{\"cache\": [\"Redis\"]}");

        // A multi-value answer, so "the manifest's answer equals what the flatten printed" is asserted against
        // something the flatten has to join rather than echo.
        data.Add(
            "multi-value",
            "# Plan\n\n:::question\n{\"id\": \"regions\", \"title\": \"Which regions?\", \"mode\": \"multi\", "
                + "\"target\": \"human\", \"options\": [\"us-east-1\", \"eu-west-1\", \"ap-south-1\"]}\n:::\n",
            "{\"regions\": [\"us-east-1\", \"eu-west-1\"]}");

        return data;
    }

    private const string TwoQuestions =
        "# Plan\n\n:::question\n{\"id\": \"db\", \"title\": \"Which database?\", \"mode\": \"single\", "
        + "\"target\": \"human\", \"options\": [\"Postgres\", \"MySQL\"], \"answer\": [\"MySQL\"]}\n:::\n\n"
        + ":::question\n{\"id\": \"cache\", \"title\": \"Which cache?\", \"mode\": \"single\", "
        + "\"target\": \"human\", \"options\": [\"Redis\", \"in-memory\"]}\n:::\n";

    [Theory]
    [MemberData(nameof(Containers))]
    public void HandoffAndTheRecord_AgreeOnWhetherAQuestionBodyIsReadable(string shape, string markdown, bool readable)
    {
        var handoff = HandoffMarkdown.Emit(markdown);
        var record = HeadlessRecord.Build(markdown, "p.charter.md", "p.charter.html", "0.0.0-test");

        var handoffRead = !handoff.Contains("Malformed question", StringComparison.Ordinal);
        var recordRead = record.Questions.Count == 1
            && record.Notes.All(note => note.Kind != HeadlessNoteKind.MalformedQuestion);

        Assert.True(
            handoffRead == recordRead,
            $"[{shape}] handoff and the headless record disagree about whether the :::question body is "
                + $"readable (handoff read it: {handoffRead}; record read it: {recordRead}). The two verbs "
                + "must share ONE question-body parse -- a body one can read and the other cannot means "
                + "strict handoff refuses a plan the emitter handles perfectly, or the record certifies a "
                + "question the handoff has already deleted.");

        Assert.Equal(readable, handoffRead);
    }

    [Theory]
    [MemberData(nameof(Containers))]
    public void WhenTheBodyIsReadable_BothVerbsReportTheSameRoutingFacts(string shape, string markdown, bool readable)
    {
        if (!readable)
        {
            return;
        }

        var handoff = HandoffMarkdown.Emit(markdown);
        var record = HeadlessRecord.Build(markdown, "p.charter.md", "p.charter.html", "0.0.0-test");

        var question = Assert.Single(record.Questions);
        Assert.Equal("db", question.Id);
        Assert.Equal("human", question.Target);

        // The handoff's metadata line is the flattened path's whole routing interface, so it must carry the
        // same id and target the record does.
        Assert.True(
            handoff.Contains("id: `db`", StringComparison.Ordinal),
            $"[{shape}] the handoff dropped the question's id: {handoff}");
        Assert.True(
            handoff.Contains("target: `human`", StringComparison.Ordinal),
            $"[{shape}] the handoff dropped the question's target: {handoff}");
        Assert.True(
            handoff.Contains("Which database?", StringComparison.Ordinal),
            $"[{shape}] the handoff dropped the question's title: {handoff}");
    }

    [Theory]
    [MemberData(nameof(Containers))]
    public void TheWriteSideAgreesWithTheReadSide_OnEveryContainerShape(string shape, string markdown, bool readable)
    {
        // The third consumer of the body is the WRITE path, which locates it as a SPAN (indices cannot survive
        // line-ending normalization, and a splice needs indices). It is a second function by necessity, and a
        // second function is a second chance to disagree: a question the record can READ but whose answer
        // `resolve` silently declines to WRITE is a plan that can never be settled unattended.
        //
        // Asserted STRUCTURALLY, against the located span, because the disagreement is invisible from outside
        // — the splice simply declines a body it mis-located, which is indistinguishable from a question that
        // had no answer to write. (Proved: reverting the empty-container boundary from `<` to `<=` corrupts
        // the located body to ":::" and every behavioural assertion stays green.)
        foreach (var block in BlockDocument.Parse(markdown).Blocks.Where(b => b.Kind == BlockKind.Question))
        {
            var read = QuestionResolution.QuestionBody(block.RawContent);
            var located = QuestionResolution.TryLocateJsonBody(block.RawContent, out var start, out var end)
                ? block.RawContent.Substring(start, end - start)
                : null;

            Assert.True(
                (read is null) == (located is null),
                $"[{shape}] the read side and the write side disagree about whether there is a body at all "
                    + $"(read: {read ?? "<null>"}; located: {located ?? "<null>"}).");

            // A row this test declares READABLE must have produced a body on both sides, so a future row
            // cannot pass vacuously by having neither side find anything.
            if (readable)
            {
                Assert.NotNull(read);
                Assert.NotNull(located);
            }

            if (read is not null && located is not null)
            {
                // Line endings are the ONE licensed difference: the read side normalizes them, the write side
                // cannot without invalidating its indices.
                Assert.Equal(
                    read.Trim(),
                    located.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim());
            }
        }
    }

    [Theory]
    [MemberData(nameof(Containers))]
    public void AnAnswerIsWrittenIntoEveryQuestionBothVerbsCanRead(string shape, string markdown, bool readable)
    {
        var applied = QuestionResolution.Apply(
            markdown,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["db"] = new[] { "Postgres" },
            });

        if (readable)
        {
            Assert.True(
                HeadlessRecord.Build(applied, "p.charter.md", "p.charter.html", "0.0.0-test")
                    .Questions.Single().Answered,
                $"[{shape}] the answer was not written into a question both verbs can read: {applied}");
        }
        else
        {
            // Nothing readable means nothing to answer — and, in particular, nothing corrupted.
            Assert.Equal(markdown, applied);
        }
    }

    // ---- the manifest describes the flatten it was built beside (Charter #187) -------------------------------

    /// <summary>Every <c>id</c> the flatten's metadata line carries, in document order.</summary>
    private static readonly Regex FlattenedIds = new(@"id: `([^`]+)`", RegexOptions.Compiled);

    /// <summary>Every ANSWERED question the flatten printed, with the values it printed and the id beneath them.</summary>
    private static readonly Regex FlattenedAnswers = new(
        @"^\*\*Q: (?<title>.*?)\*\* — Answered: (?<values>.*)\r?\n_Question — id: `(?<id>[^`]+)`",
        RegexOptions.Multiline | RegexOptions.Compiled);

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void TheManifestsQuestions_AreExactlyWhatTheFlattenEmitted(
        string shape, string markdown, string? answersJson)
    {
        // THE seam this change adds, and the reason this file is the highest-value test in it. Until now the
        // two parses only had to agree about a plan; now one artifact VOUCHES FOR THE OTHER, and it is
        // assembled from a different parse of a different string: HandoffMarkdown.Emit normalizes line endings
        // and THEN parses, while HandoffGate -> PlanInventory.Build -> PlanWalk.Blocks parses the RAW markdown.
        // A manifest naming a question the flatten did not emit, or an answer the flatten did not print, is a
        // chain-of-custody artifact certifying a document other than the one on disk.
        //
        // Asserted BEHAVIOURALLY, exactly like the rest of this file: it never checks that the two call the
        // same method, only that they say the same thing about the same inputs, so a future re-fork fails here
        // however it is spelled.
        var answers = answersJson is null ? null : HandoffAnswers.Read(answersJson);
        var flatten = HandoffMarkdown.Emit(markdown, answers?.Values, answers?.Sha256);
        var manifest = Manifest(markdown, flatten, answers);

        var manifestIds = manifest.RootElement.GetProperty("questions").EnumerateArray()
            .Select(question => question.GetProperty("id").GetString())
            .ToList();
        var flattenedIds = FlattenedIds.Matches(flatten).Select(match => match.Groups[1].Value).ToList();

        Assert.True(
            manifestIds.SequenceEqual(flattenedIds, StringComparer.Ordinal),
            $"[{shape}] the manifest's questions[] and the flattened plan disagree about which questions this "
                + $"run resolved (manifest: [{string.Join(", ", manifestIds)}]; flatten: "
                + $"[{string.Join(", ", flattenedIds)}]). They are built from two parses of two strings, and "
                + "the manifest's whole job is vouching for the file beside it.");
    }

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void EachManifestAnswer_IsWhatTheFlattenPrintedForThatQuestion(
        string shape, string markdown, string? answersJson)
    {
        var answers = answersJson is null ? null : HandoffAnswers.Read(answersJson);
        var flatten = HandoffMarkdown.Emit(markdown, answers?.Values, answers?.Sha256);
        var manifest = Manifest(markdown, flatten, answers);

        // Compared as the JOINED string the flatten actually writes, rather than by splitting it back into
        // values: a value containing ", " would make the split lie, and the point is what a reader of plan.md
        // sees.
        var printed = FlattenedAnswers.Matches(flatten)
            .ToDictionary(match => match.Groups["id"].Value, match => match.Groups["values"].Value.Trim(),
                StringComparer.Ordinal);

        foreach (var question in manifest.RootElement.GetProperty("questions").EnumerateArray())
        {
            var id = question.GetProperty("id").GetString()!;
            var answered = question.GetProperty("answered").GetBoolean();
            var joined = string.Join(
                ", ", question.GetProperty("answer").EnumerateArray().Select(value => value.GetString()));

            Assert.True(
                answered == printed.ContainsKey(id),
                $"[{shape}] the manifest says '{id}' answered={answered} while the flatten "
                    + (answered ? "printed no Answered line for it" : "printed one") + $":\n{flatten}");

            if (answered)
            {
                Assert.True(
                    string.Equals(joined, printed[id], StringComparison.Ordinal),
                    $"[{shape}] the manifest records '{id}' as {joined} while the flatten printed "
                        + $"{printed[id]}. A manifest that names a different decision from the document it "
                        + "vouches for is worse than no manifest.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Containers))]
    public void AnUnreadableBody_IsMissingFromTheManifest_AndCountedInONEField(
        string shape, string markdown, bool readable)
    {
        // The third party to the parity, and the one a consumer cannot see: a malformed question has no id, so
        // it cannot appear in questions[] at all. Without malformedQuestions the manifest for such a plan looks
        // complete while the flatten has deleted the question's id, title and target.
        var flatten = HandoffMarkdown.Emit(markdown);
        var root = Manifest(markdown, flatten, answers: null).RootElement;

        Assert.True(
            root.GetProperty("malformedQuestions").GetInt32() == (readable ? 0 : 1),
            $"[{shape}] malformedQuestions disagrees with whether the body is readable.");
        Assert.True(
            root.GetProperty("questions").GetArrayLength() == (readable ? 1 : 0),
            $"[{shape}] questions[] must list a readable question and must NOT list an unreadable one.");

        Assert.True(
            flatten.Contains("Malformed question", StringComparison.Ordinal) == !readable,
            $"[{shape}] the flatten and the manifest disagree about whether the body parsed:\n{flatten}");
    }

    /// <summary>
    /// One run assembled the way the verb assembles it: the flatten, ONE gate evaluation, and the manifest
    /// built from that value rather than from a second pass.
    /// </summary>
    private static JsonDocument Manifest(string markdown, string flatten, HandoffAnswers? answers)
        => JsonDocument.Parse(
            HandoffManifest.Build(
                markdown,
                flatten,
                answers,
                HandoffGate.Evaluate(markdown, answers?.Values),
                failIfNeedsHumanPassed: false,
                new HandoffManifestFiles("p.charter.md", answers is null ? null : "a.json", "plan.md"),
                "0.0.0-test").ToJson());
}
