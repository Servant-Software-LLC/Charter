using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Charter.Core;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// <c>charter handoff --manifest</c> through the REAL binary (Charter #187): the flag's whole contract is which
/// files exist afterwards, what they say about each other, and what <c>$?</c> was — none of which is observable
/// in-proc.
/// </summary>
/// <remarks>
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","HandoffManifest")].
/// </remarks>
[Trait("Category", "HandoffManifest")]
public class HandoffManifestTests : IDisposable
{
    private const string OpenAndAnsweredPlan =
        "---\ncharter-format-version: 1\n---\n\n# Plan\n\n:::question\n"
        + "{\"id\": \"db\", \"title\": \"Which database?\", \"mode\": \"single\", \"target\": \"human\", "
        + "\"options\": [\"Postgres\", \"MySQL\"], \"answer\": [\"Postgres\"], \"recommended\": \"Postgres\"}\n:::\n\n"
        + ":::question\n"
        + "{\"id\": \"cache\", \"title\": \"Which cache?\", \"mode\": \"single\", \"target\": \"human\", "
        + "\"options\": [\"Redis\", \"in-memory\"], \"recommended\": \"Redis\"}\n:::\n";

    private const string CleanPlan = "---\ncharter-format-version: 1\n---\n\n# Plan\n\nJust prose.\n";

    private readonly string _dir = CharterCliRunner.NewTempDirectory();

    public void Dispose() => CharterCliRunner.TryDeleteDirectory(_dir);

    // ---- the file, at the derived name ----------------------------------------------------------------------

