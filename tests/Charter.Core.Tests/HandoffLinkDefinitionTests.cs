using Markdig;
using Markdig.Syntax;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #175 — a plan's <c>[foo]: http://…</c> declarations must reach the Guardrails handoff.
///
/// After #171 stripped the <c>LinkReferenceDefinitionGroup</c>, a plan's definitions no longer reached
/// <see cref="HandoffMarkdown.Emit"/> at all, so prose that says <c>See [foo].</c> handed Guardrails a
/// DANGLING reference — literal text where the reviewer saw a link. This suite pins the fix and, more
/// importantly, the two designs that were FALSIFIED on the way to it. Both are what a future reader will
/// re-propose, and both produce a handoff that is worse than doing nothing.
///
/// <list type="number">
///   <item><description><b>"Emit the source slice, never re-serialise" — DEAD.</b>
///     <c>LinkReferenceDefinition.Span.End</c> is SHORT BY TWO when the title sits on a continuation line, so
///     the slice ends mid-token. Emitting it injects corrupt CommonMark — a title-less definition plus a
///     garbage paragraph — into the plan an LLM breaks down. That is #171 repeating one level down: trusting
///     the same node family's spans. Pinned by
///     <see cref="TheSpanOfADefinitionWithAContinuationTitle_IsTruncated_WhichIsWhyNothingSlicesIt"/> and by
///     the round trip below.</description></item>
///   <item><description><b>The containment filter ("span-contained in a block ⇒ already carried") — DEAD,
///     unsound twice.</b> A <c>:::note</c> whose first inner line is a definition flattens to
///     <c>&gt; **Note:** [inner]: http://…</c> — a blockquoted paragraph, not a definition. A
///     <c>:::diagram</c> flattens one INSIDE a <c>```mermaid</c> fence. Both render as working links and
///     dangle in the handoff, so both are span-contained and neither is carried. Pinned by the two
///     container tests below, each of which asserts the hole AND the carry in one test so the filter cannot
///     be reintroduced without going red.</description></item>
/// </list>
///
/// The decision: <see cref="HandoffMarkdown.Emit"/> PREPENDS one normalised definitions block — for each
/// distinct label, the FIRST definition in source order, re-serialised from Markdig's resolved
/// <c>Label</c>/<c>Url</c>/<c>Title</c>. Top placement is forced, not aesthetic: with winners first,
/// CommonMark's first-definition-wins does all the work and a nested copy surviving verbatim below becomes
/// inert. See <c>docs/plans/04-machine-consumer-contract.md</c> §11.
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","HandoffLinkDefinition")].
/// </summary>
[Trait("Category", "HandoffLinkDefinition")]
public class HandoffLinkDefinitionTests
{
    // The shape that killed span-slicing: a definition whose title sits on a continuation line.
    private const string ContinuationTitle = "# T\n\n[a]: http://a.example\n  \"A title\"\n\nSee [a].\n";

    // The two container holes. In each the definition is span-contained by a block the flatten RESHAPES, so a
    // containment filter would suppress the carry and the reference would dangle.
    private const string InsideNote = "# T\n\n:::note\n[inner]: http://inner.example\n:::\n\nSee [inner].\n";
    private const string InsideDiagram =
        "# T\n\n:::diagram\n\n[inner]: http://inner.example\n\n:::\n\nSee [inner].\n";

