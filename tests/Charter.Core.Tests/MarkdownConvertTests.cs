using System.Text.RegularExpressions;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Behavioral tests for <see cref="MarkdownConvert"/>, the deterministic Markdown -> <c>.charter.md</c> SEED
/// transform behind <c>charter convert</c> (Charter #17). Two properties are load-bearing: (a) every existing
/// block passes through unchanged — a plain <c>.md</c> is already a valid Charter plan, so convert must not
/// mangle prose/tables/code/lists; and (b) the ONE heuristic — an allow-listed "open questions" heading whose
/// list items become OPEN <c>:::question</c> blocks — produces <c>charter-format</c>-valid, round-trippable
/// output. The seed = <see cref="CharterFormat.EnsureVersionMarker(string)"/> applied to the transform,
/// exactly as the CLI composes it, so the marker/round-trip assertions test what `charter convert` writes.
/// </summary>
[Trait("Category", "Convert")]
public class MarkdownConvertTests
{
    // Matches only a line whose FIRST characters are ":::" — used to prove convert leaves no directive fence
    // in a doc that has no open-questions section (and, in the heuristic case, that every such fence belongs to
    // a well-formed :::question the block model recognizes — asserted structurally rather than by this regex).
    private const string LineStartDirective = @"(?m)^:::";

    // A document with NO open-questions section: prose, a pipe table, a fenced code block, and an ORDINARY list
    // (not under an allow-listed heading). There is nothing to promote, so every piece must survive verbatim.
    private const string PassThroughDoc =
        "# Read Path Plan\n\n" +
        "A plain prose paragraph describing the goal.\n\n" +
        "- an ordinary list item\n" +
        "- another ordinary item\n\n" +
        "| Name | Role |\n" +
        "| --- | --- |\n" +
        "| Ada | Author |\n\n" +
        "```csharp\n" +
        "var answer = 42;\n" +
        "```\n";

    // A document with an Open Questions section of three simple items, plus untouched content on both sides.
    private const string OpenQuestionsDoc =
        "# Read Path Plan\n\n" +
        "Some narrative prose.\n\n" +
        "## Open Questions\n\n" +
        "- Which datastore should the read path use?\n" +
        "- Do we need multi-region on day one?\n" +
        "- What's the retention window?\n\n" +
        "## Next Steps\n\n" +
        "Wrap up.\n";

    private static string Seed(string markdown) => CharterFormat.EnsureVersionMarker(MarkdownConvert.Convert(markdown).Markdown);

    [Fact]
    public void PassThrough_NoOpenQuestions_LeavesTheBodyByteForByteUnchanged()
    {
        // The pure transform is a no-op on a doc with no promotable section — prose, table, code, and an
        // ordinary list are all already valid Charter blocks.
        Assert.Equal(PassThroughDoc, MarkdownConvert.Convert(PassThroughDoc).Markdown);
    }

    [Fact]
    public void PassThrough_Seed_PreservesEveryBlockVerbatim_ModuloTheMarker()
    {
        string seed = Seed(PassThroughDoc);

        // The only difference between the seed and the input is the prepended version marker.
        Assert.Equal("---\ncharter-format-version: 1\n---\n" + PassThroughDoc, seed);

        // And each block survives intact.
        Assert.Contains("A plain prose paragraph describing the goal.", seed);
        Assert.Contains("- an ordinary list item", seed);
        Assert.Contains("| Ada | Author |", seed);
        Assert.Contains("var answer = 42;", seed);
    }

    [Fact]
    public void NoOpenQuestions_Seed_IsUnchangedExceptTheMarker()
    {
        string seed = Seed(PassThroughDoc);

        Assert.Equal(VersionMarkerStatus.Ok, CharterFormat.ValidateVersionMarker(seed).Status);
        // No directive fence is introduced when there is nothing to promote.
        Assert.DoesNotMatch(LineStartDirective, seed);
    }