    [Fact]
    public void WithTheFlag_TheManifestLandsAtTheNameDerivedFromOut()
    {
        // Boolean, not a path: `charter headless` exists partly to give a harness "a path convention it can
        // compute from the plan path alone", and a --manifest taking a path would be one more thing to tell it.
        var (exit, stdout, _) = Run(CleanPlan, "--manifest");

        Assert.Equal(0, exit);
        Assert.True(File.Exists(ManifestPath()), "-o plan.md must derive plan.manifest.json.");
        Assert.Contains("Manifest", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTheFlag_NoManifestIsWritten()
    {
        var (exit, _, _) = Run(CleanPlan);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(ManifestPath()), "--manifest is opt-in; a bare handoff writes no side file.");
    }

    [Fact]
    public void TheGateFlag_DoesNotImplyTheManifest()
    {
        // A gate flag that wrote an unbidden file beside the caller's plan would break solo primacy's "no trace
        // where nothing was said" -- the same rule that keeps `charter review` from creating .review/.
        var (exit, _, _) = Run(OpenAndAnsweredPlan, "--fail-if-needs-human");

        Assert.Equal(2, exit);
        Assert.False(File.Exists(ManifestPath()));
    }

    [Fact]
    public void TheManifest_DoesNotImplyTheGateFlag()
    {
        // Asking for a FILE must not change an exit code. The verdict is still computed and still recorded --
        // it just goes to the manifest instead of to $?.
        var (exit, _, stderr) = Run(OpenAndAnsweredPlan, "--manifest");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("need a human", stderr, StringComparison.Ordinal);

        var gate = Manifest().RootElement.GetProperty("gate");
        Assert.False(gate.GetProperty("flagPassed").GetBoolean());
        Assert.True(gate.GetProperty("needsHuman").GetBoolean());
        Assert.Equal(0, gate.GetProperty("exitCode").GetInt32());
        Assert.NotEmpty(gate.GetProperty("blockers").EnumerateArray());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public void TheRecordedExitCode_IsTheProcessesOWNExitCode(bool withGateFlag, int expected)
    {
        // The strongest available binding for a constant Charter.Core cannot reference: rather than sharing a
        // literal with HeadlessExitCodes, assert that the number in the file IS the number the process
        // returned. That also pins the discipline the forensic record keeps -- the file and $? can never
        // disagree.
        var args = withGateFlag ? new[] { "--manifest", "--fail-if-needs-human" } : new[] { "--manifest" };
        var (exit, _, _) = Run(OpenAndAnsweredPlan, args);

        Assert.Equal(expected, exit);
        Assert.Equal(exit, Manifest().RootElement.GetProperty("gate").GetProperty("exitCode").GetInt32());
    }

    // ---- the derived name is refused when it would destroy something ----------------------------------------

    [Theory]
    [InlineData("plan.md")]
    [InlineData("plan.manifest.json")]      // the shape that looks like it must self-collide, and does not
    [InlineData("plan")]                    // no extension at all
    [InlineData("plan.markdown")]
    public void TheDerivedNameIsNeverTheHandoffItself_ForAnyOutShape(string outName)
    {
        // The invariant behind the guard, asserted from OUTSIDE rather than by reaching a branch. It is worth
        // an assertion because it is not obvious: `-o plan.manifest.json` looks like it must derive onto
        // itself, and it does not -- the stem is `plan.manifest`, so the manifest lands at
        // plan.manifest.manifest.json. Ugly, and harmless. Both files exist and neither is the other.
        //
        // If a future change to the derivation DID make the two collide, this goes red -- either because the
        // run is refused (exit 1) or because one file clobbered the other.
        var planPath = Path.Combine(_dir, "plan.charter.md");
        File.WriteAllText(planPath, OpenAndAnsweredPlan);
        var outPath = Path.Combine(_dir, outName);

        var (exit, _, _) = CharterCliRunner.Run("handoff", planPath, "-o", outPath, "--manifest");

        Assert.Equal(0, exit);
        Assert.Contains("Open question (unresolved)", File.ReadAllText(outPath), StringComparison.Ordinal);

        var derived = Directory.GetFiles(_dir, "*.manifest.json")
            .Single(path => !string.Equals(path, outPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, JsonDocument.Parse(File.ReadAllText(derived)).RootElement.GetProperty("schema").GetInt32());
    }

    [Fact]
    public void ADerivedNameCollidingWithThePlan_IsRefused_AndThePlanIsUntouched()
    {
        // A plan named `x.manifest.json` beside `-o x.md` derives straight onto the SOURCE. Charter never
        // overwrites its own input.
        var planPath = Path.Combine(_dir, "x.manifest.json");
        File.WriteAllText(planPath, CleanPlan);

        var (exit, _, stderr) = CharterCliRunner.Run(
            "handoff", planPath, "-o", Path.Combine(_dir, "x.md"), "--manifest");

        Assert.Equal(1, exit);
        Assert.Equal(CleanPlan, File.ReadAllText(planPath));
        Assert.False(File.Exists(Path.Combine(_dir, "x.md")));
        Assert.Contains("overwrite the plan itself", stderr, StringComparison.Ordinal);
    }

    // ---- write order ----------------------------------------------------------------------------------------

    [Fact]
    public void TheHandoffIsWrittenFIRST_SoAFailedManifestLeavesAnHonestDegradedState()
    {
        // Proved by making the manifest write fail on its own: a DIRECTORY at the derived name. The exit is 1
        // and there is no manifest -- but plan.md is complete and correct, which is the point. The other order
        // would leave a manifest describing a file that does not exist, and that is a LIE rather than a gap.
        Directory.CreateDirectory(ManifestPath());

        var (exit, stdout, _) = Run(CleanPlan, "--manifest");

        Assert.Equal(1, exit);
        Assert.True(File.Exists(Out()), "the handoff must already be on disk when the manifest write fails.");
        Assert.Contains("Handed off", stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(ManifestPath()));
    }

    [Fact]
    public void NoTemporaryFileSurvivesAFailedWrite()
    {
        // The atomic write is temp-file-then-rename, and a crashed rename must not leave litter beside the
        // caller's output.
        Directory.CreateDirectory(ManifestPath());
        Run(CleanPlan, "--manifest");

        Assert.Empty(Directory.GetFiles(_dir, "*.charter-tmp-*"));
    }

    // ---- the stale-manifest hazard the second stamp exists for -----------------------------------------------

    [Fact]
    public void AStaleManifest_IsDetectableFromTheTwoArtifactsALONE()
    {
        // THE reproduction, end to end. Run once with answers + --manifest (exit 0). Re-run as a plain handoff:
        // the write is unconditional so plan.md becomes the all-questions-open flatten, no manifest is written
        // because it is opt-in, and the OLD manifest survives -- with planSha256, the in-band plan stamp and
        // charterVersion ALL matching. Every documented join is green while the manifest certifies decisions
        // that are not in the file beside it.
        var answersPath = Path.Combine(_dir, "answers.json");
        File.WriteAllText(answersPath, "{\"cache\": [\"Redis\"]}");

        var (first, _, _) = Run(
            OpenAndAnsweredPlan, "--answers", answersPath, "--manifest", "--fail-if-needs-human");
        Assert.Equal(0, first);

        var manifest = Manifest().RootElement;
        var answersSha = manifest.GetProperty("answersSha256").GetString();
        Assert.Matches("^[0-9a-f]{64}$", answersSha);

        var (second, _, _) = Run(OpenAndAnsweredPlan);
        Assert.Equal(0, second);

        var stale = File.ReadAllText(Out());

        // Everything a consumer was told to join on still agrees...
        Assert.Contains(
            $"<!-- {HandoffMarkdown.StampPrefix}{manifest.GetProperty("planSha256").GetString()} -->",
            stale,
            StringComparison.Ordinal);

        // ...while the plan.md on disk was resolved from NO answers file at all.
        Assert.Contains("Open question (unresolved)", stale, StringComparison.Ordinal);

        // The one line that exposes it: the file says `none`, the manifest says a hex.
        Assert.Contains(
            $"<!-- {HandoffMarkdown.AnswersStampPrefix}{HandoffMarkdown.NoAnswersFile} -->",
            stale,
            StringComparison.Ordinal);
        Assert.NotEqual(HandoffMarkdown.NoAnswersFile, answersSha);
    }

    [Fact]
    public void TheInBandAnswersStamp_CarriesTheSameHashTheManifestRecords()
    {
        // The two halves of the same fact, from one read of one file. If they could differ, the in-band half
        // would be unable to expose a stale manifest -- which is the only thing it is there for.
        var answersPath = Path.Combine(_dir, "answers.json");
        File.WriteAllText(answersPath, "{\"cache\": [\"Redis\"]}");

        Run(OpenAndAnsweredPlan, "--answers", answersPath, "--manifest");

        var recorded = Manifest().RootElement.GetProperty("answersSha256").GetString();

        Assert.Contains(
            $"<!-- {HandoffMarkdown.AnswersStampPrefix}{recorded} -->",
            File.ReadAllText(Out()),
            StringComparison.Ordinal);
    }

    // ---- the hash recipe ------------------------------------------------------------------------------------

    [Fact]
    public void OnABomLessUtf8AnswersFile_answersSha256_EqualsSha256sum()
    {
        // The happy case, asserted so the DIVERGENCE below reads as a property of the encoding rather than as
        // Charter hashing something arbitrary.
        var answersPath = Path.Combine(_dir, "answers.json");
        File.WriteAllBytes(
            answersPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes("{\"cache\":[\"Redis\"]}"));

        var (exit, _, stderr) = Run(OpenAndAnsweredPlan, "--answers", answersPath, "--manifest");

        Assert.Equal(0, exit);
        Assert.Equal(Sha256OfBytes(answersPath), Manifest().RootElement.GetProperty("answersSha256").GetString());
        Assert.DoesNotContain("byte order mark", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void OnAUtf16AnswersFile_answersSha256_DIVERGESFromSha256sum_AndCharterSaysSo()
    {
        // The documented divergence, and the reason it is documented: File.ReadAllText decodes UTF-16 per the
        // BOM and PlanHash hashes the UTF-8 RE-ENCODING of that string, so a pipeline generating answers.json
        // from Windows PowerShell 5.1 gets a permanent, unexplainable mismatch against `sha256sum`. The run is
        // honest -- the answers apply correctly -- so this WARNS rather than rejecting.
        var answersPath = Path.Combine(_dir, "answers.json");
        File.WriteAllBytes(
            answersPath,
            new UnicodeEncoding(bigEndian: false, byteOrderMark: true)
                .GetPreamble()
                .Concat(new UnicodeEncoding(false, false).GetBytes("{\"cache\":[\"Redis\"]}"))
                .ToArray());

        var (exit, _, stderr) = Run(OpenAndAnsweredPlan, "--answers", answersPath, "--manifest");

        Assert.Equal(0, exit);
        Assert.Contains("Answered: Redis", File.ReadAllText(Out()), StringComparison.Ordinal);

        Assert.NotEqual(Sha256OfBytes(answersPath), Manifest().RootElement.GetProperty("answersSha256").GetString());
        Assert.Contains("UTF-16LE byte order mark", stderr, StringComparison.Ordinal);
        Assert.Contains("sha256sum", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePlanAndHandoffHashes_MatchSha256sum_BecauseCharterWritesBomLessUtf8()
    {
        Run(CleanPlan, "--manifest");

        var manifest = Manifest().RootElement;

        Assert.Equal(Sha256OfBytes(Out()), manifest.GetProperty("handoffSha256").GetString());
        Assert.Equal(
            Sha256OfBytes(Path.Combine(_dir, "plan.charter.md")), manifest.GetProperty("planSha256").GetString());
    }

    // ---- absence, and determinism ---------------------------------------------------------------------------

    [Fact]
    public void NoAnswersFile_RecordsNullTwice_WhileAnEmptyOneRecordsARealHash()
    {
        Run(CleanPlan, "--manifest");
        var none = Manifest().RootElement;
        Assert.Equal(JsonValueKind.Null, none.GetProperty("answers").ValueKind);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("answersSha256").ValueKind);

        var answersPath = Path.Combine(_dir, "answers.json");
        File.WriteAllText(answersPath, "{}");
        Run(CleanPlan, "--answers", answersPath, "--manifest");

        var empty = Manifest().RootElement;
        Assert.Equal("answers.json", empty.GetProperty("answers").GetString());
        Assert.Matches("^[0-9a-f]{64}$", empty.GetProperty("answersSha256").GetString());
    }

    [Fact]
    public void TwoRunsOfTheSameInvocation_ProduceAByteIdenticalManifest()
    {
        // No clock, so reproducibility is assertable rather than promised -- a harness diffs two runs.
        Run(OpenAndAnsweredPlan, "--manifest");
        var first = File.ReadAllText(ManifestPath());

        Run(OpenAndAnsweredPlan, "--manifest");

        Assert.Equal(first, File.ReadAllText(ManifestPath()));
    }

    [Fact]
    public void TheManifest_NamesFilesAndNeverPaths()
    {
        // It travels with the worktree a crew collects, exactly like the artifact and the forensic record, so a
        // local absolute path in it would leak the producer's filesystem into a handed-on file.
        var answersPath = Path.Combine(_dir, "answers.json");
        File.WriteAllText(answersPath, "{\"cache\": [\"Redis\"]}");
        Run(OpenAndAnsweredPlan, "--answers", answersPath, "--manifest");

        var json = File.ReadAllText(ManifestPath());

        Assert.DoesNotContain(_dir.Replace("\\", "\\\\", StringComparison.Ordinal), json, StringComparison.Ordinal);
        Assert.DoesNotContain(_dir.Replace("\\", "/", StringComparison.Ordinal), json, StringComparison.Ordinal);

        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("plan.charter.md", root.GetProperty("plan").GetString());
        Assert.Equal("answers.json", root.GetProperty("answers").GetString());
        Assert.Equal("plan.md", root.GetProperty("handoff").GetString());
    }

    [Fact]
    public void ARelativeOutPath_StillRecordsABareHandoffName()
    {
        // `-o ../gr/plan.md` is the shape a real pipeline uses, and it is why the name fields are declared
        // non-contract: what lands in the file is `plan.md`, like almost every other Guardrails handoff.
        var nested = Path.Combine(_dir, "gr");
        var planPath = Path.Combine(_dir, "plan.charter.md");
        File.WriteAllText(planPath, CleanPlan);

        var (exit, _, _) = CharterCliRunner.Run(
            "handoff", planPath, "-o", Path.Combine(nested, "plan.md"), "--manifest");

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(nested, "plan.manifest.json")));
        Assert.Equal(
            "plan.md",
            JsonDocument.Parse(File.ReadAllText(Path.Combine(nested, "plan.manifest.json")))
                .RootElement.GetProperty("handoff").GetString());
    }

    // ---- help ------------------------------------------------------------------------------------------------

    [Fact]
    public void HandoffHelp_DocumentsTheFlag_AndNoLongerClaimsA1MeansNothingWasWritten()
    {
        // The old wording ("1 ... and NOTHING was written") became false under --manifest: the handoff is
        // written FIRST, so a failure at the manifest write leaves a valid plan.md behind a 1.
        //
        // Asserted on SINGLE TOKENS only. System.CommandLine word-wraps help to the console width, so a
        // multi-word phrase can be split across lines on one agent and not another -- and for a DoesNotContain
        // that fails in the DANGEROUS direction, passing because the old claim wrapped rather than because it
        // is gone. `INVOCATION` appears only in the corrected wording.
        var (_, stdout, _) = CharterCliRunner.Run("handoff", "--help");

        Assert.Contains("--manifest", stdout, StringComparison.Ordinal);
        Assert.Contains("plan.manifest.json", stdout, StringComparison.Ordinal);
        Assert.Contains("INVOCATION", stdout, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private string Out() => Path.Combine(_dir, "plan.md");

    private string ManifestPath() => Path.Combine(_dir, "plan.manifest.json");

    private JsonDocument Manifest() => JsonDocument.Parse(File.ReadAllText(ManifestPath()));

    private static string Sha256OfBytes(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    // Named distinctly (not a Run overload) for the params-vs-overload trap CharterCliRunner warns about: a
    // flag bound to a plan-name parameter would silently assert against a run with no flag at all.
    private (int ExitCode, string StdOut, string StdErr) Run(string plan, params string[] extraArgs)
    {
        var planPath = Path.Combine(_dir, "plan.charter.md");
        File.WriteAllText(planPath, plan);

        var args = new List<string> { "handoff", planPath, "-o", Out() };
        args.AddRange(extraArgs);
        return CharterCliRunner.Run(args.ToArray());
    }
}