    [Fact]
    public void TheSpanOfADefinitionWithAContinuationTitle_IsTruncated_WhichIsWhyNothingSlicesIt()
    {
        // Characterisation of Markdig 0.37, stated in the suite that depends on it being false. This is the
        // whole argument for re-serialising, so it is asserted rather than believed: the node's own span does
        // not cover its own text, and the slice a "faithful passthrough" would emit is a broken definition.
        var definition = OnlyDefinition(ContinuationTitle);

        var start = definition.Span.Start;
        var end = definition.Span.End;
        var slice = ContinuationTitle.Substring(start, end - start + 1);

        Assert.Equal("[a]: http://a.example\n  \"A titl", slice);

        // Two characters short, and the two it drops are the ones that close the title.
        Assert.Equal(slice.Length + 2, "[a]: http://a.example\n  \"A title\"".Length);

        // Re-parsed, that slice defines NOTHING AT ALL — worse than the "title-less definition plus a garbage
        // paragraph" the design predicted, and worse than emitting no definition, because the corrupt text is
        // still IN the file. The unterminated title invalidates the whole construct, so Markdig backtracks the
        // definition into a paragraph and `[a]` dangles as literal text in the plan an LLM breaks down.
        var corrupt = slice + "\n\nSee [a].\n";
        Assert.Empty(Definitions(corrupt));
        Assert.DoesNotContain("<a href=", CharterRenderer.Render(corrupt), StringComparison.Ordinal);

        // Markdig's RESOLVED values, by contrast, are correct — which is what the emitter reads.
        Assert.Equal("a", definition.Label);
        Assert.Equal("http://a.example", definition.Url);
        Assert.Equal("A title", definition.Title);
    }

    [Fact]
    public void Handoff_ADefinitionWhoseTitleContinuesOnTheNextLine_SurvivesWithItsTitle()
    {
        var flatten = HandoffMarkdown.Emit(ContinuationTitle);

        var carried = OnlyDefinition(flatten);
        Assert.Equal("a", carried.Label);
        Assert.Equal("http://a.example", carried.Url);
        Assert.Equal("A title", carried.Title);

        // ...and no fragment of the truncated slice reached the file.
        Assert.DoesNotContain("\"A titl\n", flatten, StringComparison.Ordinal);
    }

    [Fact]
    public void Handoff_ADefinitionInsideANote_IsCarried_ThoughTheNoteFlattenDestroysIt()
    {
        var flatten = HandoffMarkdown.Emit(InsideNote);

        // THE HOLE, asserted first so the test cannot pass for the wrong reason: the note's flatten turns the
        // definition into a blockquoted paragraph. Whatever else is true, THAT copy defines nothing.
        Assert.Contains("> **Note:** [inner]: http://inner.example", flatten, StringComparison.Ordinal);

        // THE CARRY: the reference still resolves, because the definitions block leads the file.
        AssertResolves(flatten, "inner", "http://inner.example");
    }

    [Fact]
    public void Handoff_ADefinitionInsideADiagram_IsCarried_ThoughItFlattensInsideAMermaidFence()
    {
        var flatten = HandoffMarkdown.Emit(InsideDiagram);

        // THE HOLE: inside a ```mermaid fence, where CommonMark sees literal code, not a definition.
        Assert.Contains("```mermaid", flatten, StringComparison.Ordinal);
        var fenceStart = flatten.IndexOf("```mermaid", StringComparison.Ordinal);
        var fenceEnd = flatten.IndexOf("```", fenceStart + "```mermaid".Length, StringComparison.Ordinal);
        Assert.Contains(
            "[inner]: http://inner.example",
            flatten[fenceStart..fenceEnd],
            StringComparison.Ordinal);

        AssertResolves(flatten, "inner", "http://inner.example");
    }

    [Fact]
    public void Handoff_TheDefinitionsBlock_LeadsTheFlatten()
    {
        var flatten = HandoffMarkdown.Emit("# Title\n\n[foo]: http://example.com\n\nSee [foo].\n");

        // Top placement is load-bearing (§11.2): end placement re-opens the redirection bug, because a LOSER
        // definition surviving verbatim earlier — a :::note carrying one — would win over an appended winner.
        Assert.StartsWith("[foo]: http://example.com\n\n# Title", flatten, StringComparison.Ordinal);
    }

    [Fact]
    public void Handoff_ARedefinedLabel_CarriesTheFIRSTDefinition_TheOneTheRenderResolves()
    {
        // Distinctness is Markdig's own, so the flatten resolves a reference exactly as the RENDER does:
        // [Foo] and [foo] are one label, and CommonMark's first-definition-wins picks the first.
        const string plan =
            "# T\n\n[Foo]: http://first.example\n\n[foo]: http://second.example\n\nSee [FOO].\n";

        var rendered = CharterRenderer.Render(plan);
        Assert.Contains("href=\"http://first.example\"", rendered, StringComparison.Ordinal);

        var flatten = HandoffMarkdown.Emit(plan);

        var carried = Assert.Single(Definitions(flatten));
        Assert.Equal("http://first.example", carried.Url);
        Assert.DoesNotContain("second.example", flatten, StringComparison.Ordinal);

        AssertResolves(flatten, "FOO", "http://first.example");
    }