    [Fact]
    public void OpenQuestions_ThreeItems_BecomeThreeOpenQuestionBlocks()
    {
        string seed = Seed(OpenQuestionsDoc);

        var questions = BlockDocument.Parse(seed).Blocks.Where(b => b.Kind == BlockKind.Question).ToList();
        Assert.Equal(3, questions.Count);

        var titles = questions.Select(q => ParseBody(q).Title).ToList();
        Assert.Equal(
            new[]
            {
                "Which datastore should the read path use?",
                "Do we need multi-region on day one?",
                "What's the retention window?",
            },
            titles);

        foreach (var question in questions)
        {
            var spec = ParseBody(question);
            Assert.Equal(QuestionMode.FreeText, spec.Mode); // safest default — no options invented
            Assert.Equal(QuestionTarget.Human, spec.Target);
            Assert.Empty(spec.Answer); // no answer key => OPEN
            Assert.False(string.IsNullOrWhiteSpace(spec.Id)); // a generated, non-empty id
            Assert.Empty(spec.Options); // free-text carries no options
        }
    }

    [Fact]
    public void OpenQuestions_ContentOutsideTheSection_IsUntouched()
    {
        string seed = Seed(OpenQuestionsDoc);

        Assert.Contains("# Read Path Plan", seed);
        Assert.Contains("Some narrative prose.", seed);
        Assert.Contains("## Open Questions", seed); // the heading itself stays; only its list is promoted
        Assert.Contains("## Next Steps", seed);
        Assert.Contains("Wrap up.", seed);
        // The original bullet lines are gone — they became questions, not leftover list items.
        Assert.DoesNotContain("- Which datastore should the read path use?", seed);
    }

    [Fact]
    public void Output_AlwaysCarriesTheVersionMarker()
    {
        Assert.Equal(VersionMarkerStatus.Ok, CharterFormat.ValidateVersionMarker(Seed(OpenQuestionsDoc)).Status);
        Assert.Equal(VersionMarkerStatus.Ok, CharterFormat.ValidateVersionMarker(Seed(PassThroughDoc)).Status);
    }

    [Fact]
    public void Output_RoundTrips_ParsesWithValidQuestionBodiesAndNoLeakedDirective()
    {
        string seed = Seed(OpenQuestionsDoc);
        var blocks = BlockDocument.Parse(seed).Blocks;

        // No garbled/unknown directive leaked: every block classifies cleanly, and every ":::" line that exists
        // belongs to a :::question the block model recognizes (opener + closer per question).
        Assert.DoesNotContain(blocks, b => b.Kind == BlockKind.Unknown);

        var questions = blocks.Where(b => b.Kind == BlockKind.Question).ToList();
        Assert.NotEmpty(questions);
        foreach (var question in questions)
        {
            var body = InnerBody(question.RawContent);
            var (ok, error) = QuestionSpec.Validate(body);
            Assert.True(ok, error);
        }

        // The rendered seed produces no error/degradation placeholder.
        string html = CharterRenderer.Render(seed);
        Assert.DoesNotContain("could not be parsed", html);
        Assert.DoesNotContain("Unknown directive", html);
    }

    [Fact]
    public void GeneratedIds_AreDocumentUnique_EvenForIdenticalItemText()
    {
        string doc =
            "## Open Questions\n\n" +
            "- Same question text?\n" +
            "- Same question text?\n";

        string seed = Seed(doc);

        // Two identical items must not collide on id (that would double-write a single answer into both).
        Assert.Empty(QuestionResolution.FindDuplicateQuestionIds(seed));

        var ids = BlockDocument.Parse(seed).Blocks
            .Where(b => b.Kind == BlockKind.Question)
            .Select(b => ParseBody(b).Id)
            .ToList();
        Assert.Equal(2, ids.Count);
        Assert.Equal(2, ids.Distinct().Count());
    }

    [Fact]
    public void Heading_NotImmediatelyFollowedByAList_IsNotPromoted()
    {
        // An allow-listed heading followed by PROSE (not a list) is not "directly under" a list — conservative,
        // so nothing is promoted and the doc is unchanged.
        string doc =
            "## Open Questions\n\n" +
            "There are no bullet points here, just a paragraph.\n";

        Assert.Equal(doc, MarkdownConvert.Convert(doc).Markdown);
    }

    [Fact]
    public void ProseMentioningOpenQuestions_IsNotAHeading_AndIsNotPromoted()
    {
        // A mid-paragraph mention of "Open Questions" is not a heading; the following list is an ordinary list.
        string doc =
            "This section covers Open Questions in passing.\n\n" +
            "- not a question\n" +
            "- also not a question\n";

        Assert.Equal(doc, MarkdownConvert.Convert(doc).Markdown);
    }

