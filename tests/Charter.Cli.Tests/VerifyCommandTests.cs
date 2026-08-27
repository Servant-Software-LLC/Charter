using System.Text;
using System.Text.Json.Nodes;
using Charter.Core;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// <c>charter verify</c> through the REAL binary (Charter #192): what it joins, what it refuses, and — the half
/// that took the most care — how it names a benign cause without ever excusing one.
/// </summary>
/// <remarks>
/// <para>
/// The companion suite <c>VerifyNegativeSuiteTests</c> holds what verify deliberately exits <b>0</b> on, and it
/// was written first. This one holds what it catches. Read them together: either alone gives a misleading
/// picture of the verb.
/// </para>
/// <para>
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","VerifyCommand")].
/// </para>
/// </remarks>
[Trait("Category", "VerifyCommand")]
public class VerifyCommandTests : IDisposable
{
    private readonly string _dir = CharterCliRunner.NewTempDirectory();

    public void Dispose() => CharterCliRunner.TryDeleteDirectory(_dir);

    // ---- the happy path, and the proof it is not vacuous -----------------------------------------------------

    [Fact]
    public void AnUntouchedPair_Verifies_AndEveryJoinIsReportedAsRun()
    {
        // The positive control the rest of the suite rests on: if this did not report four MATCHes, every
        // "it caught the tamper" test below could be passing because the verb fails on everything.
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);

        var (exit, stdout, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(0, exit);
        Assert.Equal(4, Occurrences(stdout, "MATCH"));
        Assert.DoesNotContain("MISMATCH", stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.Trim());
    }

    [Fact]
    public void Verify_WritesNOTHING()
    {
        // Read-only is a contract, not a description. A verifier that repaired, cached or normalised anything
        // would be editing the evidence it was asked to weigh.
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);

        var before = Snapshot();
        VerifyFixture.Verify(_dir);