    [Fact]
    public void Handoff_ANestedDefinition_AppearsTwice_AndTheLeadingCopyIsTheOneThatWINS()
    {
        // ACCEPTED RESIDUE 1 (§11.3): a nested definition appears at the top AND verbatim in its container.
        // Suppressing the second copy needs the unsound containment filter or surgery on container bodies;
        // Charter's standing trade is a visible inert duplicate over a silent wrong resolution. The duplicate
        // is INERT because the leading copy is first, and that is what is asserted — not merely its presence.
        const string plan =
            "# T\n\n:::note\nSee the guide.\n\n[g]: http://nested.example\n:::\n\nAlso [g].\n";

        var flatten = HandoffMarkdown.Emit(plan);

        Assert.Equal(2, Occurrences(flatten, "[g]: http://nested.example"));
        AssertResolves(flatten, "g", "http://nested.example");
    }

    [Fact]
    public void Handoff_AnUnreferencedDefinition_IsStillEmitted()
    {
        // ACCEPTED RESIDUE 3 (§11.3): "nothing is dropped" beats tidiness. Reachability is a whole-document
        // analysis whose failure mode is deleting a definition a later edit needs.
        var flatten = HandoffMarkdown.Emit("# T\n\n[unused]: http://nobody.example\n\nProse.\n");

        Assert.Contains("[unused]: http://nobody.example", flatten, StringComparison.Ordinal);
    }

    [Fact]
    public void Handoff_AFootnoteShapedDefinition_IsEmitted_BecauseTheFLATTENFOLLOWSTheRENDER()
    {
        // ACCEPTED RESIDUE 4 (§11.3). Charter enables NO footnote extension, so `[^1]: body` parses as an
        // ordinary definition with label `^1` and the RENDER makes [^1] a link to `body`. Carrying it keeps
        // the flatten agreeing with the render, which is the governing rule; carving out `^` would break that
        // principle for an exotic case, and the cost is that a GFM reader of the flatten sees a footnote the
        // render never had. Asserted, not assumed, so the trade is visible if it is ever revisited.
        const string plan = "# T\n\nText[^1].\n\n[^1]: body\n";

        Assert.Contains("<a href=\"body\">^1</a>", CharterRenderer.Render(plan), StringComparison.Ordinal);

        var carried = Assert.Single(Definitions(HandoffMarkdown.Emit(plan)));
        Assert.Equal("^1", carried.Label);
        Assert.Equal("body", carried.Url);
    }

    [Fact]
    public void Handoff_EveryDefinitionSHAPE_ResolvesInTheFlattenExactlyAsItDoesInThePlan()
    {
        // The real guarantee, and the one that makes the escaping provable rather than argued: for every
        // authoring shape, re-parsing the FLATTEN yields the same resolved (url, title) as parsing the PLAN.
        // A serialisation bug in any of the escapes goes red here rather than in production.
        string[] shapes =
        [
            "[a]: http://x.example",
            "[a]: <http://x.example/a b>",
            "[a]: <>",
            "[a]: http://x.example \"Double quoted\"",
            "[a]: http://x.example 'Single quoted'",
            "[a]: http://x.example (Paren title)",
            "[a]: http://x.example 'He said \"hi\"'",
            "[a]: http://x.example \"back\\\\slash\"",
            "[a]: http://x.example\n  \"Continuation title\"",
            "[a]: http://x.example/a(b)",
            "[a]: http://x.example/trailing\\\\",
            "[a]: ./docs/relative.md",
            "[a\\]b]: http://x.example",
            "[a\\[b]: http://x.example",
            "[a\\\\b]: http://x.example",
            "[a   b]: http://x.example",
            "[Å]: http://x.example",
        ];

        foreach (var shape in shapes)
        {
            var plan = "# T\n\n" + shape + "\n\nProse.\n";
            var expected = OnlyDefinition(plan);

            var flatten = HandoffMarkdown.Emit(plan);
            var actual = OnlyDefinition(flatten);

            Assert.Equal(expected.Label, actual.Label);
            Assert.Equal(expected.Url, actual.Url);
            Assert.Equal(expected.Title, actual.Title);
        }
    }

