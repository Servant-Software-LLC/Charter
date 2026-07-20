using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Golden-HTML tests for the <c>:::question</c> block's rendering to a native HTML <c>&lt;form&gt;</c>
/// (TDD "red", no new stubs). These compile against the existing renderer surface
/// (<see cref="BlockDocument.Parse(string)"/>, <see cref="CharterRenderer.Render(string)"/>) plus the
/// <see cref="BlockKind.Question"/> member (task 01) and the <see cref="QuestionSpec"/> type (task 09), and
/// FAIL at runtime: today a <c>:::question</c> container still classifies to <see cref="BlockKind.Note"/> and
/// renders as a note-callout <c>&lt;div&gt;</c> wrapping its raw JSON body, so no <c>&lt;form&gt;</c> and no
/// native controls are emitted. Task <c>12-implement-question-form</c> makes them pass.
///
/// The load-bearing contract these pin:
/// <list type="number">
///   <item><description>a <c>:::question</c> container classifies as <see cref="BlockKind.Question"/>;</description></item>
///   <item><description>it renders as a native <c>&lt;form&gt;</c> whose root carries the block's
///     content-derived stable <see cref="Block.Id"/> (the annotation anchor) AND the question id
///     (<c>data-question-id</c>, so a submitted answer correlates back to its question);</description></item>
///   <item><description>each answer mode maps to its native control — <c>single</c>&#8594;radio,
///     <c>multi</c>&#8594;checkbox, <c>free-text</c>&#8594;<c>&lt;textarea&gt;</c>, <c>number</c>&#8594;number
///     input, <c>bool</c>&#8594;a checkbox — and every option label appears;</description></item>
///   <item><description>the rendered form is plain native HTML (it needs no Charter JS to DISPLAY): native
///     inputs, not a script-built widget. The submit WIRING is added serve-time by the SDK (task 15) and is
///     not part of this rendered artifact.</description></item>
/// </list>
/// Ids are asserted against <see cref="Block.Id"/> (recomputed here), never a hard-coded hash, so these
/// survive any hash choice — exactly as <c>RendererGoldenTests</c> does.
/// </summary>
[Trait("Category","QuestionForm")]
public class QuestionFormTests
{
    // The question id carried by the primary single-select document, asserted as the answer-correlation token.
    private const string QuestionId = "q-primary-color";

    // Option labels are the single source of truth for both each document's JSON body and its expected labels
    // and per-option control counts, so the assertions stay structural (one control per option), never pinned.
    private static readonly string[] SingleOptions = { "Red", "Green", "Blue" };
    private static readonly string[] MultiOptions = { "Email", "Slack", "Webhook" };

    // A single-select :::question whose body is a JSON question spec (id/title/mode/options/target). Reused by
    // the classification, form-anchor, single-control, and standalone-inert facts.
    private static readonly string SingleSelectDoc =
        QuestionDoc(QuestionId, "Which primary color do you prefer?", "single", "human", SingleOptions);

    // A multi-select :::question — its options become checkboxes rather than radios.
    private static readonly string MultiSelectDoc =
        QuestionDoc("q-notify-channels", "Which channels should we notify?", "multi", "agent", MultiOptions);

    [Fact]
    public void Parse_QuestionContainer_ClassifiesAsQuestion()
    {
        var block = BlockDocument.Parse(SingleSelectDoc).Blocks[0];

        // RED until task 12's classifier lands: a :::question container still classifies to Note today.
        Assert.Equal(BlockKind.Question, block.Kind);
    }

    [Fact]
    public void Render_Question_EmitsNativeFormCarryingBlockAnchorAndQuestionId()
    {
        var block = BlockDocument.Parse(SingleSelectDoc).Blocks[0];
        var html = CharterRenderer.Render(SingleSelectDoc);

        // A native <form> element (RED today: a :::question still renders as a <div class="note">).
        Assert.Contains("<form", html);

        // The block root carries its content-derived stable anchor. The document has exactly ONE block, so
        // the only element that can carry id="{block.Id}" is the form root — asserted against block.Id, never
        // a hard-coded hash, exactly as RendererGoldenTests does.
        Assert.Contains($"id=\"{block.Id}\"", html);

        // ...and it carries the question id so a submitted answer correlates back to its question (RED today).
        Assert.Contains($"data-question-id=\"{QuestionId}\"", html);
    }

