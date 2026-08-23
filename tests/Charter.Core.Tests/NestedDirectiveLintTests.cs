using System;
using System.Linq;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #203: the lint that finds <c>:::</c> directives the RENDERER draws live and the BLOCK MODEL cannot
/// see, and the tiers the record and the strict gate act on.
/// </summary>
public class NestedDirectiveLintTests
{
    private const string QuestionBody =
        ":::question\n{\"id\":\"q-nested\",\"title\":\"Which store?\",\"mode\":\"single\","
        + "\"target\":\"human\",\"options\":[\"Postgres\",\"DynamoDB\"]}\n:::";

    private const string ListItemPlan =
        "# A plan\n\n- an item\n\n  :::question\n"
        + "  {\"id\":\"q-nested\",\"title\":\"Which store?\",\"mode\":\"single\","
        + "\"target\":\"human\",\"options\":[\"Postgres\",\"DynamoDB\"]}\n  :::\n";

    private const string BlockquotePlan =
        "# A plan\n\n> :::question\n"
        + "> {\"id\":\"q-nested\",\"title\":\"Which store?\",\"mode\":\"single\","
        + "\"target\":\"human\",\"options\":[\"Postgres\",\"DynamoDB\"]}\n> :::\n";

    private static string InsideNote(string inner) =>
        "# A plan\n\nProse.\n\n::::note\nA callout.\n\n" + inner + "\n::::\n";

    // ---- The predicate: live rendering, along the whole chain ---------------------------------------------

    /// <summary>
    /// <b>The anti-drift binding, asserted BEHAVIOURALLY.</b> For every container kind,
    /// <c>CharterMarkdown.RendersChildren</c> must agree with whether the renderer actually draws a nested
    /// <c>:::question</c> as a live form — and the lint must report exactly the live ones. Asserting the
    /// verdicts rather than "they call the same method" is what makes a future re-fork fail however it is
    /// spelled: the renderer's dispatch reads the predicate, and so does the lint, but this test would catch it
    /// even if one of them stopped.
    /// </summary>
    [Theory]
    [InlineData("note", true)]
    [InlineData("warn", true)]
    [InlineData("comparison", true)]
    [InlineData("diagram", false)]
    [InlineData("diff", false)]
    [InlineData("custom-html", false)]
    [InlineData("questoin", false)]   // an unknown :::foo — ContainerBody, never children
    public void TheLintReportsExactlyTheNestingsTheRendererDrawsLive(string outerDirective, bool live)
    {
        var markdown = "# A plan\n\nProse.\n\n::::" + outerDirective + "\nA body.\n\n" + QuestionBody + "\n::::\n";
        var html = CharterRenderer.Render(markdown);

        // What the reviewer is actually shown: a real form with a question id, or inert text.
        Assert.Equal(live, html.Contains("data-question-id=\"q-nested\"", StringComparison.Ordinal));

        // The lint's verdict must be the same verdict.
        var found = NestedDirectiveLint.Find(markdown);
        Assert.Equal(live, found.Any(directive => directive.Kind == BlockKind.Question));
    }

    /// <summary>
    /// The CHAIN is walked, not the immediate parent. A <c>:::question</c> inside a <c>:::note</c> inside a
    /// <c>:::custom-html</c> has a child-rendering parent and is still inert, because the escape hatch swallowed
    /// the whole subtree — so an "is my parent a note?" test would report a question nobody can answer.
    /// </summary>
    [Fact]
    public void AChildRenderingParentInsideAnOpaqueOneIsStillInert_AndIsNotReported()
    {
        var markdown =
            "# A plan\n\nProse.\n\n:::::custom-html\n<p>Author markup.</p>\n\n::::note\nA callout.\n\n"
            + QuestionBody + "\n::::\n:::::\n";

        Assert.DoesNotContain("data-question-id=\"q-nested\"", CharterRenderer.Render(markdown), StringComparison.Ordinal);
        Assert.Empty(NestedDirectiveLint.Find(markdown));
    }

    /// <summary>
    /// CommonMark containers count as live links in the chain: Markdig's HTML renderer descends through a list
    /// item and a blockquote, so a <c>:::question</c> in either is a real form — verified against the render,
    /// not assumed. Blockquote nesting is in scope and gets the same treatment.
    /// </summary>
    [Theory]
    [InlineData(ListItemPlan)]
    [InlineData(BlockquotePlan)]
    public void ACommonMarkContainerIsALiveLink_SoANestedQuestionIsReported(string markdown)
    {
        Assert.Contains("data-question-id=\"q-nested\"", CharterRenderer.Render(markdown), StringComparison.Ordinal);
        Assert.Contains(NestedDirectiveLint.Find(markdown), directive => directive.Kind == BlockKind.Question);
    }

    /// <summary>A top-level directive is a block, which is the whole point — it is never reported.</summary>
    [Fact]
    public void ATopLevelDirectiveIsNeverReported()
    {
        var markdown = "# A plan\n\nProse.\n\n" + QuestionBody + "\n\n:::note\nA callout.\n:::\n";
        Assert.Empty(NestedDirectiveLint.Find(markdown));
    }