    [Fact]
    public void Handoff_SeveralDefinitions_RideConsecutiveLines_AndAllStillResolve()
    {
        // The block emits one definition PER LINE with no blank line between them, which is only correct
        // because CommonMark lets link reference definitions be consecutive. Asserted rather than assumed: if
        // the second line were swallowed as a continuation of the first (the title-continuation shape this
        // whole suite is built around), every label but the first would silently vanish.
        const string plan =
            "# T\n\n[a]: http://a.example\n\n[b]: http://b.example \"B\"\n\n[c]: <http://c.example/x y>\n\n"
            + "See [a], [b] and [c].\n";

        var flatten = HandoffMarkdown.Emit(plan);

        Assert.Equal(3, Definitions(flatten).Count);
        AssertResolves(flatten, "a", "http://a.example");

        var rendered = CharterRenderer.Render(flatten + "\n\nProbe [b] and [c].\n");

        // [b] carries a title, so it renders with one -- which also proves the title survived the re-quoting.
        Assert.Contains("<a href=\"http://b.example\" title=\"B\">b</a>", rendered, StringComparison.Ordinal);

        // [c]'s destination needs the angle form and is emitted last, so this also proves the block does not
        // end early.
        Assert.Contains("<a href=\"http://c.example/x%20y\">c</a>", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Handoff_APlanWithNoDefinitions_IsUnchanged()
    {
        // No definitions, no leading block — so every plan that does not use reference links flattens
        // byte-identically to before #175, and the blank line the block would add never appears.
        const string plan = "# Title\n\nSee it.\n";

        Assert.StartsWith("# Title", HandoffMarkdown.Emit(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_TheDefinitions_AreASecondChannel_NotBlocks()
    {
        // The channel is deliberately NOT a BlockKind: nothing enters the block stream, so the definitions
        // never occupy an anchor slot, never perturb the duplicate discriminator and never add a sourceMap
        // entry. Only Emit reads them, and no source offset is exposed at all (§11.2).
        var document = BlockDocument.Parse("# T\n\n[a]: http://a.example \"T\"\n\nSee [a].\n");

        Assert.Equal(2, document.Blocks.Count);
        Assert.DoesNotContain(document.Blocks, b => b.RawContent.Contains("a.example", StringComparison.Ordinal));

        var definition = Assert.Single(document.LinkDefinitions);
        Assert.Equal("a", definition.Label);
        Assert.Equal("http://a.example", definition.Url);
        Assert.Equal("T", definition.Title);
    }

    /// <summary>Every link reference definition Markdig finds in <paramref name="markdown"/>, in source order.</summary>
    private static IReadOnlyList<LinkReferenceDefinition> Definitions(string markdown)
    {
        var found = new List<LinkReferenceDefinition>();
        foreach (var node in Markdown.Parse(markdown, CharterMarkdown.Pipeline))
        {
            if (node is LinkReferenceDefinitionGroup group)
            {
                found.AddRange(group.OfType<LinkReferenceDefinition>());
            }
        }

        return found;
    }

    private static LinkReferenceDefinition OnlyDefinition(string markdown) => Assert.Single(Definitions(markdown));

    /// <summary>Assert that <paramref name="label"/> resolves to <paramref name="url"/> in the flattened plan
    /// — proved by rendering the flatten and finding a real anchor, not by string-matching the definition.</summary>
    private static void AssertResolves(string flatten, string label, string url)
    {
        var rendered = CharterRenderer.Render(flatten + "\n\nProbe [" + label + "].\n");
        Assert.Contains($"<a href=\"{url}\">{label}</a>", rendered, StringComparison.Ordinal);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
