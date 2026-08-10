using System.Linq;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Behavioral tests for <see cref="DiffRecap"/>, the deterministic git-diff -> <c>.charter.md</c> SEED
/// transform behind <c>charter recap</c> (Charter #1). Three properties are load-bearing:
/// (a) the seed is a VALID, round-trippable <c>.charter.md</c> whose diffs render per-line, because the whole
/// point is reviewing a change in Charter's existing annotation loop;
/// (b) nothing is silently lost — arbitrary repository content must not be able to truncate a block, and a cap
/// must always announce itself; and
/// (c) it stays MECHANICAL — it never invents the summary, diagram or questions that are the agent's job.
/// </summary>
[Trait("Category", "Recap")]
public class DiffRecapTests
{
    /// <summary>A two-file diff: one modified, one added. The shape most recaps are made of.</summary>
    private const string OrdinaryDiff =
        "diff --git a/src/Reader.cs b/src/Reader.cs\n" +
        "index 1111111..2222222 100644\n" +
        "--- a/src/Reader.cs\n" +
        "+++ b/src/Reader.cs\n" +
        "@@ -10,3 +10,4 @@ public sealed class Reader\n" +
        "     public int Count { get; }\n" +
        "-    private int _stale;\n" +
        "+    private int _fresh;\n" +
        "+    private int _added;\n" +
        "diff --git a/src/New.cs b/src/New.cs\n" +
        "new file mode 100644\n" +
        "index 0000000..3333333\n" +
        "--- /dev/null\n" +
        "+++ b/src/New.cs\n" +
        "@@ -0,0 +1,2 @@\n" +
        "+public sealed class New;\n" +
        "+// end\n";

    private static string Seed(string markdown) => CharterFormat.EnsureVersionMarker(markdown);

    // -------------------------------------------------------------------------------------------------
    // Parsing
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void ParsesEachFile_WithItsChangeKindAndLineCounts()
    {
        var result = DiffRecap.Build("main..HEAD", OrdinaryDiff);

        Assert.Equal(2, result.Files.Count);

        var modified = result.Files[0];
        Assert.Equal("src/Reader.cs", modified.Path);
        Assert.Equal(RecapChange.Modified, modified.Change);
        Assert.Equal(2, modified.Added);
        Assert.Equal(1, modified.Removed);

        var added = result.Files[1];
        Assert.Equal("src/New.cs", added.Path);
        Assert.Equal(RecapChange.Added, added.Change);
        Assert.Equal(2, added.Added);
        Assert.Equal(0, added.Removed);
    }

    /// <summary>
    /// The <c>---</c>/<c>+++</c> preamble must never reach the block. Those lines begin with the same markers
    /// as removed/added content, so emitting them would both corrupt the +/- counts and show the reviewer two
    /// phantom changes per file.
    /// </summary>
    [Fact]
    public void TheFileHeaderPreamble_IsNotEmittedAsDiffContent()
    {
        var result = DiffRecap.Build("main..HEAD", OrdinaryDiff);

        Assert.All(result.Files, file =>
            Assert.DoesNotContain(file.DiffLines, line =>
                line.StartsWith("--- ", System.StringComparison.Ordinal)
                || line.StartsWith("+++ ", System.StringComparison.Ordinal)
                || line.StartsWith("index ", System.StringComparison.Ordinal)));

        Assert.All(result.Files, file => Assert.StartsWith("@@", file.DiffLines[0]));
    }

    [Fact]
    public void ARename_KeepsBothPaths_SoTheReviewerSeesWhatMoved()
    {
        const string diff =
            "diff --git a/old/Name.cs b/new/Name.cs\n" +
            "similarity index 96%\n" +
            "rename from old/Name.cs\n" +
            "rename to new/Name.cs\n" +
            "--- a/old/Name.cs\n" +
            "+++ b/new/Name.cs\n" +
            "@@ -1,2 +1,2 @@\n" +
            "-// old\n" +
            "+// new\n";

        var file = Assert.Single(DiffRecap.Build("main..HEAD", diff).Files);

        Assert.Equal(RecapChange.Renamed, file.Change);
        Assert.Equal("new/Name.cs", file.Path);
        Assert.Equal("old/Name.cs", file.OldPath);
        Assert.Contains("Renamed from", DiffRecap.Build("main..HEAD", diff).Markdown);
    }