    [Fact]
    public void Render_SingleSelectQuestion_EmitsOneRadioPerOptionWithLabels()
    {
        var html = CharterRenderer.Render(SingleSelectDoc);

        // single -> a native radio per option (RED today: no <input> at all).
        Assert.Contains("type=\"radio\"", html);
        var radioCount = CountOccurrences(html, "type=\"radio\"");
        Assert.Equal(SingleOptions.Length, radioCount);

        // Every option label appears in the rendered form.
        foreach (var option in SingleOptions)
        {
            Assert.Contains(option, html);
        }
    }

    [Fact]
    public void Render_MultiSelectQuestion_EmitsOneCheckboxPerOptionWithLabels()
    {
        var html = CharterRenderer.Render(MultiSelectDoc);

        // multi -> a native checkbox per option (RED today: no <input> at all).
        Assert.Contains("type=\"checkbox\"", html);
        var checkboxCount = CountOccurrences(html, "type=\"checkbox\"");
        Assert.Equal(MultiOptions.Length, checkboxCount);

        foreach (var option in MultiOptions)
        {
            Assert.Contains(option, html);
        }
    }

    [Theory]
    [InlineData("free-text", "<textarea")]
    [InlineData("number", "type=\"number\"")]
    [InlineData("bool", "type=\"checkbox\"")]
    public void Render_NonSelectMode_EmitsItsNativeControl(string mode, string expectedControl)
    {
        // The three modes that carry no options each map to a single native control. RED today: a :::question
        // renders as a note callout, so none of these control tokens are present.
        var html = CharterRenderer.Render(QuestionDocForMode(mode));

        Assert.Contains(expectedControl, html);
    }

    [Fact]
    public void Render_Question_IsNativeFormNotScriptBuiltWidget()
    {
        var html = CharterRenderer.Render(SingleSelectDoc);

        // The rendered artifact IS the form: native HTML controls, present without any client JS (RED today:
        // no <form>/<input> is emitted).
        Assert.Contains("<form", html);
        Assert.Contains("<input", html);

        // Standalone-inert: the form needs no Charter JS to DISPLAY, so the rendered artifact contains no
        // <script> that builds the widget. (The submit WIRING — posting answers — is added serve-time by the
        // SDK in task 15 and is not part of this rendered artifact.)
        Assert.DoesNotContain("<script", html);
    }

    /// <summary>
    /// A <c>:::question</c> document whose body is a JSON question spec. The <c>options</c> field is emitted
    /// only when <paramref name="options"/> is non-empty (select modes); the other modes omit it.
    /// </summary>
    private static string QuestionDoc(string id, string title, string mode, string target, params string[] options)
    {
        var optionsField = string.Empty;
        if (options.Length > 0)
        {
            var quoted = new string[options.Length];
            for (var i = 0; i < options.Length; i++)
            {
                quoted[i] = $"\"{options[i]}\"";
            }

            optionsField = $", \"options\": [{string.Join(", ", quoted)}]";
        }

        var body = $"{{ \"id\": \"{id}\", \"title\": \"{title}\", \"mode\": \"{mode}\", \"target\": \"{target}\"{optionsField} }}";
        return ":::question\n" + body + "\n:::";
    }

    /// <summary>A minimal, schema-valid <c>:::question</c> for an option-free <paramref name="mode"/>.</summary>
    private static string QuestionDocForMode(string mode)
        => QuestionDoc($"q-mode-{mode}", $"A {mode} question", mode, "human");

    /// <summary>Counts non-overlapping occurrences of <paramref name="needle"/> in <paramref name="haystack"/>.</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
