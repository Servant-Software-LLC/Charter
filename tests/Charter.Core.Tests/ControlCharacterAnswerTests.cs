using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// The one character rule an answer value must satisfy (Charter #202): it may carry <c>U+000A</c> and no
/// other control character, and neither of the two Unicode line/paragraph separators.
/// </summary>
/// <remarks>
/// <para>
/// <b>The reported defect.</b> A <c>:::question</c> whose answer carried a raw carriage return flattened as
/// <c>Answered: alpha&lt;CR&gt;beta</c>. The answer arrives JSON-ESCAPED inside the question body, so
/// <see cref="HandoffMarkdown.Emit"/>'s source normalization (which folds every CR and CRLF in the plan's own
/// text) never sees it as a character — it becomes one only after the JSON decode, which happens later.
/// </para>
/// <para>
/// <b>Why LF is kept and CR is not.</b> The shipped review page renders a <c>free-text</c> question as a
/// <c>&lt;textarea&gt;</c>, so a reviewer writing two sentences on two lines is using the affordance the
/// product gave them, and their LFs travel into the flatten intact — the emitter joins blocks with a blank
/// line, so a multi-line answer can never escape its own block, and <c>charter verify</c>'s lead scan already
/// walks back to that blank line for exactly this reason. A lone CR is a different animal: readers DISAGREE
/// about whether it ends a line (CommonMark says yes, <c>string.Split('\n')</c> says no,
/// <c>ReviewBaseStatus</c> collapses it, <c>charter verify</c> refuses to), and a document containing one has
/// no single answer to "how many lines is this". That disagreement is the bug.
/// </para>
/// <para>
/// <b>The rule is a CATEGORY, not a list.</b> Naming <c>\r</c> alone would be re-litigated the first time
/// somebody pasted a form feed. Everything <see cref="char.IsControl(char)"/> is true of — C0
/// (U+0000–U+001F), DEL (U+007F) and C1 (U+0080–U+009F) — is refused except U+000A, and U+2028 / U+2029 are
/// added because they are line terminators to JavaScript and to .NET's <c>ReplaceLineEndings</c> while being
/// <c>Zl</c>/<c>Zp</c> rather than <c>Cc</c>. It stops there: NBSP and the zero-width format characters occur
/// in honest human text and refusing them would refuse real answers.
/// </para>
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","ControlCharacterAnswer")].
/// </remarks>
[Trait("Category", "ControlCharacterAnswer")]
public class ControlCharacterAnswerTests
{
    // The case the issue lands on: free-text declares NO options, so #186's membership check has nothing to
    // test against and every shape check (one value, not blank) passes a value carrying a CR.
    private const string OpenFreeTextQuestion =
        "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
        + "{\"id\": \"why\", \"title\": \"Why this approach?\", \"mode\": \"free-text\", "
        + "\"target\": \"human\"}\n:::\n";

    private const string OpenSelectQuestion =
        "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
        + "{\"id\": \"db\", \"title\": \"Which database?\", \"mode\": \"single\", \"target\": \"human\", "
        + "\"options\": [\"Postgres\", \"MySQL\"]}\n:::\n";

    // ---- 1. The headline repro, asserted on the FLATTEN --------------------------------------------------

