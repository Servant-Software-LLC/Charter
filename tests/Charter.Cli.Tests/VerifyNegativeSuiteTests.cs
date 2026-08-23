using System.Text.Json.Nodes;
using Charter.Core;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #192, MILESTONE ZERO — the inputs <c>charter verify</c> exits <b>0</b> on that a reader would expect
/// it to catch.
/// </summary>
/// <remarks>
/// <para>
/// This suite was written BEFORE any join was, and it is not a list of bugs. It is the shape of the verb: it
/// decided whether the verb should exist at all, what it may be named, and — above all — what its help and its
/// report are forbidden to claim. Every row below is a deliberate <c>0</c>.
/// </para>
/// <para>
/// <b>The one fact behind all of them.</b> The handoff and its manifest sit in one directory and are writable
/// by the same party. There is no independent witness available to this process, so <c>verify</c> detects
/// <b>inconsistency between two mutually-writable files; it can never detect incorrectness</b>. After this
/// ships, a green <c>verify</c> WILL be quoted in a post-mortem as proof a run was proper. The
/// <see cref="VerifyCommand.NotProvenNote"/> printed on success is the only thing standing between that and a
/// false claim, which is why the last two tests here assert it is present rather than assert a behaviour.
/// </para>
/// <para>
/// Each test additionally asserts that verify DID its work (the joins report <c>MATCH</c>), so none of them can
/// pass because the verb silently did nothing.
/// </para>
/// <para>
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","VerifyNegativeSuite")].
/// </para>
/// </remarks>
[Trait("Category", "VerifyNegativeSuite")]
public class VerifyNegativeSuiteTests : IDisposable
{
    private readonly string _dir = CharterCliRunner.NewTempDirectory();

    public void Dispose() => CharterCliRunner.TryDeleteDirectory(_dir);

    [Fact]
    public void AnEditedANSWER_WithTheHashRecomputed_STILLPASSES()
    {
        // The headline case, and the reason the help text exists. A party with write access to both files can
        // change what the plan DECIDED and re-establish every join in two edits. Nothing here is a bug; the
        // remedy is not another check, it is not overselling the verb.
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);

        VerifyFixture.EditHandoff(_dir, text => text.Replace("Postgres", "Cassandra", StringComparison.Ordinal));
        VerifyFixture.EditManifest(_dir, manifest =>
            manifest["handoffSha256"] = PlanHash.Sha256Hex(VerifyFixture.ReadHandoff(_dir)));

        var (exit, stdout, _) = VerifyFixture.Verify(_dir);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("MISMATCH", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEditedPLANHASH_OnBothSides_STILLPASSES()
    {
        // verify never opens the .charter.md -- it joins the manifest against the handoff's IN-BAND stamp. So a
        // party who rewrites the plan and updates both recorded hashes leaves a pair that agrees perfectly
        // about a plan that no longer exists in that form.
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);

        const string forged = "0000000000000000000000000000000000000000000000000000000000000000";
        VerifyFixture.EditHandoff(_dir, text =>
            text.Replace(
                "<!-- " + HandoffMarkdown.StampPrefix + VerifyFixture.PlanSha256(_dir) + " -->",
                "<!-- " + HandoffMarkdown.StampPrefix + forged + " -->",
                StringComparison.Ordinal));
        VerifyFixture.EditManifest(_dir, manifest =>
        {
            manifest["planSha256"] = forged;
            manifest["handoffSha256"] = PlanHash.Sha256Hex(VerifyFixture.ReadHandoff(_dir));
        });

        var (exit, stdout, _) = VerifyFixture.Verify(_dir);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("MISMATCH", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AnINVENTEDAnswerVALUE_InTheManifest_STILLPASSES()
    {
        // Answer VALUES are deliberately not compared: doing so means prose-parsing arbitrary user text, and a
        // wrong parse would fail honest runs. So the manifest may claim any value it likes as long as the id
        // and the answered flag line up. The report says "values NOT checked" for exactly this reason.
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);

        VerifyFixture.EditManifest(_dir, manifest =>
            manifest["questions"]![0]!["answer"] = new JsonArray("Cassandra", "and anything else"));

        var (exit, stdout, _) = VerifyFixture.Verify(_dir);

        Assert.Equal(0, exit);
        Assert.Contains("questions", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("MISMATCH", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void APlanNOHUMANEVERREVIEWED_STILLPASSES()
    {
        // `charter handoff` does not read the review log at all, so nothing in either artifact carries evidence
        // that a person ever saw the plan. An inline `answer` an agent wrote itself is indistinguishable from
        // one a reviewer settled -- that is the withdrawn reading of `answerSource`, one artifact down.
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);

        Assert.False(
            Directory.Exists(Path.Combine(_dir, "plan.charter.md.review")),
            "the fixture must have no review log, so the claim under test is real.");

        var (exit, stdout, _) = VerifyFixture.Verify(_dir);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("MISMATCH", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void WhetherTheGATEFLAGWasPassedOrHONOURED_ChangesNothingHere()
    {
        // `gate.flagPassed` records the ARGV, not obedience -- which is why the field is not called `enforced`.
        // Both invocations below verify identically, so a green verify says nothing at all about whether the
        // caller ran the gate or whether it honoured the code the gate returned.
        var withFlag = CharterCliRunner.NewTempDirectory();
        try
        {
            VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);
            VerifyFixture.Build(withFlag, VerifyFixture.AnsweredPlan, "--fail-if-needs-human");

            var bare = VerifyFixture.Verify(_dir);
            var gated = VerifyFixture.Verify(withFlag);

            Assert.Equal(0, bare.ExitCode);
            Assert.Equal(0, gated.ExitCode);
            Assert.DoesNotContain("MISMATCH", bare.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("MISMATCH", gated.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(withFlag);
        }
    }

    [Fact]
    public void ASUCCESSFULRun_StillPrintsWhatItCannotProve()
    {
        // The load-bearing one. A verify that disclaims only when it FAILS is a verify whose green output gets
        // pasted into a post-mortem with nothing attached. Asserted on SINGLE TOKENS -- System.CommandLine
        // wraps help to the console width, and the same discipline is kept here so a wrapped report line
        // cannot make this pass or fail for the wrong reason.
        VerifyFixture.Build(_dir, VerifyFixture.AnsweredPlan);

        var (exit, stdout, _) = VerifyFixture.Verify(_dir);

        Assert.Equal(0, exit);
        Assert.Contains("INCONSISTENCY", stdout, StringComparison.Ordinal);
        Assert.Contains("INCORRECTNESS", stdout, StringComparison.Ordinal);
        Assert.Contains("mutually-writable", stdout, StringComparison.Ordinal);
        Assert.Contains("post-mortem", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHelp_RefusesTheSameClaimsTheReportDoes()
    {
        // One constant behind both, so the sentence a pipeline author reads before wiring the verb in cannot
        // drift from the sentence its output carries.
        var (_, stdout, _) = CharterCliRunner.Run("verify", "--help");

        Assert.Contains("INCONSISTENCY", stdout, StringComparison.Ordinal);
        Assert.Contains("INCORRECTNESS", stdout, StringComparison.Ordinal);
        Assert.Contains("READ-ONLY", stdout, StringComparison.Ordinal);

        // ...and it names the OTHER verb, so a reader who wanted the review-side checks is not left to guess.
        Assert.Contains("review", stdout, StringComparison.Ordinal);
    }
}
