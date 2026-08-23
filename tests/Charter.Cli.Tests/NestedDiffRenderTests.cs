using System.IO;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #208, end to end through the REAL CLI: a plan with a <c>:::diff</c> inside a <c>::::note</c>
/// renders, and #203's warning still fires — now with nothing following it.
/// </summary>
/// <remarks>
/// <para>
/// The defect was reproduced exactly here: <c>charter render</c> exited <b>1</b>, printing #203's
/// nested-directive warning and then <c>charter render: The given key '14' was not present in the
/// dictionary.</c> — an unhandled <c>KeyNotFoundException</c> out of <c>AnchorAssignment.SubIdForLine</c>,
/// surfaced as a CLI error. The author was handed the real cause and then denied the render that would let
/// them act on it.
/// </para>
/// <para>
/// <b>The pairing is the point.</b> Rendering the shape is not the same as SUPPORTING it, so the warning
/// assertion and the strict-gate assertion belong in one test file: <c>render</c> is total, and the shape is
/// still refused at the gate that has evidence against it (#203 — a nested diff's <c>+</c>/<c>-</c> lines are
/// eaten as bullet markers in the flatten, so an added and a removed line become indistinguishable). A future
/// change that "fixed" the crash by loosening the gate would go red here.
/// </para>
/// </remarks>
[Trait("Category", "NestedDiffRender")]
public class NestedDiffRenderTests
{
    private const string NestedDiffPlan =
        "---\ncharter-format-version: 1\n---\n\n" +
        "# Nested Diff Plan\n\nAn overview paragraph.\n\n" +
        "::::note\nThe change we are proposing:\n\n" +
        ":::diff\n```diff\n" +
        "-const timeout = 30;\n" +
        "+const timeout = 120;\n" +
        " const retries = 3;\n" +
        "```\n:::\n::::\n";

    [Fact]
    public void Render_ANestedDiff_Succeeds_AndStillWarns()
    {
        var workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            var plan = WritePlan(workDir);
            var outputPath = Path.Combine(workDir, "out.html");

            var result = CharterCliRunner.Run("render", plan, "-o", outputPath);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(outputPath));

            // The warning is intact — this is a shape an author must still fix — and it is the LAST thing on
            // stderr. The dictionary error that used to follow it is the whole defect.
            Assert.Contains("nested inside a container", result.StdErr, StringComparison.Ordinal);
            Assert.Contains(":::diff", result.StdErr, StringComparison.Ordinal);
            Assert.Contains("CONTENT CORRUPTED", result.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("not present in the dictionary", result.StdErr, StringComparison.Ordinal);

            // …and the rendered file carries the diff readably, with no anchors on its lines (#166).
            var html = File.ReadAllText(outputPath);
            Assert.Contains("<div class=\"diff-line diff-del\">-const timeout = 30;", html, StringComparison.Ordinal);
            Assert.Contains("<div class=\"diff-line diff-add\">+const timeout = 120;", html, StringComparison.Ordinal);
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Export_ANestedDiff_Succeeds_AndTheArtifactCarriesTheDiff()
    {
        var workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            var plan = WritePlan(workDir);
            var outputPath = Path.Combine(workDir, "artifact.html");

            var result = CharterCliRunner.Run("export", plan, "-o", outputPath);

            Assert.Equal(0, result.ExitCode);

            var html = File.ReadAllText(outputPath);
            Assert.Contains("<div class=\"diff-line diff-add\">+const timeout = 120;", html, StringComparison.Ordinal);
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// The control: a render that no longer crashes is NOT a nested <c>:::diff</c> becoming supported. Strict
    /// handoff still blocks it, on the evidence #203 gathered from the real flatten.
    /// </summary>
    [Fact]
    public void ANestedDiff_StillBlocksStrictHandoff()
    {
        var workDir = CharterCliRunner.NewTempDirectory();
        try
        {
            var plan = WritePlan(workDir);

            var strict = CharterCliRunner.Run(
                "handoff", plan, "-o", Path.Combine(workDir, "plan.md"), "--fail-if-needs-human");

            Assert.Equal(2, strict.ExitCode);
            Assert.Contains("nested-diff", strict.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            CharterCliRunner.TryDeleteDirectory(workDir);
        }
    }

    private static string WritePlan(string workDir)
    {
        var path = Path.Combine(workDir, "plan.charter.md");
        File.WriteAllText(path, NestedDiffPlan);
        return path;
    }
}