    [Fact]
    public void ACarriageReturnInAFreeTextAnswer_NeverReachesTheFlattenedPlan()
    {
        // Pre-fix this emitted `**Q: Why this approach?** - Answered: alpha<CR>beta`, putting a bare CR inside
        // the handed-off CommonMark - a line terminator where the reviewer meant a character.
        var output = HandoffMarkdown.Emit(OpenFreeTextQuestion, Answers(("why", ["alpha\rbeta"])));

        Assert.DoesNotContain("\r", output, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", output, StringComparison.Ordinal);
        Assert.Contains(HandoffMarkdown.OpenQuestionMarker, output, StringComparison.Ordinal);
    }

    [Fact]
    public void ACarriageReturnInAFreeTextAnswer_IsRejected_NamingTheCharacterAndTheQuestion()
    {
        var rejections = AnswerRules.CheckAll(OpenFreeTextQuestion, Answers(("why", ["alpha\rbeta"])));

        var rejection = Assert.Single(rejections);
        Assert.Equal("why", rejection.QuestionId);
        Assert.Contains("U+000D", rejection.Reason, StringComparison.Ordinal);
        Assert.Contains("carriage return", rejection.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRejectionReason_NeverCarriesTheRawControlCharacterItIsComplainingAbout()
    {
        // The reason goes to stderr. Echoing the raw CR there would mangle the very line that explains it -
        // and #186 documents Reason as ASCII-only so its bytes do not depend on the console encoding.
        var rejection = Assert.Single(
            AnswerRules.CheckAll(OpenFreeTextQuestion, Answers(("why", ["alpha\rbeta"]))));

        Assert.All(rejection.Reason, character => Assert.False(char.IsControl(character)));
    }

    // ---- 2. The NEWLINE decision: LF stays legal ---------------------------------------------------------

    [Fact]
    public void ALineFeedInAFreeTextAnswer_IsACCEPTED_AndSurvivesIntoTheFlatten()
    {
        // A reviewer writing two sentences in the <textarea> the renderer gives a free-text question. This is
        // the boundary the rule is drawn at: refusing it would refuse the product's own affordance.
        const string twoSentences = "Postgres is already in the stack.\nThe team knows it.";
        var output = HandoffMarkdown.Emit(OpenFreeTextQuestion, Answers(("why", [twoSentences])));

        Assert.Empty(AnswerRules.CheckAll(OpenFreeTextQuestion, Answers(("why", [twoSentences]))));
        Assert.Contains("Answered: " + twoSentences, output, StringComparison.Ordinal);
    }

    [Fact]
    public void AMultiLineAnswer_StaysInsideItsOwnBlock_SoTheMetadataLineStillFollowsIt()
    {
        // Why LF is safe where CR is not: Emit joins blocks with a blank line, so an answer's extra newlines
        // can never carry the reader out of the question they belong to. The metadata line is still the last
        // line of this question's block, which is exactly what `charter verify`'s lead scan relies on.
        var output = HandoffMarkdown.Emit(OpenFreeTextQuestion, Answers(("why", ["one\ntwo"])));

        var block = output
            .Split("\n\n", StringSplitOptions.None)
            .Single(part => part.Contains(HandoffMarkdown.AnsweredMarker, StringComparison.Ordinal));
        var lines = block.Split('\n');

        Assert.EndsWith("Answered: one", lines[0], StringComparison.Ordinal);
        Assert.Equal("two", lines[1]);
        Assert.StartsWith(HandoffMarkdown.QuestionIdMarker, lines[^1], StringComparison.Ordinal);
    }

    // ---- 3. The rule is a CATEGORY -----------------------------------------------------------------------

    [Theory]
    [InlineData("\r", "carriage return")]       // U+000D - the reported defect
    [InlineData("\r\n", "carriage return")]     // a Windows generator's line break inside a VALUE
    [InlineData("\t", "tab")]                   // U+0009 - invisible in the plan; breaks Ordinal membership
    [InlineData("\f", "form feed")]             // U+000C - the character the issue predicts gets re-litigated
    [InlineData("\v", "U+000B")]                // vertical tab
    [InlineData("\0", "U+0000")]                // NUL
    [InlineData("\u001B", "U+001B")]            // ESC - an ANSI sequence in anything that cats plan.md
    [InlineData("\u007F", "U+007F")]            // DEL
    [InlineData("\u0085", "U+0085")]            // NEL - a C1 control AND a Unicode line terminator
    [InlineData("\u2028", "line separator")]    // Zl - a line terminator to JavaScript and ReplaceLineEndings
    [InlineData("\u2029", "paragraph separator")]
    public void EveryForbiddenCharacter_IsRefusedAndNamed(string offender, string expectedInReason)
    {
        var rejections = AnswerRules.CheckAll(
            OpenFreeTextQuestion, Answers(("why", [$"alpha{offender}beta"])));

        var rejection = Assert.Single(rejections);
        Assert.Contains(expectedInReason, rejection.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\n")]          // the one carve-out
    [InlineData("\u00A0")]      // NBSP - Zs, ordinary in "10 km"; the rule deliberately stops short of it
    [InlineData("\u200B")]      // ZWSP - Cf; invisible, but refusing format characters would refuse RTL text
    [InlineData("\u202E")]      // RLO - a real hazard, and a WIDER one than answers; not settled here
    public void ACharacterOutsideTheRule_IsLeftAlone(string permitted)
    {
        Assert.Empty(
            AnswerRules.CheckAll(OpenFreeTextQuestion, Answers(("why", [$"alpha{permitted}beta"]))));
    }

    [Fact]
    public void EveryValueIsScanned_NotJustTheFirst()
    {
        // The obvious way to get this wrong is to check `values[0]`, which passes every single-value test in
        // this file. A `multi` answer's later elements come from the same producer as its first.
        const string multi =
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
            + "{\"id\": \"caches\", \"title\": \"Which caches?\", \"mode\": \"multi\", \"target\": \"human\", "
            + "\"options\": [\"Redis\", \"in-memory\"]}\n:::\n";

        var rejection = Assert.Single(
            AnswerRules.CheckAll(multi, Answers(("caches", ["Redis", "in-\rmemory"]))));

        Assert.Contains("U+000D", rejection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARejectedValue_FallsBackToTheRecordedDecision_RatherThanWinningOrErasing()
    {
        // The in-library guarantee at the merge (the #186 shape, for this rule): a refused value never becomes
        // the resolved answer, so the flatten still asserts what the plan records. Without the fallback the
        // question would flatten as OPEN and a human's decision would be lost to a malformed answers file.
        const string answered =
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
            + "{\"id\": \"why\", \"title\": \"Why this approach?\", \"mode\": \"free-text\", "
            + "\"target\": \"human\", \"answer\": [\"It is already in the stack.\"]}\n:::\n";

        var spec = new QuestionSpec(
            "why", "Why this approach?", QuestionMode.FreeText, Array.Empty<string>(), QuestionTarget.Human)
        {
            Answer = ["It is already in the stack."],
        };

        var resolved = AnswerRules.Merge(spec, Answers(("why", ["alpha\rbeta"])));

        Assert.Equal(["It is already in the stack."], resolved);

        var output = HandoffMarkdown.Emit(answered, Answers(("why", ["alpha\rbeta"])));
        Assert.DoesNotContain("\r", output, StringComparison.Ordinal);
        Assert.Contains("Answered: It is already in the stack.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCharacterRuleIsReportedBeforeTheMembershipRule()
    {
        // A CR-carrying value also fails a select question's membership check, and THAT rejection echoes the
        // raw value into the reason. The character rule runs first so the caller gets the actionable sentence
        // and the printed reason stays printable.
        var rejection = Assert.Single(
            AnswerRules.CheckAll(OpenSelectQuestion, Answers(("db", ["Post\rgres"]))));

        Assert.Contains("U+000D", rejection.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("not one of this question's options", rejection.Reason, StringComparison.Ordinal);
    }

    // ---- 4. The WRITE path: no answer Charter writes into a plan may carry one ----------------------------

    [Fact]
    public void ApplyToFile_RefusesAControlCharacter_AndLeavesThePlanUntouched()
    {
        var path = NewPlanPath();
        try
        {
            File.WriteAllText(path, OpenFreeTextQuestion);

            var thrown = Assert.Throws<MalformedAnswerException>(
                () => QuestionResolution.ApplyToFile(path, Answers(("why", ["alpha\rbeta"]))));

            Assert.Contains("why", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("U+000D", thrown.Message, StringComparison.Ordinal);
            Assert.Equal(OpenFreeTextQuestion, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Apply_TheKernel_LeavesTheQuestionUntouchedRatherThanWritingAControlCharacter()
    {
        // The in-library guarantee, mirroring AnswerRules.Merge's: a rejected value never becomes the recorded
        // answer however it reaches the kernel. ApplyToFile refuses LOUDLY before this is ever hit, because a
        // silent skip is what lets a caller commit the answer away as applied (Charter #203).
        var updated = QuestionResolution.Apply(OpenFreeTextQuestion, Answers(("why", ["alpha\rbeta"])));

        Assert.Equal(OpenFreeTextQuestion, updated);
    }

    [Fact]
    public void ApplyToFile_StillWritesAMultiLineFreeTextAnswer()
    {
        var path = NewPlanPath();
        try
        {
            File.WriteAllText(path, OpenFreeTextQuestion);

            QuestionResolution.ApplyToFile(path, Answers(("why", ["one\ntwo"])));

            // The plan carries it JSON-escaped, so the .charter.md itself stays a well-formed single-line body.
            Assert.Contains("\"answer\": [\"one\\ntwo\"]", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static string NewPlanPath()
        => Path.Combine(Path.GetTempPath(), $"charter-202-{Guid.NewGuid():N}.charter.md");

    private static Dictionary<string, IReadOnlyList<string>> Answers(
        params (string Id, string[] Values)[] entries)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            map[entry.Id] = entry.Values;
        }

        return map;
    }
}