    [Fact]
    public void ComplexListItem_WithNestedSubList_IsLeftVerbatim_NotForced()
    {
        // A top-level item that carries a nested sub-list is "complex" and does not cleanly map to a single
        // question, so it is left as-is while its simple sibling is promoted.
        string doc =
            "## Open Questions\n\n" +
            "- A simple decision to make?\n" +
            "- A complex item\n" +
            "  - with a nested detail\n";

        string converted = MarkdownConvert.Convert(doc).Markdown;

        var blocks = BlockDocument.Parse(converted).Blocks;
        Assert.Single(blocks, b => b.Kind == BlockKind.Question); // exactly the simple item promoted
        Assert.Contains("with a nested detail", converted); // the complex item survives verbatim
    }

    [Fact]
    public void ComplexItemInNumberedSection_IsLeftVerbatim_AndReportedAsSkipped()
    {
        // Charter #34: the item-5 repro reduced to (flat, nested, flat). The MIDDLE item carries nested
        // sub-bullets PLUS a trailing "Decision:" paragraph — three children — so it is complex and must be
        // emitted verbatim rather than crammed into one giant :::question (which would only make later
        // enrichment harder). The two flat siblings promote; the report names the one item left as prose, so
        // the operation is transparent, not silent — the whole point of the fix.
        string doc =
            "## 9. Open questions / risks\n\n" +
            "1. **Per-machine vs per-user install.** Which scope should the installer default to?\n" +
            "2. **Forced `MinimumVersion` when `SelfUpdateEnabled=0`.** This intentionally lets the app self-update for security even when the admin disabled self-update, but it has two independent limitations, not one:\n" +
            "   - **Permission coupling (see #1):** the update needs elevated rights.\n" +
            "   - **Same-major ceiling:** it will not cross a major boundary.\n" +
            "   Decision: treat the **Intune required assignment** as the security floor. Confirm this split with the customer's security team.\n" +
            "3. **Rollback.** How does a failed rollout revert cleanly?\n";

        ConvertResult result = MarkdownConvert.Convert(doc);

        // The two flat items became :::question blocks; the nested middle item did not.
        var questions = BlockDocument.Parse(Seed(doc)).Blocks.Where(b => b.Kind == BlockKind.Question).ToList();
        Assert.Equal(2, questions.Count);
        var titles = questions.Select(q => ParseBody(q).Title).ToList();
        Assert.Equal(
            new[]
            {
                "Per-machine vs per-user install. Which scope should the installer default to?",
                "Rollback. How does a failed rollout revert cleanly?",
            },
            titles);

        // The complex item survives verbatim in the produced markdown — its content is preserved intact, not
        // dropped and not forced into a question.
        Assert.Contains("Same-major ceiling", result.Markdown);
        Assert.DoesNotContain("Same-major ceiling", string.Join("\n", titles));

        // The report is transparent about exactly what it left behind: 2 promoted, 1 skipped at ordinal 2.
        ConvertedSection section = Assert.Single(result.Sections);
        Assert.Contains("9. Open questions / risks", section.Heading);
        Assert.Equal(2, section.Promoted);
        SkippedItem skipped = Assert.Single(section.Skipped);
        Assert.Equal(2, skipped.Ordinal); // 1-based position within the list — the MIDDLE item
        Assert.False(string.IsNullOrWhiteSpace(skipped.LeadSnippet));
        Assert.Contains("Forced MinimumVersion", skipped.LeadSnippet); // the item's lead text, markup-stripped
    }

    [Fact]
    public void RisksSection_IsAlsoPromoted()
    {
        // "Risks" is a trigger word — its bullets are decisions the reviewer must weigh.
        string doc =
            "## Risks\n\n" +
            "- Data loss during migration.\n" +
            "- Downtime exceeds the window.\n";

        var questions = BlockDocument.Parse(Seed(doc)).Blocks.Where(b => b.Kind == BlockKind.Question).ToList();
        Assert.Equal(2, questions.Count);
    }