    [Fact]
    public void ADeletion_IsReportedAsDeleted_NotAsAHeavilyModifiedFile()
    {
        const string diff =
            "diff --git a/gone/Old.cs b/gone/Old.cs\n" +
            "deleted file mode 100644\n" +
            "--- a/gone/Old.cs\n" +
            "+++ /dev/null\n" +
            "@@ -1,2 +0,0 @@\n" +
            "-// line one\n" +
            "-// line two\n";

        var file = Assert.Single(DiffRecap.Build("main..HEAD", diff).Files);

        Assert.Equal(RecapChange.Deleted, file.Change);
        Assert.Equal(2, file.Removed);
    }

    /// <summary>
    /// A binary file has no reviewable diff. It must still APPEAR — a change the recap omits entirely is a
    /// change the reviewer approves without knowing it happened — and it must be named in the notes.
    /// </summary>
    [Fact]
    public void ABinaryFile_IsListedAndReported_NeverSilentlyOmitted()
    {
        const string diff =
            "diff --git a/docs/logo.png b/docs/logo.png\n" +
            "index 4444444..5555555 100644\n" +
            "Binary files a/docs/logo.png and b/docs/logo.png differ\n";

        var result = DiffRecap.Build("main..HEAD", diff);

        var file = Assert.Single(result.Files);
        Assert.True(file.IsBinary);
        Assert.Empty(file.DiffLines);
        Assert.Contains("docs/logo.png", result.Markdown);
        Assert.Contains(result.Notes, note => note.Contains("binary", System.StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Git's "\ No newline at end of file" is an annotation about the diff, not a line of it — kept,
    /// it would render as a change the file does not contain.</summary>
    [Fact]
    public void TheNoNewlineAnnotation_IsNotADiffLine()
    {
        const string diff =
            "diff --git a/a.txt b/a.txt\n" +
            "--- a/a.txt\n" +
            "+++ b/a.txt\n" +
            "@@ -1 +1 @@\n" +
            "-old\n" +
            @"\ No newline at end of file" + "\n" +
            "+new\n";

        var file = Assert.Single(DiffRecap.Build("main..HEAD", diff).Files);

        Assert.DoesNotContain(file.DiffLines, line => line.StartsWith(@"\", System.StringComparison.Ordinal));
        Assert.Equal(1, file.Added);
        Assert.Equal(1, file.Removed);
    }

    [Fact]
    public void AnEmptyDiff_ProducesNoFiles_AndStillBuildsAValidSeed()
    {
        var result = DiffRecap.Build("main..HEAD", string.Empty);

        Assert.Empty(result.Files);
        Assert.Contains("Files changed | 0", result.Markdown);
        Assert.DoesNotContain(BlockDocument.Parse(Seed(result.Markdown)).Blocks, b => b.Kind == BlockKind.Unknown);
    }

    // -------------------------------------------------------------------------------------------------
    // The two fences — the defects that make a recap silently wrong
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// THE defect this transform exists to avoid. A container's close check runs before its inner code fence
    /// consumes the line, so a diff CONTEXT line whose trimmed text is <c>:::</c> closes a three-colon
    /// <c>:::diff</c> and every later line drops out of the block with no error. Any diff of a
    /// <c>.charter.md</c> produces that line. Asserted end-to-end through the real parser, because reading the
    /// parser is exactly what would get this wrong.
    /// </summary>
    [Fact]
    public void ADiffOfACharterFile_KeepsEveryLine_RatherThanClosingItsOwnBlockEarly()
    {
        const string diff =
            "diff --git a/plan.charter.md b/plan.charter.md\n" +
            "--- a/plan.charter.md\n" +
            "+++ b/plan.charter.md\n" +
            "@@ -1,6 +1,7 @@\n" +
            " :::diff\n" +
            " + one\n" +
            "++ two\n" +
            " :::\n" +
            " :::note\n" +
            " the tail that used to vanish\n";

        var result = DiffRecap.Build("main..HEAD", diff);
        var html = CharterRenderer.Render(Seed(result.Markdown));

        // Every non-blank body line survives into the rendered diff, including the two that used to end it.
        Assert.Contains("the tail that used to vanish", html);
        Assert.Contains(":::note", html);
        Assert.Equal(1, CountOccurrences(html, "class=\"diff\""));
    }

    /// <summary>A body containing a fenced code block must not close the diff's own code fence.</summary>
    [Fact]
    public void ADiffOfAMarkdownFileWithCodeFences_KeepsEveryLine()
    {
        const string diff =
            "diff --git a/README.md b/README.md\n" +
            "--- a/README.md\n" +
            "+++ b/README.md\n" +
            "@@ -1,4 +1,4 @@\n" +
            " ```sh\n" +
            "-charter render old\n" +
            "+charter render new\n" +
            " ```\n" +
            " trailing prose\n";

        var result = DiffRecap.Build("main..HEAD", diff);
        var html = CharterRenderer.Render(Seed(result.Markdown));

        Assert.Contains("trailing prose", html);
        Assert.Contains("charter render new", html);
    }

    [Theory]
    [InlineData(new[] { "+ ordinary" }, ":::", "```")]
    [InlineData(new[] { " :::" }, "::::", "```")]
    [InlineData(new[] { " ::::deep" }, ":::::", "```")]
    [InlineData(new[] { " ```sh" }, ":::", "````")]
    [InlineData(new[] { " :::", " ```" }, "::::", "````")]
    public void EachFence_IsWidenedOnlyAsFarAsTheContentRequires(
        string[] body, string expectedContainer, string expectedCode)
    {
        Assert.Equal(expectedContainer, DiffRecap.ContainerFence(body));
        Assert.Equal(expectedCode, DiffRecap.CodeFence(body));
    }

    // -------------------------------------------------------------------------------------------------
    // The cap
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A cap that hid its own effect would make the recap WRONG rather than short: the reviewer would annotate
    /// a change believing they had seen all of it. The omission is stated three ways — on the record, in the
    /// rendered block, and in the notes the CLI prints.
    /// </summary>
    [Fact]
    public void ACappedFile_AnnouncesWhatItHid_InTheRecordTheBlockAndTheNotes()
    {
        var body = string.Concat(Enumerable.Range(0, 50).Select(i => $"+line {i}\n"));
        var diff =
            "diff --git a/big.cs b/big.cs\n" +
            "--- a/big.cs\n" +
            "+++ b/big.cs\n" +
            "@@ -0,0 +1,50 @@\n" + body;

        var result = DiffRecap.Build("main..HEAD", diff, commits: null, maxDiffLinesPerFile: 10);

        var file = Assert.Single(result.Files);
        Assert.Equal(10, file.DiffLines.Count);
        Assert.Equal(41, file.OmittedLines);                 // 50 body lines + the @@ header, less the 10 kept
        Assert.Equal(50, file.Added);                        // counts describe the WHOLE file, not the excerpt
        Assert.Contains(":::warn", result.Markdown);
        Assert.Contains("not shown", result.Markdown);
        Assert.Contains(result.Notes, note => note.Contains("big.cs", System.StringComparison.Ordinal));
    }

    [Fact]
    public void AZeroCap_MeansNoCap()
    {
        var body = string.Concat(Enumerable.Range(0, 30).Select(i => $"+line {i}\n"));
        var diff = "diff --git a/big.cs b/big.cs\n--- a/big.cs\n+++ b/big.cs\n@@ -0,0 +1,30 @@\n" + body;

        var file = Assert.Single(DiffRecap.Build("r", diff, commits: null, maxDiffLinesPerFile: 0).Files);

        Assert.Equal(0, file.OmittedLines);
        Assert.Equal(31, file.DiffLines.Count);
    }

    // -------------------------------------------------------------------------------------------------
    // Escaping — the seed is generated from arbitrary repository content
    // -------------------------------------------------------------------------------------------------

    /// <summary>A commit subject is arbitrary text. A literal <c>|</c> in one would silently add a column to
    /// the commit table and shift every later cell.</summary>
    [Fact]
    public void ACommitSubjectContainingAPipe_DoesNotBreakTheCommitTable()
    {
        var commits = new[] { new RecapCommit("abc1234", "fix: guard a || b in the parser") };

        var markdown = DiffRecap.Build("main..HEAD", OrdinaryDiff, commits).Markdown;

        var row = markdown.Split('\n').Single(l => l.Contains("abc1234", System.StringComparison.Ordinal));
        Assert.Contains(@"\|", row);

        // The RENDERED row is the property that matters: exactly two cells, with the pipes surviving as text.
        var html = CharterRenderer.Render(Seed(markdown));
        var rendered = html.Split("<tr>").Single(r => r.Contains("abc1234", System.StringComparison.Ordinal));
        Assert.Equal(2, rendered.Split("<td").Length - 1);
        Assert.Contains("a || b", rendered);
    }

    [Fact]
    public void APathContainingABacktick_IsStillRenderedAsCode()
    {
        const string diff =
            "diff --git a/odd/`weird`.cs b/odd/`weird`.cs\n" +
            "--- a/odd/`weird`.cs\n" +
            "+++ b/odd/`weird`.cs\n" +
            "@@ -1 +1 @@\n" +
            "+ok\n";

        var html = CharterRenderer.Render(Seed(DiffRecap.Build("r", diff).Markdown));

        Assert.Contains("weird", html);
        Assert.DoesNotContain("<h3></h3>", html);
    }

    // -------------------------------------------------------------------------------------------------
    // The contract: a valid seed, and only the mechanical half of one
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSeed_IsAValidRoundTrippableCharterDocument()
    {
        var seed = Seed(DiffRecap.Build("main..HEAD", OrdinaryDiff).Markdown);

        var blocks = BlockDocument.Parse(seed).Blocks;

        // No :::foo the catalog does not define -- an Unknown block is how a malformed directive surfaces.
        Assert.DoesNotContain(blocks, b => b.Kind == BlockKind.Unknown);
        Assert.Equal(2, blocks.Count(b => b.Kind == BlockKind.Diff));
        Assert.Equal(VersionMarkerStatus.Ok, CharterFormat.ValidateVersionMarker(seed).Status);
    }

    /// <summary>Each file's diff renders as a per-LINE annotatable block — the reason a recap is worth more
    /// than `git diff`, and the thing that would quietly stop working if the body form changed.</summary>
    [Fact]
    public void EveryFilesDiff_RendersAsAPerLineAnnotatableBlock()
    {
        var html = CharterRenderer.Render(Seed(DiffRecap.Build("main..HEAD", OrdinaryDiff).Markdown));

        Assert.Equal(2, CountOccurrences(html, "class=\"diff\""));
        Assert.Contains("diff-line diff-add", html);
        Assert.Contains("diff-line diff-del", html);

        // The fence delimiters are structure, not content: they must never become annotatable diff lines.
        Assert.DoesNotContain(">```diff<", html);
    }

    /// <summary>
    /// The guard on scope. Charter's binary holds no model, so recap emits only what the diff STATES; the
    /// summary, the grouping, the diagram and the questions are the authoring agent's job. If a future change
    /// starts synthesizing them here, this fails — deliberately.
    /// </summary>
    [Fact]
    public void ItSynthesizesNoJudgment_NoDiagramAndNoQuestions()
    {
        var markdown = DiffRecap.Build("main..HEAD", OrdinaryDiff).Markdown;

        Assert.DoesNotContain(":::diagram", markdown);
        Assert.DoesNotContain(":::question", markdown);
        Assert.DoesNotContain(":::comparison", markdown);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, System.StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, System.StringComparison.Ordinal);
        }

        return count;
    }
}
