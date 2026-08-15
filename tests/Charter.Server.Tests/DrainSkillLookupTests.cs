using System;
using System.IO;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Charter #116 — the review page hands over <c>/charter-drain &lt;plan&gt;</c>, and that is only better than
/// the raw command line it replaced if the skill actually resolves. Handing over a call that resolves to
/// nothing trades one silent failure for another, which is the exact defect #144 set out to remove.
///
/// <para>
/// The lookup is deliberately ASYMMETRIC in its consequences: a wrong "missing" costs the reviewer one extra
/// true sentence ("install the skills first"), while a wrong "installed" costs them a paste that does nothing
/// and no idea why. So it is used to ADD an instruction, never to withhold one — see the SDK side.
/// </para>
/// </summary>
public class DrainSkillLookupTests
{
    [Fact]
    public void FindsAProjectScopedInstallBesideThePlan()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".claude", "skills", DrainSkillLookup.SkillName));
            var plan = Path.Combine(root, "plan.charter.md");
            File.WriteAllText(plan, "# plan\n");

            Assert.True(DrainSkillLookup.IsInstalled(plan, EmptyHome(root)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// An agent reviewing this plan is working in this repo, so a repo-scoped install lives at or ABOVE the
    /// plan — plans live under <c>docs/plans/</c>, the skills at the repo root.
    /// </summary>
    [Fact]
    public void WalksUpFromThePlanToTheRepoRoot()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".claude", "skills", DrainSkillLookup.SkillName));
            var nested = Path.Combine(root, "docs", "plans");
            Directory.CreateDirectory(nested);
            var plan = Path.Combine(nested, "plan.charter.md");
            File.WriteAllText(plan, "# plan\n");

            Assert.True(DrainSkillLookup.IsInstalled(plan, EmptyHome(root)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ReportsMissingWhenNoSkillDirectoryExists()
    {
        var root = NewTempDir();
        try
        {
            // A .claude/skills with OTHER skills but not this one is still "missing" — the page's question is
            // whether THIS invocation resolves, not whether the reviewer has ever installed anything.
            Directory.CreateDirectory(Path.Combine(root, ".claude", "skills", "charter-format"));
            var plan = Path.Combine(root, "plan.charter.md");
            File.WriteAllText(plan, "# plan\n");

            Assert.False(DrainSkillLookup.IsInstalled(plan, EmptyHome(root)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ANullOrEmptyPlanPathIsNotAnError()
    {
        // The home-directory candidate is still checked, so this asserts only that it does not throw —
        // whichever answer this machine gives is legitimate.
        var _ = DrainSkillLookup.IsInstalled(null);
        var __ = DrainSkillLookup.IsInstalled(string.Empty);
    }

    /// <summary>A home directory with no .claude/skills in it, so only the project walk can answer.</summary>
    private static string EmptyHome(string root)
    {
        var home = Path.Combine(root, "fake-home");
        Directory.CreateDirectory(home);
        return home;
    }

    /// <summary>
    /// A temp directory that looks like a REPO ROOT. The <c>.git</c> marker is what stops the project walk
    /// climbing out of the fixture: without it the walk continues up through the user's home and finds the
    /// developer's own <c>~/.claude/skills</c>, which makes the missing-skill case unassertable on any
    /// machine that has the skill installed. It did exactly that the first time this was written.
    /// </summary>
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-skilllookup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort temp cleanup.
        }
    }
}
