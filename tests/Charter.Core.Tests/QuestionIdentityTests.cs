using System;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #75 item 3 — the evidence that lets a queued answer be checked before it is folded into a plan.
/// An answer is keyed by <c>:::question</c> id, not by an anchor, so the replaced-plan evidence that quarantines
/// annotations says nothing about it: a plan re-authored at the same path that reuses <c>db-choice</c> could
/// inherit the old document's decision, and unlike an orphaned annotation that write lands IN THE PLAN FILE.
///
/// <see cref="QuestionIdentity"/> fingerprints the question's DECLARED shape — id, title, mode, target, options
/// — and deliberately not its answer. These tests pin both halves: what must change the fingerprint (anything
/// the reviewer read before choosing) and what must NOT (the answer being written back, which happens on every
/// successful apply and would otherwise make the next apply look stale).
/// </summary>
[Trait("Category", "QuestionIdentity")]
public class QuestionIdentityTests
{
    private const string DbChoice =
        ":::question\n"
        + "{ \"id\": \"db-choice\", \"title\": \"Which database?\", \"mode\": \"single\", "
        + "\"target\": \"human\", \"options\": [\"Postgres\", \"SQLite\"] }\n"
        + ":::\n";

    private static string Plan(string question) =>
        "# A plan\n\nSome prose the reviewer reads first.\n\n" + question + "\nTrailing prose.\n";

    // ---- what must NOT change the fingerprint -------------------------------------------------------------

    [Fact]
    public void Fingerprint_IsUnchangedByTheAnswerBeingWrittenBack()
    {
        // Load-bearing. `QuestionResolution.Apply` splices "answer" into the block's JSON body, so if the
        // fingerprint covered it then applying one answer would make the SAME answer, or a sibling answer in
        // the same pass, look stale — and the guard would fire on its own success.
        var before = QuestionIdentity.FingerprintOf(Plan(DbChoice), "db-choice");
        var after = QuestionIdentity.FingerprintOf(
            QuestionResolution.Apply(Plan(DbChoice), new Dictionary<string, IReadOnlyList<string>>
            {
                ["db-choice"] = new[] { "Postgres" },
            }),
            "db-choice");

        Assert.NotNull(before);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Fingerprint_IsUnchangedByEditsElsewhereInThePlan()
    {
        // The living-document model: prose is rewritten, blocks are inserted, other questions are answered, and
        // a decision on THIS question stays perfectly applicable.
        var edited =
            "# A completely rewritten heading\n\nEntirely new prose.\n\nAnd another new paragraph.\n\n"
            + DbChoice;

        Assert.Equal(
            QuestionIdentity.FingerprintOf(Plan(DbChoice), "db-choice"),
            QuestionIdentity.FingerprintOf(edited, "db-choice"));
    }

    // ---- what MUST change the fingerprint -----------------------------------------------------------------

    [Theory]
    [InlineData("\"title\": \"Which database?\"", "\"title\": \"Which datastore, exactly?\"")]
    [InlineData("\"mode\": \"single\"", "\"mode\": \"multi\"")]
    [InlineData("\"target\": \"human\"", "\"target\": \"agent\"")]
    [InlineData("[\"Postgres\", \"SQLite\"]", "[\"Postgres\", \"MySQL\"]")]
    [InlineData("[\"Postgres\", \"SQLite\"]", "[\"SQLite\", \"Postgres\"]")]
    [InlineData("[\"Postgres\", \"SQLite\"]", "[\"Postgres\", \"SQLite\", \"DuckDB\"]")]
    public void Fingerprint_ChangesWhenAnythingTheReviewerReadChanges(string original, string replacement)
    {
        var changed = DbChoice.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(DbChoice, changed);

        Assert.NotEqual(
            QuestionIdentity.FingerprintOf(Plan(DbChoice), "db-choice"),
            QuestionIdentity.FingerprintOf(Plan(changed), "db-choice"));
    }

    [Fact]
    public void Fingerprint_ChangesWhenAReplacedPlanReusesTheIdForADifferentQuestion()
    {
        // The reported case: a `charter convert` seed regenerates a question the old document also had.
        const string reused =
            ":::question\n"
            + "{ \"id\": \"db-choice\", \"title\": \"Which tenancy model?\", \"mode\": \"single\", "
            + "\"target\": \"human\", \"options\": [\"Schema per tenant\", \"Row-level\"] }\n"
            + ":::\n";

        Assert.NotEqual(
            QuestionIdentity.FingerprintOf(Plan(DbChoice), "db-choice"),
            QuestionIdentity.FingerprintOf(Plan(reused), "db-choice"));
    }

    // ---- "no evidence" cases, which must all read as null (never as a mismatch) ----------------------------

    [Fact]
    public void FingerprintOf_AnAbsentQuestion_IsNull_NotAMismatch()
        => Assert.Null(QuestionIdentity.FingerprintOf(Plan(DbChoice), "rollout-scope"));

    [Fact]
    public void FingerprintOf_AMalformedQuestionBody_IsNull()
    {
        const string malformed = ":::question\n{ \"id\": \"db-choice\", not json at all\n:::\n";
        Assert.Null(QuestionIdentity.FingerprintOf(Plan(malformed), "db-choice"));
    }

    [Theory]
    [InlineData("", "db-choice")]
    [InlineData("# Just prose\n", "")]
    public void FingerprintOf_EmptyInputs_AreNull(string markdown, string questionId)
        => Assert.Null(QuestionIdentity.FingerprintOf(markdown, questionId));

    // ---- the value itself ---------------------------------------------------------------------------------

    [Fact]
    public void Fingerprint_IsAStableLowercaseSha256_SoItSurvivesAProcessRestart()
    {
        var fingerprint = QuestionIdentity.FingerprintOf(Plan(DbChoice), "db-choice");

        Assert.NotNull(fingerprint);
        Assert.Equal(64, fingerprint!.Length);
        Assert.All(fingerprint, ch => Assert.True(
            (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'),
            $"'{ch}' is not lowercase hex; the fingerprint is persisted in the sidecar and compared literally."));
    }

    [Fact]
    public void Fingerprint_OfASpecMatches_FingerprintOfTheSameQuestionInAPlan()
        => Assert.Equal(
            QuestionIdentity.Fingerprint(QuestionSpec.Parse(
                "{ \"id\": \"db-choice\", \"title\": \"Which database?\", \"mode\": \"single\", "
                + "\"target\": \"human\", \"options\": [\"Postgres\", \"SQLite\"] }")),
            QuestionIdentity.FingerprintOf(Plan(DbChoice), "db-choice"));
}