        Assert.Equal(before, Snapshot());
    }

    // ---- the joins -------------------------------------------------------------------------------------------

    [Fact]
    public void AStaleManifestBesideARerunHandoff_IsCaughtByTheANSWERSStamp()
    {
        // The hazard that forced the second stamp (plan-04 10.5), reproduced end to end: run once with
        // --answers --manifest, then re-run as a PLAIN handoff. The write is unconditional so plan.md becomes
        // the all-questions-open flatten; no manifest is written because it is opt-in; the OLD manifest
        // survives -- and planSha256, the in-band plan stamp and charterVersion ALL match. Only the answers
        // stamp separates them, which is the whole reason it exists.
        var answers = Path.Combine(_dir, "answers.json");
        File.WriteAllText(answers, "{\"cache\": [\"Redis\"]}");

        File.WriteAllText(VerifyFixture.PlanPath(_dir), VerifyFixture.OpenQuestionPlan);
        CharterCliRunner.Run(
            "handoff", VerifyFixture.PlanPath(_dir), "-o", VerifyFixture.HandoffPath(_dir),
            "--answers", answers, "--manifest");

        // The plain re-run: same plan, no --answers, no --manifest.
        CharterCliRunner.Run("handoff", VerifyFixture.PlanPath(_dir), "-o", VerifyFixture.HandoffPath(_dir));

        var (exit, stdout, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(2, exit);

        // The plan stamp still agrees -- that is the point of the reproduction.
        Assert.Contains("plan-sha256     MATCH", stdout, StringComparison.Ordinal);
        Assert.Contains("answers-sha256  MISMATCH", stdout, StringComparison.Ordinal);
        Assert.Contains("resolution", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestFromADifferentRun_IsCaughtByThePLANStamp()
    {
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);
        VerifyFixture.EditManifest(_dir, manifest =>
            manifest["planSha256"] = new string('a', 64));

        var (exit, stdout, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(2, exit);
        Assert.Contains("plan-sha256     MISMATCH", stdout, StringComparison.Ordinal);
        Assert.Contains("plan-sha256:", stderr, StringComparison.Ordinal);
    }

    // ---- the payload cross-check -----------------------------------------------------------------------------

    [Fact]
    public void AManifestSayingUNANSWERED_BesideAHandoffSayingAnswered_Fails()
    {
        // #187's own opening reproduction, one artifact later. Without this check the pair passes every hash
        // join while the manifest vouches for a resolution the document does not carry -- which is precisely
        // the disagreement the manifest was built to make impossible.
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);
        VerifyFixture.EditManifest(_dir, manifest => manifest["questions"]![0]!["answered"] = false);

        var (exit, stdout, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(2, exit);
        Assert.Contains("questions       MISMATCH", stdout, StringComparison.Ordinal);
        Assert.Contains("`db`", stderr, StringComparison.Ordinal);
        Assert.Contains("Answered", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuestionTheManifestDoesNotRECORD_Fails()
    {
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);
        VerifyFixture.EditManifest(_dir, manifest => manifest["questions"] = new JsonArray());

        var (exit, stdout, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(2, exit);
        Assert.Contains("questions       MISMATCH", stdout, StringComparison.Ordinal);
        Assert.Contains("the manifest does not record it", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuestionTheHANDOFFDoesNotCarry_Fails()
    {
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);
        VerifyFixture.EditManifest(_dir, manifest =>
            ((JsonArray)manifest["questions"]!).Add(new JsonObject
            {
                ["id"] = "ghost",
                ["answered"] = true,
            }));

        var (exit, _, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(2, exit);
        Assert.Contains("`ghost`", stderr, StringComparison.Ordinal);
        Assert.Contains("not in the document beside it", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AMetadataLineInPROSE_IsNotedButNeverFails()
    {
        // Ordinary prose can spell Charter's own literals -- a plan DOCUMENTING Charter does exactly that (this
        // repo's own would), and :::custom-html passes anything through verbatim. Three rules meet here:
        //
        //   1. The marker must open the LINE, so a mid-sentence mention is invisible to the scan entirely.
        //   2. An Answered/Open/Delegated lead must sit above it, so a lone metadata line is not counted as a
        //      question -- the phantom never enters the id set.
        //   3. It is a NOTE, not a finding, so it cannot change the exit code. Failing here would make an
        //      honest plan unverifiable forever, which is the false-alarm class this verb exists to avoid.
        //
        // Excluding it is safe in the other direction too: real tampering that strips a question's lead line
        // ALSO removes that id from the handoff's set, which IS a finding.
        const string documentingPlan =
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\nCharter emits this under every question:\n\n"
            + HandoffMarkdown.QuestionIdMarker + "example`; mode: `single`; target: `human`_\n";

        VerifyFixture.Build(_dir, documentingPlan);

        var (exit, stdout, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(0, exit);
        Assert.Contains("note:", stderr, StringComparison.Ordinal);
        Assert.Contains("informational", stderr, StringComparison.Ordinal);

        // ...and the phantom did NOT enter the id set, so it is not reported as a question the manifest missed.
        Assert.DoesNotContain("`example`", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("MISMATCH", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AMidSentenceMentionOfTheMarker_IsNotEvenNoticed()
    {
        // Rule 1 above, on its own: the scan anchors the marker to the START of a line, so prose that merely
        // TALKS about the metadata line produces no note at all. Without this the common case -- documentation
        // prose -- would emit noise on every run and the note would stop being read.
        const string documentingPlan =
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\nCharter emits a line like "
            + HandoffMarkdown.QuestionIdMarker + "example`; mode: `single`_ under every question.\n";

        VerifyFixture.Build(_dir, documentingPlan);

        var (exit, _, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr.Trim());
    }

    [Fact]
    public void AMultiLineTITLEOrANSWER_StillVerifies_BecauseTheLeadIsSoughtInTheWholeBlock()
    {
        // The same family as the lone-CR trap, one field over. A question's TITLE and its ANSWER VALUE both
        // come from JSON and neither is collapsed by the emitter, so an honest flatten can read
        //
        //   **Q: pick a
        //   store** - Answered: alpha
        //   beta
        //   _Question - id: `db`; ...
        //
        // Looking only ONE line above the metadata line finds `beta`, drops the id from the set, and reports an
        // untouched pair as a mismatch. Searching back to the block's first line finds the real lead.
        // The TITLE was multi-line here too until Charter #212 forbade a newline in it — a title has no
        // reviewer affordance that produces one, and since #219 it rides the single-line marker the Guardrails
        // gate matches. The ANSWER keeps its newline (#202's textarea carve-out), so the flatten still spreads
        // one question over several lines and the behaviour under test is reached exactly as before.
        const string multiLinePlan =
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
            + "{\"id\": \"db\", \"title\": \"pick a store\", \"mode\": \"free-text\", "
            + "\"target\": \"human\", \"answer\": [\"alpha\\nbeta\"]}\n:::\n";

        VerifyFixture.Build(_dir, multiLinePlan);

        // The premise: the flatten really does spread one question over several lines.
        Assert.Contains("alpha\nbeta", VerifyFixture.ReadHandoff(_dir), StringComparison.Ordinal);

        var (exit, stdout, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("MISMATCH", stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.Trim());
    }

    // ---- the escalation clause ---------------------------------------------------------------------------------

    [Fact]
    public void AManifestRecordingNeedsHuman_ExitsTwo_EvenThoughEveryJoinHolds()
    {
        // The vacuous-pass fix. A verifier that reads needsHuman: true and exits 0 is lying by omission -- and
        // this is not the producer changing an exit code as a side effect of writing a file (10.0.2), it is a
        // READER re-reporting Charter's own recorded verdict.
        VerifyFixture.Build(_dir, VerifyFixture.OpenQuestionPlan);

        var (exit, stdout, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(2, exit);
        Assert.DoesNotContain("MISMATCH", stdout, StringComparison.Ordinal);
        Assert.Contains("gate.needsHuman true", stdout, StringComparison.Ordinal);
        Assert.Contains("needs a person", stderr, StringComparison.Ordinal);
    }

    // ---- the two false alarms ----------------------------------------------------------------------------------

    [Fact]
    public void ACrlfRewrite_FAILS_AndIsNamedAsALineEndingRewrite()
    {
        // It FAILS. PlanHash defines the question this field answers as "are these two files byte-for-byte the
        // same revision?", and a verifier must not quietly answer a weaker one. The recompute is a labelled
        // diagnostic, never a redefinition of the field.
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);
        VerifyFixture.EditHandoff(_dir, text => text.Replace("\n", "\r\n", StringComparison.Ordinal));

        var (exit, stdout, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(2, exit);
        Assert.Contains("handoff-sha256  MISMATCH", stdout, StringComparison.Ordinal);
        Assert.Contains("LINE-ENDING REWRITE", stderr, StringComparison.Ordinal);
        Assert.Contains("autocrlf", stderr, StringComparison.Ordinal);

        // The claim is BOUNDED: the decode already strips a BOM and honours UTF-16, so the same test covers a
        // re-encoding -- and nothing beyond that is asserted.
        Assert.Contains("RE-ENCODING", stderr, StringComparison.Ordinal);

        // ONE problem, not two. The diagnosis rides the finding as a continuation line rather than as a second
        // entry, so the closing count cannot report the EXPLANATION as a second thing wrong. Caught by reading
        // the verb's real output, which said "2 finding(s)" over one mismatch and one cause.
        Assert.Contains("1 finding(s)", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddedTrailingNewline_GetsItsOWNDiagnosis_NotTheAlarmingOne()
    {
        // `Emit` writes no final newline while HandoffManifest.ToJson() appends one, so any editor set to
        // "insert final newline" produces exactly this -- and it is MORE LIKELY than a wholesale CRLF rewrite.
        // Without its own branch the most common benign mutation would get the most alarming message.
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);
        VerifyFixture.EditHandoff(_dir, text => text + "\n");

        var (exit, _, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(2, exit);
        Assert.Contains("TRAILING NEWLINE", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("LINE-ENDING REWRITE", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ALONECRWhereANewlineWas_DeclinesToDiagnose_RatherThanCallItALineEndingRewrite()
    {
        // ReviewBaseStatus's hash collapses a lone CR into a newline, and copying that form here would bless a
        // CONTENT change as a harmless rewrite. This is the shape that proves it: replace one newline with a
        // lone CR, and a `\r` -> `\n` collapse reproduces the manifest's hash EXACTLY -- so a verifier using
        // that form would announce "LINE-ENDING REWRITE" with total confidence over a file whose lone CR it
        // cannot account for. Declining is the only honest answer available.
        //
        // This used to point at a companion test proving a lone CR could be PLAN CONTENT, via a question
        // answer that reached the flatten JSON-escaped. Charter #212 closed that route, and `Emit`
        // normalises every CR in prose before parsing -- so no Charter-produced flatten can carry one.
        // That does NOT weaken the rule, it sharpens it: verify reads a file it did not necessarily
        // produce, so "Charter would not emit this" is not evidence about how the bytes got there.
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);
        // The newline chosen is the one before `Prose.`, which is preceded by another newline -- so the result
        // is a LONE CR (`\n\rProse.`), not a CRLF. Collapsing that CR to a newline reproduces the manifest's
        // hash exactly, which is what makes this the falsifying case rather than an ordinary content edit.
        VerifyFixture.EditHandoff(_dir, text => ReplaceFirst(text, "\nProse.", "\rProse."));

        var (exit, _, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(2, exit);
        Assert.Contains("not determined", stderr, StringComparison.Ordinal);
        Assert.Contains("lone CR", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("LINE-ENDING REWRITE", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ALoneCRInAQuestionANSWER_IsRefusedAtParse_SoItCannotReachTheFlattenAtAll()
    {
        // The other half of the test above, and the reason its channel moved (Charter #212). The route that
        // produced the original false alarm is closed at the format, not merely handled downstream.
        const string crAnswerPlan =
            "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
            + "{\"id\": \"db\", \"title\": \"Which?\", \"mode\": \"free-text\", \"target\": \"human\", "
            + "\"answer\": [\"alpha\\rbeta\"]}\n:::\n";

        VerifyFixture.Build(_dir, crAnswerPlan);

        var handoff = VerifyFixture.ReadHandoff(_dir);
        Assert.Contains("Malformed question", handoff, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', handoff);
    }

    [Fact]
    public void AByteOrderMarkOnTheHandoff_IsEVIDENCE_NotAnExcuse()
    {
        // HandoffAnswers.EncodingWarning is deliberately NOT reused. For an --answers file a HUMAN chose the
        // encoding and the remedy really is "write it as BOM-less UTF-8". Charter writes THIS file itself, so a
        // mark means somebody rewrote it -- and telling the user to rewrite the artifact under audit would be
        // exactly the wrong remedy.
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);

        var text = VerifyFixture.ReadHandoff(_dir);
        VerifyFixture.WriteHandoffBytes(
            _dir, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
                .Concat(Encoding.UTF8.GetBytes(text)).ToArray());

        var (exit, stdout, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(2, exit);

        // The mark does not change the DECODED text, so the hash still matches -- the rewrite is the finding.
        Assert.Contains("handoff-sha256  MATCH", stdout, StringComparison.Ordinal);
        Assert.Contains("UTF-8 byte order mark", stderr, StringComparison.Ordinal);
        Assert.Contains("rewritten", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Write the file as BOM-less UTF-8", stderr, StringComparison.Ordinal);
    }

    // ---- "could not answer" is a 1, never a verdict ------------------------------------------------------------

    [Fact]
    public void AMissingManifest_IsAOne_AndSaysItIsNotEvidenceOfTampering()
    {
        // The known limit: the artifacts are DESIGNED to travel (bare names, no local paths), but discovery is
        // co-location plus co-naming. A handoff copied without its manifest is unverifiable at its new home --
        // honest, and it must not read as alarming.
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);
        File.Delete(VerifyFixture.ManifestPath(_dir));

        var (exit, _, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(1, exit);
        Assert.Contains("no manifest beside", stderr, StringComparison.Ordinal);
        Assert.Contains("not evidence of tampering", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnparseableManifest_IsAOne()
    {
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);
        File.WriteAllText(VerifyFixture.ManifestPath(_dir), "{ not json");

        var (exit, _, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(1, exit);
        Assert.Contains("Nothing was verified", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestFromANEWERCharter_IsAOne_AndSaysSo()
    {
        // "Newer than I am" and "broken" are different answers, and a verifier owes the user the right one. A
        // 1 here promises nothing, which is exactly right: this build cannot know whether a schema-2 manifest's
        // fields still mean what it would join on.
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);
        VerifyFixture.EditManifest(_dir, manifest => manifest["schema"] = HandoffManifest.Schema + 1);

        var (exit, _, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(1, exit);
        Assert.Contains("Upgrade charter", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AHandoffWithNoStamp_IsAOne()
    {
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);
        File.WriteAllText(VerifyFixture.HandoffPath(_dir), "# Not a flattened plan\n");

        var (exit, _, stderr) = VerifyFixture.Verify(_dir);

        Assert.Equal(1, exit);
        Assert.Contains(HandoffMarkdown.StampPrefix, stderr, StringComparison.Ordinal);
        Assert.Contains("Nothing was verified", stderr, StringComparison.Ordinal);
    }

    // ---- the wrong-file guards ---------------------------------------------------------------------------------

    [Fact]
    public void APlanArgument_IsRefusedLOUDLY_AndNamesTheOtherVerb()
    {
        // The whole reason `charter verify` was an acceptable name for a second meaning: reaching for the wrong
        // verb here is LOUD. `headless` vs `handoff` is the counter-example -- that one fails silently at exit
        // 0 with no plan.md, which is why neither was renamed.
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);

        var (exit, _, stderr) = CharterCliRunner.Run("verify", VerifyFixture.PlanPath(_dir));

        Assert.Equal(1, exit);
        Assert.Contains("is a Charter PLAN", stderr, StringComparison.Ordinal);
        Assert.Contains("charter review", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestArgument_IsRefusedLoudly()
    {
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);

        var (exit, _, stderr) = CharterCliRunner.Run("verify", VerifyFixture.ManifestPath(_dir));

        Assert.Equal(1, exit);
        Assert.Contains("is the MANIFEST", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerbIsDiscoverable_FromTheBannerAndFromAnUnknownVerb()
    {
        // CharterCommands.Commands is the single catalog both help surfaces are generated from -- the fix for
        // #138, where an agent enumerating Charter from --help concluded `reply` did not exist.
        var (_, banner, _) = CharterCliRunner.Run("--help");
        var (_, _, unknown) = CharterCliRunner.Run("nosuchverb");

        Assert.Contains("verify", banner, StringComparison.Ordinal);
        Assert.Contains("verify", unknown, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private List<string> Snapshot()
        => Directory.GetFileSystemEntries(_dir, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => $"{path}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path):O}")
            .ToList();

    private static string ReplaceFirst(string text, string find, string replacement)
    {
        var at = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(at >= 0, $"the fixture must contain {find.Replace("\n", "\\n", StringComparison.Ordinal)}.");
        return text[..at] + replacement + text[(at + find.Length)..];
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
