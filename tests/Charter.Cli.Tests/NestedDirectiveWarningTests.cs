using System.IO;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #203, the author-time half: <c>render</c>, <c>review</c> and <c>handoff</c> warn about a <c>:::</c>
/// directive that renders live and is invisible to the block model, and <c>headless</c>/strict <c>handoff</c>
/// escalate the ones that cost something.
/// </summary>
/// <remarks>
/// The warning has to arrive at AUTHOR time, because by the time the divergence bites — a reviewer answering a
/// form whose answer can never be folded back — the damage is already done. It is a warning and not an error
/// for the same reason its siblings are: Charter reports, the agent decides.
/// </remarks>
[Trait("Category", "NestedDirectiveWarning")]
public class NestedDirectiveWarningTests
{
    private const string NestedQuestionPlan =
        "---\ncharter-format-version: 1\n---\n\n" +
        "# Nested Question Plan\n\nAn overview paragraph.\n\n" +
        "::::note\nA callout that asks something.\n\n" +
        ":::question\n" +
        "{\"id\":\"q-nested\",\"title\":\"Which datastore?\",\"mode\":\"single\"," +
        "\"options\":[\"Postgres\",\"DynamoDB\"],\"recommended\":\"Postgres\",\"target\":\"human\"}\n" +
        ":::\n::::\n";

    private const string NestedComparisonPlan =
        "---\ncharter-format-version: 1\n---\n\n" +
        "# Nested Comparison Plan\n\nAn overview paragraph.\n\n" +
        "::::note\nA callout that compares.\n\n" +
        ":::comparison\n- **Postgres** - mature\n- **DynamoDB** - scales\n:::\n::::\n";

    // The negative control that matters most: an INERT nesting. :::custom-html never renders its children, so
    // this :::question is prose on the page — it asserts nothing false and it is the author's own markup.
    private const string InertNestingPlan =
        "---\ncharter-format-version: 1\n---\n\n" +
        "# Inert Nesting Plan\n\nAn overview paragraph.\n\n" +
        "::::custom-html\n<p>Author markup showing what a question looks like:</p>\n\n" +
        ":::question\n{\"id\":\"q-example\",\"title\":\"An example\",\"mode\":\"single\"," +
        "\"options\":[\"A\",\"B\"],\"target\":\"human\"}\n" +
        ":::\n::::\n";

    [Theory]
    [InlineData("render", "out.html")]
    [InlineData("handoff", "plan.md")]
    public void RenderAndHandoff_WarnAboutANestedQuestion_WithoutChangingTheExitCode(string verb, string outName)
    {
        var workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            var plan = WritePlan(workDir, NestedQuestionPlan);
            var outputPath = Path.Combine(workDir, outName);

            var result = CharterCliRunner.Run(verb, plan, "-o", outputPath);

            // A warning, never an error — the same contract every sibling lint keeps.
            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(outputPath));

            Assert.Contains($"charter {verb}: warning:", result.StdErr, StringComparison.Ordinal);
            Assert.Contains("nested inside a container", result.StdErr, StringComparison.Ordinal);
            Assert.Contains(":::question", result.StdErr, StringComparison.Ordinal);

            // The message ranks the finding rather than making the reviewer rank it, and states the rule.
            Assert.Contains("DECISION LOST", result.StdErr, StringComparison.Ordinal);
            Assert.Contains("must be a top-level block", result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// A nested <c>:::comparison</c> is warned about and nothing more: its body was READ out of the flatten and
    /// survives blockquoting intact, so it never escalates and never blocks.
    /// </summary>
    [Fact]
    public void ANestedComparison_Warns_ButNeitherEscalatesNorBlocks()
    {
        var workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            var plan = WritePlan(workDir, NestedComparisonPlan);

            // Recorded — `headless` carries Charter's diagnostics in the record, not on stderr — but neither
            // the exit code nor the strict gate moves.
            var headless = CharterCliRunner.Run("headless", plan, "--out-dir", workDir);
            Assert.Equal(0, headless.ExitCode);

            var record = File.ReadAllText(Directory.GetFiles(workDir, "*.headless.json")[0]);
            Assert.Contains("\"nested-directive\"", record, StringComparison.Ordinal);
            Assert.Contains("\"needsHuman\": false", record, StringComparison.Ordinal);

            var strict = CharterCliRunner.Run(
                "handoff", plan, "-o", Path.Combine(workDir, "plan.md"), "--fail-if-needs-human");
            Assert.Equal(0, strict.ExitCode);
            Assert.Contains("nested inside a container", strict.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// The escalation, end to end. Before this existed, this exact plan exited <b>0</b> from both verbs with
    /// <c>"needsHuman": false, "questions": []</c> — over a form a human can answer on the page.
    /// </summary>
    [Fact]
    public void ANestedQuestion_EscalatesFromHeadlessAndFromStrictHandoff()
    {
        var workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            var plan = WritePlan(workDir, NestedQuestionPlan);

            var headless = CharterCliRunner.Run("headless", plan, "--out-dir", workDir);
            Assert.Equal(2, headless.ExitCode);

            var record = File.ReadAllText(Directory.GetFiles(workDir, "*.headless.json")[0]);
            Assert.Contains("\"needsHuman\": true", record, StringComparison.Ordinal);
            Assert.Contains("\"nested-question\"", record, StringComparison.Ordinal);

            var strict = CharterCliRunner.Run(
                "handoff", plan, "-o", Path.Combine(workDir, "plan.md"), "--fail-if-needs-human");
            Assert.Equal(2, strict.ExitCode);
            Assert.Contains("nested-question", strict.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// <b>The negative control, and the one that keeps the lint honest.</b> A <c>:::question</c> inside
    /// <c>:::custom-html</c> renders as inert prose — a plan DOCUMENTING Charter does exactly this — so it
    /// asserts nothing false and must produce no warning, no note and no escalation. A structural
    /// "is it nested?" predicate would have flagged it, which is why the predicate is "does it render live".
    /// </summary>
    [Fact]
    public void AnInertNesting_IsSilentEverywhere()
    {
        var workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            var plan = WritePlan(workDir, InertNestingPlan);

            var render = CharterCliRunner.Run("render", plan, "-o", Path.Combine(workDir, "out.html"));
            Assert.Equal(0, render.ExitCode);
            Assert.DoesNotContain("nested inside a container", render.StdErr, StringComparison.Ordinal);

            var headless = CharterCliRunner.Run("headless", plan, "--out-dir", workDir);
            Assert.Equal(0, headless.ExitCode);

            var record = File.ReadAllText(Directory.GetFiles(workDir, "*.headless.json")[0]);
            Assert.DoesNotContain("nested-", record, StringComparison.Ordinal);
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    private static string WritePlan(string workDir, string content)
    {
        var path = Path.Combine(workDir, "plan.charter.md");
        File.WriteAllText(path, content);
        return path;
    }
}
