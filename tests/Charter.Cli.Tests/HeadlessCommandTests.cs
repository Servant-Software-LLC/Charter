using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// The unattended (firstmate-crewmate) path, Charter #7, exercised through the REAL binary.
/// </summary>
/// <remarks>
/// <para>
/// <c>charter export</c> already rendered a self-contained artifact and exited without a server, so these
/// tests deliberately do NOT re-prove rendering. What they pin is the surface <c>export</c> did not have:
/// a path convention a harness can compute without being told, a forensic record on disk, and a SCRIPTABLE
/// exit code that says whether a human decision was left outstanding.
/// </para>
/// <para>
/// The artifact-equivalence test is the anti-redundancy guard: <c>headless</c> must produce the SAME bytes
/// <c>export</c> does, because it calls the same exporter. The day it forks, that test goes red.
/// </para>
/// </remarks>
[Trait("Category", "Cli")]
public class HeadlessCommandTests
{
    private const string PlanWithOpenHumanQuestion =
        "---\ncharter-format-version: 1\n---\n\n"
        + "# Storage plan\n\n"
        + "Prose the renderer will render.\n\n"
        + ":::question\n"
        + "{\"id\": \"store\", \"title\": \"Which store?\", \"mode\": \"single\", "
        + "\"target\": \"human\", \"options\": [\"Postgres\", \"SQLite\"]}\n"
        + ":::\n";

    private const string PlanWithNoOutstandingDecision =
        "---\ncharter-format-version: 1\n---\n\n"
        + "# Storage plan\n\n"
        + "Prose the renderer will render.\n\n"
        + ":::question\n"
        + "{\"id\": \"store\", \"title\": \"Which store?\", \"mode\": \"single\", "
        + "\"target\": \"human\", \"options\": [\"Postgres\", \"SQLite\"], \"answer\": [\"Postgres\"]}\n"
        + ":::\n";

    // ---------------------------------------------------------------------------------------------------
    // The predictable path convention — the thing a collecting harness needs.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// With no output option at all, both files land beside the plan at names derived purely from the plan's
    /// own filename. That is the whole "discoverable path" requirement: a harness that knows the plan path
    /// knows where the outputs are, with nothing passed and nothing configured.
    /// </summary>
    [Fact]
    public void Headless_WithNoOutputOption_WritesBothFilesBesideThePlan_AtDerivedNames()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string plan = Path.Combine(workDir, "storage.charter.md");
            File.WriteAllText(plan, PlanWithNoOutstandingDecision);

