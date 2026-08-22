using System.Text.Json;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// The drift guard for <c>&lt;plan&gt;.headless.json</c> (Charter #173): it BINDS the record's emitted shape to
/// the one document a consumer reads, <c>skills/charter/references/unattended.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// The version marker this record needs already existed — <see cref="HeadlessRecord.Schema"/>, documented as
/// "bumped when the on-disk shape changes incompatibly" — and it had <b>already failed</b>. <c>recommended</c>
/// was added in Charter #142 with the number left at 1, so there are records in the wild, both
/// <c>"schema": 1</c>, with different question shapes; and <c>unattended.md</c> still showed the pre-#142
/// example. <b>A prose promise is exactly what did not work</b>, which is why the deliverable here is a test
/// rather than a stability section: an addition that ships undocumented now fails the build, naming the field.
/// </para>
/// <para>
/// Modelled on <see cref="CharterFormatDriftTests"/>, which binds the <c>charter-format</c> skill's catalog and
/// frontmatter to <see cref="CharterFormat.Version"/> for the same reason and after the same class of miss
/// (Charter #155).
/// </para>
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","HeadlessRecordContract")].
/// </remarks>
[Trait("Category", "HeadlessRecordContract")]
public class HeadlessRecordContractTests
{
    /// <summary>
    /// The STABLE CORE a consumer may assert on across versions, declared here as the test-side source of
    /// truth. A field named here that stops being emitted fails the build, which is the point: it is the half
    /// of the record that cannot move without a <see cref="HeadlessRecord.Schema"/> bump.
    /// </summary>
    private static readonly string[] StableTopLevelFields =
    {
        "schema", "charterVersion", "plan", "planSha256", "needsHuman",
    };

    /// <summary>The stable core of a <c>questions[]</c> entry.</summary>
    private static readonly string[] StableQuestionFields = { "id", "target", "answered" };

    // A plan exercising every emitted shape at once: a marker, an answered and an open question, a
    // recommended lean, an unknown directive, and an untracked deferral.
    private const string RichPlan =
        "---\ncharter-format-version: 1\n---\n\n"
        + "# Plan\n\n"
        + "The retry work is out of scope for this stage.\n\n"
        + ":::question\n{\"id\":\"db\",\"title\":\"Which database?\",\"mode\":\"single\",\"target\":\"human\","
        + "\"options\":[\"Postgres\",\"MySQL\"],\"answer\":[\"Postgres\"],\"recommended\":\"Postgres\"}\n:::\n\n"
        + ":::question\n{\"id\":\"cache\",\"title\":\"Which cache?\",\"mode\":\"single\",\"target\":\"human\","
        + "\"options\":[\"Redis\",\"in-memory\"]}\n:::\n\n"
        + ":::file-tree\nsrc/\n:::\n";

    [Fact]
    public void EveryEmittedField_IsDocumentedInUnattendedMd()
    {
        var doc = ReadDoc();
        var root = Record().RootElement;

        foreach (var property in root.EnumerateObject())
        {
            AssertDocumented(doc, property.Name, "a top-level record field");
        }

        foreach (var property in root.GetProperty("questions")[0].EnumerateObject())
        {
            AssertDocumented(doc, property.Name, "a questions[] field");
        }

        foreach (var property in root.GetProperty("notes")[0].EnumerateObject())
        {
            AssertDocumented(doc, property.Name, "a notes[] field");
        }

        foreach (var property in root.GetProperty("planFormatVersion").EnumerateObject())
        {
            AssertDocumented(doc, property.Name, "a planFormatVersion field");
        }
    }

    [Fact]
    public void EveryNoteKindToken_IsDocumentedInUnattendedMd()
    {
        // notes[] is where the failure of the old prose promise actually bit: `notes: []` did NOT mean
        // "Charter noticed nothing", because `handoff` printed two lints the record had no kind for. Adding a
        // kind later flips a consumer's `notes.length === 0` assertion with nobody at fault, so every kind the
        // enum can produce must be named where the consumer reads.
        var doc = ReadDoc();

        foreach (var kind in Enum.GetValues<HeadlessNoteKind>())
        {
            AssertDocumented(doc, HeadlessNote.Token(kind), "a notes[].kind token");
        }
    }