    /// <summary>The reported line is the container's own opening line, so a reviewer can go straight to it.</summary>
    [Fact]
    public void TheReportedLineIsTheNestedContainersOwnOpeningLine()
    {
        //                1              2  3       4  5         6           7  8
        var markdown = "# A plan\n" + "\n" + "Prose.\n" + "\n" + "::::note\n" + "A callout.\n" + "\n" + QuestionBody + "\n::::\n";

        var found = Assert.Single(NestedDirectiveLint.Find(markdown));
        Assert.Equal(BlockKind.Question, found.Kind);
        Assert.Equal("question", found.Directive);
        Assert.Equal(8, found.SourceLine);
    }

    // ---- The tiers ----------------------------------------------------------------------------------------

    /// <summary>
    /// A nested <c>:::question</c> raises <c>needsHuman</c> — the fourth term, mirroring `MalformedQuestions`.
    /// Before this existed the record read <c>needsHuman: false, questions: []</c> over a live form a human was
    /// looking at, and `charter headless` exited 0.
    /// </summary>
    [Fact]
    public void ANestedQuestion_RaisesNeedsHuman_AndIsNotInQuestions()
    {
        var inventory = PlanInventory.Build(InsideNote(QuestionBody));

        Assert.True(inventory.NeedsHuman);
        Assert.Equal(1, inventory.NestedQuestions);
        Assert.Empty(inventory.Questions);
        Assert.Contains(inventory.Notes, note => note.Kind == HeadlessNoteKind.NestedQuestion);
    }

    /// <summary>
    /// The three blocking kinds block strict handoff; the warning kinds do not. Read as a table because the
    /// asymmetry between them is the design, not an accident.
    /// </summary>
    [Theory]
    [InlineData(QuestionBody, HandoffGate.NestedQuestion)]
    [InlineData(":::diff\n- was\n+ is\n:::", HandoffGate.NestedDiff)]
    [InlineData(":::questoin\n{\"id\":\"typo\"}\n:::", HandoffGate.NestedUnknownDirective)]
    public void ANestedQuestionDiffOrUnknownDirective_BlocksStrictHandoff(string inner, string blockerKind)
    {
        var result = HandoffGate.Evaluate(InsideNote(inner), answers: null);

        Assert.True(result.NeedsHuman);
        Assert.Contains(result.Blockers, blocker => blocker.Kind == blockerKind);
    }

    /// <summary>
    /// The kinds whose bodies were READ out of the flatten and found intact are warnings only — recorded, never
    /// blocking, never escalating. <c>NestedDirectiveFlattenTests</c> is the evidence behind this row.
    /// </summary>
    [Theory]
    [InlineData(":::comparison\n- **A** — one\n- **B** — two\n:::")]
    [InlineData(":::diagram\n```mermaid\ngraph TD;\n  A-->B;\n```\n:::")]
    [InlineData(":::note\nAn inner callout.\n:::")]
    [InlineData(":::warn\nAn inner warning.\n:::")]
    public void ANestedComparisonDiagramNoteOrWarn_IsRecordedButNeitherBlocksNorEscalates(string inner)
    {
        var plan = InsideNote(inner);

        var inventory = PlanInventory.Build(plan);
        Assert.Contains(inventory.Notes, note => note.Kind == HeadlessNoteKind.NestedDirective);
        Assert.False(inventory.NeedsHuman);
        Assert.Equal(0, inventory.NestedQuestions);

        Assert.False(HandoffGate.Evaluate(plan, answers: null).NeedsHuman);
    }

    /// <summary>
    /// Every nested kind gets its OWN note token, because <see cref="HandoffGate"/> switches on the token and
    /// `unattended.md` tells consumers to branch on it. One token carrying two tiers would make the gate's
    /// verdict unreproducible from the record.
    /// </summary>
    [Theory]
    [InlineData(BlockKind.Question, "nested-question")]
    [InlineData(BlockKind.Diff, "nested-diff")]
    [InlineData(BlockKind.Unknown, "nested-unknown-directive")]
    [InlineData(BlockKind.Comparison, "nested-directive")]
    [InlineData(BlockKind.Diagram, "nested-directive")]
    [InlineData(BlockKind.Note, "nested-directive")]
    [InlineData(BlockKind.Warn, "nested-directive")]
    public void EachNestedKindMapsToItsOwnTierToken(BlockKind kind, string token)
        => Assert.Equal(token, HeadlessNote.Token(NestedDirectiveLint.NoteKindFor(kind)));

    /// <summary>
    /// The lint rides <see cref="PlanInventory"/>'s ONE walk, so a nested directive's reported line is the same
    /// line the anchor assignment beside it was built from — never a second parse's idea of the same document.
    /// </summary>
    [Fact]
    public void TheNoteCarriesTheSameLineTheStandaloneLintReports()
    {
        var plan = InsideNote(QuestionBody);

        var standalone = Assert.Single(NestedDirectiveLint.Find(plan));
        var note = Assert.Single(
            PlanInventory.Build(plan).Notes, n => n.Kind == HeadlessNoteKind.NestedQuestion);

        Assert.Equal(standalone.SourceLine, note.SourceLine);
    }
}