            var result = CharterCliRunner.Run("headless", plan);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(workDir, "storage.charter.html")), "artifact not at the derived path");
            Assert.True(File.Exists(Path.Combine(workDir, "storage.charter.headless.json")), "record not at the derived path");
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// <c>--out-dir</c> relocates the pair WITHOUT renaming them (and creates the directory), so a crew can
    /// point every plan at one collection directory and still compute each artifact's name from its plan.
    /// </summary>
    [Fact]
    public void Headless_WithOutDir_WritesTheSameNamesIntoThatDirectory_CreatingIt()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string plan = Path.Combine(workDir, "storage.charter.md");
            File.WriteAllText(plan, PlanWithNoOutstandingDecision);
            string collected = Path.Combine(workDir, "data", "charter");

            var result = CharterCliRunner.Run("headless", plan, "--out-dir", collected);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(collected, "storage.charter.html")));
            Assert.True(File.Exists(Path.Combine(collected, "storage.charter.headless.json")));

            // Nothing was left beside the plan when an explicit collection directory was named.
            Assert.False(File.Exists(Path.Combine(workDir, "storage.charter.html")));
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Exit codes — scriptable, and never inferred from an empty collection.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>An open question addressed to a human exits 2: rendered fine, but a decision is outstanding.</summary>
    [Fact]
    public void Headless_WithAnOpenHumanQuestion_Exits2_AndStillWritesEverything()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string plan = Path.Combine(workDir, "storage.charter.md");
            File.WriteAllText(plan, PlanWithOpenHumanQuestion);

            var result = CharterCliRunner.Run("headless", plan);

            // 2 is NOT a failure: the forensic guarantee holds regardless of the escalation.
            Assert.Equal(2, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(workDir, "storage.charter.html")));
            Assert.True(File.Exists(Path.Combine(workDir, "storage.charter.headless.json")));

            // The escalation is explained on stderr; stdout keeps its plain written-file lines.
            Assert.Contains("charter headless:", result.StdErr);
            Assert.Contains("store", result.StdErr);
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    /// <summary>Nothing outstanding exits 0 and says nothing on stderr.</summary>
    [Fact]
    public void Headless_WithNothingOutstanding_Exits0_AndWarnsAboutNothing()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string plan = Path.Combine(workDir, "storage.charter.md");
            File.WriteAllText(plan, PlanWithNoOutstandingDecision);

            var result = CharterCliRunner.Run("headless", plan);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.StdErr.Trim());
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// The record's <c>needsHuman</c> and the process exit code are the SAME fact — a harness that reads the
    /// file and one that branches on <c>$?</c> must never reach opposite conclusions.
    /// </summary>
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    public void Headless_RecordNeedsHuman_AlwaysAgreesWithTheExitCode(bool outstanding, int expectedExitCode)
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string plan = Path.Combine(workDir, "storage.charter.md");
            File.WriteAllText(plan, outstanding ? PlanWithOpenHumanQuestion : PlanWithNoOutstandingDecision);

            var result = CharterCliRunner.Run("headless", plan);

            var record = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(workDir, "storage.charter.headless.json"))).RootElement;

            Assert.Equal(expectedExitCode, result.ExitCode);
            Assert.Equal(outstanding, record.GetProperty("needsHuman").GetBoolean());
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    /// <summary>A missing plan is the ordinary verb error (1), distinct from the escalation code (2).</summary>
    [Fact]
    public void Headless_MissingInput_Exits1_WithCleanError()
    {
        string missing = Path.Combine(Path.GetTempPath(), "charter-missing-" + Guid.NewGuid().ToString("N") + ".charter.md");

        var result = CharterCliRunner.Run("headless", missing);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("charter headless:", result.StdErr);

        string combined = result.StdOut + "\n" + result.StdErr;
        Assert.DoesNotContain("Unhandled exception", combined);
        Assert.DoesNotContain("   at ", combined);
    }

    /// <summary>
    /// Charter never overwrites its own input. A plan whose derived artifact name would collide with the plan
    /// itself is refused rather than silently rendered over the source.
    /// </summary>
    [Fact]
    public void Headless_WhenTheDerivedArtifactWouldOverwriteThePlan_Exits1_AndLeavesThePlanIntact()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            // GetFileNameWithoutExtension("plan.html") + ".html" is "plan.html" — the plan's own name.
            string plan = Path.Combine(workDir, "plan.html");
            File.WriteAllText(plan, PlanWithNoOutstandingDecision);

            var result = CharterCliRunner.Run("headless", plan);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("charter headless:", result.StdErr);
            Assert.Equal(PlanWithNoOutstandingDecision, File.ReadAllText(plan));
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // The invariants headless inherits and must not break.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The anti-redundancy guard: the artifact <c>headless</c> writes is byte-identical to the one
    /// <c>export</c> writes, because it IS the exporter. A second renderer would be a fork of the
    /// portable-artifact contract; this test fails the moment one appears.
    /// </summary>
    [Fact]
    public void Headless_Artifact_IsByteIdenticalToTheExportVerbsArtifact()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string plan = Path.Combine(workDir, "storage.charter.md");
            File.WriteAllText(plan, PlanWithOpenHumanQuestion);
            string exported = Path.Combine(workDir, "exported.html");

            Assert.Equal(0, CharterCliRunner.Run("export", plan, "-o", exported).ExitCode);
            CharterCliRunner.Run("headless", plan);

            Assert.Equal(
                File.ReadAllBytes(exported),
                File.ReadAllBytes(Path.Combine(workDir, "storage.charter.html")));
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// Invariant 1 holds on the unattended path: the saved artifact is SDK-free, so it opens standalone. The
    /// annotation SDK is a serve-time injection and must never reach a file on disk.
    /// </summary>
    [Fact]
    public void Headless_Artifact_ContainsNoAnnotationSdk()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string plan = Path.Combine(workDir, "storage.charter.md");
            File.WriteAllText(plan, PlanWithOpenHumanQuestion);

            CharterCliRunner.Run("headless", plan);

            string html = File.ReadAllText(Path.Combine(workDir, "storage.charter.html"));
            Assert.DoesNotContain("data-charter-sdk", html, StringComparison.Ordinal);
            Assert.DoesNotContain("/api/annotations", html, StringComparison.Ordinal);
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// The record carries the plan and artifact by NAME only — never the local path they were produced at, so
    /// it is as safe to collect and pass on as the artifact beside it.
    /// </summary>
    [Fact]
    public void Headless_Record_CarriesNoLocalFilesystemPath()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string plan = Path.Combine(workDir, "storage.charter.md");
            File.WriteAllText(plan, PlanWithOpenHumanQuestion);

            CharterCliRunner.Run("headless", plan);

            string json = File.ReadAllText(Path.Combine(workDir, "storage.charter.headless.json"));
            Assert.DoesNotContain(workDir, json, StringComparison.OrdinalIgnoreCase);

            var record = JsonDocument.Parse(json).RootElement;
            Assert.Equal("storage.charter.md", record.GetProperty("plan").GetString());
            Assert.Equal("storage.charter.html", record.GetProperty("artifact").GetString());
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// The record persists the anchor→line source map the live review server holds only in memory — the
    /// forensic gap <c>export</c> genuinely left. Every anchor here must be one the ARTIFACT actually carries,
    /// or the map cannot be used to trace a rendered element back to its markdown line.
    /// </summary>
    [Fact]
    public void Headless_Record_PersistsASourceMapWhoseAnchorsAppearInTheArtifact()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string plan = Path.Combine(workDir, "storage.charter.md");
            File.WriteAllText(plan, PlanWithOpenHumanQuestion);

            CharterCliRunner.Run("headless", plan);

            string html = File.ReadAllText(Path.Combine(workDir, "storage.charter.html"));
            var sourceMap = JsonDocument
                .Parse(File.ReadAllText(Path.Combine(workDir, "storage.charter.headless.json")))
                .RootElement.GetProperty("sourceMap");

            Assert.NotEmpty(sourceMap.EnumerateObject());
            foreach (var entry in sourceMap.EnumerateObject())
            {
                Assert.Contains($"id=\"{entry.Name}\"", html, StringComparison.Ordinal);
                Assert.True(entry.Value.GetInt32() >= 1, $"anchor {entry.Name} has a non-positive source line");
            }
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// It never blocks: no loopback listener, no long-poll, no prompt — the requirement that makes it safe
    /// inside an unattended crewmate session. Asserted as a HARD deadline far below the shared 60s harness
    /// guard, and against a plan with an open human question (exactly the case interactive mode would wait on
    /// a person for). A regression that started the review server would hang here, not pass slowly.
    /// </summary>
    [Fact]
    public void Headless_ReturnsPromptly_WithoutServingOrWaitingForAHuman()
    {
        string workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            string plan = Path.Combine(workDir, "storage.charter.md");
            File.WriteAllText(plan, PlanWithOpenHumanQuestion);

            var stopwatch = Stopwatch.StartNew();
            var result = CharterCliRunner.Run("headless", plan);
            stopwatch.Stop();

            Assert.Equal(2, result.ExitCode);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(30),
                $"headless took {stopwatch.Elapsed} — it must not serve or wait for a human.");

            // A served page is the SDK-injected one; a review session also prints its capability URL. Neither
            // may appear on an unattended run.
            Assert.DoesNotContain("127.0.0.1", result.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("?key=", result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    /// <summary>The verb is discoverable: it appears in the help banner and in the unknown-command hint.</summary>
    [Fact]
    public void Headless_IsListedInTheHelpBanner_AndTheUnknownCommandHint()
    {
        Assert.Contains("headless", CharterCliRunner.Run().StdOut, StringComparison.Ordinal);
        Assert.Contains("headless", CharterCliRunner.Run("bogus-verb").StdErr, StringComparison.Ordinal);
    }
}