    [Fact]
    public void NumberedHeading_CombiningTriggers_WithOrderedList_BecomesThreeOpenQuestions()
    {
        // Charter #31: the real-world dogfood shape — a NUMBERED/prefixed heading combining two triggers
        // ("9. Open questions / risks") over an ORDERED (numbered) list whose items carry bold lead-ins. Both
        // the exact-allow-list miss and the ordered-list handling are exercised at once.
        string doc =
            "## 9. Open questions / risks\n\n" +
            "1. **Per-machine vs per-user install.** Which scope should the installer default to?\n" +
            "2. **Version pinning.** Do we pin a floor or an exact version?\n" +
            "3. **Rollback.** How does a failed rollout revert cleanly?\n";

        string seed = Seed(doc);
        var questions = BlockDocument.Parse(seed).Blocks.Where(b => b.Kind == BlockKind.Question).ToList();
        Assert.Equal(3, questions.Count);

        var titles = questions.Select(q => ParseBody(q).Title).ToList();
        Assert.Equal(
            new[]
            {
                "Per-machine vs per-user install. Which scope should the installer default to?",
                "Version pinning. Do we pin a floor or an exact version?",
                "Rollback. How does a failed rollout revert cleanly?",
            },
            titles);

        foreach (var question in questions)
        {
            var spec = ParseBody(question);
            Assert.Equal(QuestionMode.FreeText, spec.Mode); // safest default — no options invented
            Assert.Equal(QuestionTarget.Human, spec.Target);
            Assert.Empty(spec.Answer); // OPEN
            Assert.False(string.IsNullOrWhiteSpace(spec.Id));
        }
    }

    [Fact]
    public void OrderedList_UnderBareQuestionsHeading_IsPromoted()
    {
        // An ordered list under a bare "Questions" heading promotes just as a bullet list would.
        string doc =
            "## Questions\n\n" +
            "1. Which datastore should the read path use?\n" +
            "2. Do we need multi-region on day one?\n";

        var questions = BlockDocument.Parse(Seed(doc)).Blocks.Where(b => b.Kind == BlockKind.Question).ToList();
        Assert.Equal(2, questions.Count);
    }

    [Fact]
    public void ArchitectureHeading_WithList_IsNotPromoted()
    {
        // Over-fire guard: a heading carrying NO trigger word must never be promoted, even directly over a list.
        string doc =
            "## Architecture\n\n" +
            "- The read path is stateless.\n" +
            "- The write path is append-only.\n";

        Assert.Equal(doc, MarkdownConvert.Convert(doc).Markdown);
    }

    [Fact]
    public void HeadingWithTriggerOnlyAsSubstring_IsNotPromoted()
    {
        // Whole-word guard: "Brisketry" contains the letters of "risk" but not the WHOLE word, so it must not
        // fire — a naive substring match would wrongly promote this list.
        string doc =
            "## Brisketry\n\n" +
            "- Smoke low and slow.\n" +
            "- Rest before slicing.\n";

        Assert.Equal(doc, MarkdownConvert.Convert(doc).Markdown);
    }

    [Fact]
    public void DecisionsAndOpenIssuesHeading_Fires_OnEitherTrigger()
    {
        // A heading combining "decisions" and "open issues" fires; both are triggers.
        string doc =
            "## Decisions and open issues\n\n" +
            "- Adopt the new format?\n" +
            "- Deprecate the legacy path?\n";

        var questions = BlockDocument.Parse(Seed(doc)).Blocks.Where(b => b.Kind == BlockKind.Question).ToList();
        Assert.Equal(2, questions.Count);
    }

    [Fact]
    public void Convert_IsDeterministic()
    {
        Assert.Equal(MarkdownConvert.Convert(OpenQuestionsDoc).Markdown, MarkdownConvert.Convert(OpenQuestionsDoc).Markdown);
    }

    // Parse a promoted question block's JSON body into its validated spec.
    private static QuestionSpec ParseBody(Block question) => QuestionSpec.Parse(InnerBody(question.RawContent));

    // The JSON body of a ":::question\n<json>\n:::" block — everything between the opening fence line and the
    // closing ":::" fence.
    private static string InnerBody(string rawContent)
    {
        var normalized = rawContent.Replace("\r\n", "\n", StringComparison.Ordinal);
        var bodyStart = normalized.IndexOf('\n') + 1;
        var closeFence = normalized.LastIndexOf(":::", StringComparison.Ordinal);
        return normalized[bodyStart..closeFence].Trim();
    }
}