    [Fact]
    public void TheDocumentedSchemaNumber_IsTheCodesOwn()
    {
        // The example a consumer copies must be the CURRENT shape, not the one that shipped first. This is the
        // exact assertion #142 would have failed.
        Assert.Contains($"\"schema\": {HeadlessRecord.Schema}", ReadDoc(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStableCore_IsEmitted_AndIsDeclaredStableInTheDoc()
    {
        var doc = ReadDoc();
        var root = Record().RootElement;

        foreach (var field in StableTopLevelFields)
        {
            Assert.True(
                root.TryGetProperty(field, out _),
                $"the record no longer emits '{field}', which is declared part of its STABLE CORE. Removing "
                    + "one is a HeadlessRecord.Schema bump plus an edit to unattended.md and to this test.");
        }

        foreach (var field in StableQuestionFields)
        {
            Assert.True(
                root.GetProperty("questions")[0].TryGetProperty(field, out _),
                $"the record no longer emits questions[].{field}, which is declared part of its STABLE CORE.");
        }

        // The doc must say WHICH half is the contract, or a consumer has no way to tell a load-bearing field
        // from a presentational one and will assert on all of them.
        Assert.Contains("Stable core", doc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDoc_DeclaresTheNonContractHalf_Explicitly()
    {
        var doc = ReadDoc();

        // Message strings: they embed ids and are written for a human, so a consumer matching on one will
        // break on a wording fix nobody thought of as a change.
        Assert.Contains("`message`", doc, StringComparison.Ordinal);

        // sourceMap VALUES: the anchor ids moved TWICE inside v0.24.0 alone (#166 stopped treating an id
        // inside an opaque region as an anchor; #171 stopped a link-reference group stealing the title's
        // slot). A consumer may join questions[].anchorId to sourceMap within ONE record; never across
        // records from different Charter versions.
        Assert.Contains("anchor id", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("within one record", doc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDoc_TellsAConsumerToIgnoreAnUnrecognisedNoteKind()
    {
        // Never reject. New kinds are the compatible change this contract is built to absorb, and a consumer
        // that refuses the whole file over one turns a diagnostic into an outage.
        var doc = ReadDoc();

        Assert.Contains("ignore", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unrecognised `kind`", doc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDoc_StatesWhatEachAbsenceMeans()
    {
        // Every one of these is a place where "empty" and "not applicable" and "too old to know" look
        // identical on the wire, and a harness guessing wrong asserts something false with total confidence.
        var doc = ReadDoc();

        foreach (var required in new[]
                 {
                     "`options: []`",
                     "`recommended`",
                     "`sourceLine`",
                     "`answered`",
                 })
        {
            Assert.True(
                doc.Contains(required, StringComparison.Ordinal),
                $"unattended.md does not state what an absent {required} means.");
        }
    }

    [Fact]
    public void PlanFormatVersion_DistinguishesAMissingMarkerFromANonIntegerOne()
    {
        // A bare int could not: CharterFormat reports no version for BOTH, so `charter-format-version: 1.0`
        // read identically to an unstamped plan. The pair is status + the raw declared value.
        var unstamped = Record("# Plan\n").RootElement.GetProperty("planFormatVersion");
        var nonInteger = Record("---\ncharter-format-version: 1.0\n---\n\n# Plan\n")
            .RootElement.GetProperty("planFormatVersion");
        var ok = Record(RichPlan).RootElement.GetProperty("planFormatVersion");

        Assert.Equal("missing", unstamped.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, unstamped.GetProperty("marker").ValueKind);

        Assert.Equal("unsupported", nonInteger.GetProperty("status").GetString());
        Assert.Equal("1.0", nonInteger.GetProperty("marker").GetString());
        Assert.Equal(JsonValueKind.Null, nonInteger.GetProperty("version").ValueKind);

        Assert.Equal("ok", ok.GetProperty("status").GetString());
        Assert.Equal(CharterFormat.Version, ok.GetProperty("version").GetInt32());
    }

    [Fact]
    public void TheTwoLintsHandoffPrints_NowHaveNoteKinds()
    {
        // Until they did, `notes: []` did not mean "Charter noticed nothing" -- and "did Charter raise
        // diagnostics nobody read" is precisely the query #173's harness asks of this file.
        var kinds = Record().RootElement.GetProperty("notes").EnumerateArray()
            .Select(note => note.GetProperty("kind").GetString())
            .ToList();

        Assert.Contains("missing-recommendation", kinds);
        Assert.Contains("untracked-deferral", kinds);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static JsonDocument Record(string markdown = RichPlan)
        => JsonDocument.Parse(
            HeadlessRecord.Build(markdown, "p.charter.md", "p.charter.html", "0.0.0-test").ToJson());

    private static void AssertDocumented(string doc, string token, string what)
        => Assert.True(
            doc.Contains("`" + token + "`", StringComparison.Ordinal),
            $"unattended.md does not document '{token}' ({what}). The record's shape is a CONTRACT a "
                + "post-mortem harness asserts against; an undocumented field is one it can only discover by "
                + "reading Charter's source, and a field that ships undocumented is exactly how `recommended` "
                + "reached the wild while the published example still showed the shape before it.");

    /// <summary>Read unattended.md, located by walking up to the repo root (Charter.sln).</summary>
    private static string ReadDoc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Charter.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "could not locate the repo root (Charter.sln) from the test base directory.");

        var path = Path.Combine(dir!.FullName, "skills", "charter", "references", "unattended.md");
        Assert.True(File.Exists(path), $"unattended.md not found at {path}.");
        return File.ReadAllText(path);
    }
}
