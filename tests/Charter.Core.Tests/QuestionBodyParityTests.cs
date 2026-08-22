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
    public static TheoryData<string, string, bool> Containers() => new()
    {
        // The canonical shape.
        { "closed", "# Plan\n\n:::question\n" + Json + "\n:::\n", true },

        // Unclosed at EOF. Markdig closes the container itself, so this renders as an ordinary question form.
        { "unclosed-at-eof", "# Plan\n\n:::question\n" + Json + "\n", true },

        // The same, CRLF — the line ending a Windows editor writes.
        { "unclosed-at-eof-crlf", "# Plan\r\n\r\n:::question\r\n" + Json + "\r\n", true },

        // A four-colon container fence. CommonMark directive containers nest by fence length, and Charter's
        // own docs use ::::note around an inner :::block, so an author reaching for :::: is not exotic.
        { "four-colon-fence", "# Plan\n\n::::question\n" + Json + "\n::::\n", true },

        // A body that is not JSON at all: unreadable, and BOTH verbs must say so.
        { "not-json", "# Plan\n\n:::question\nnot json at all\n:::\n", false },

        // A trailing comma — the single most common hand-authoring slip, and not valid JSON.
        { "trailing-comma", "# Plan\n\n:::question\n{\"id\": \"db\", \"title\": \"t\",}\n:::\n", false },

        // An EMPTY container: closed, but declaring nothing. Both verbs must call it unreadable for the same
        // reason (the schema parse rejects an empty body), not one of them because it could not find a body.
        { "empty-body", "# Plan\n\n:::question\n:::\n", false },
    };

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
}
