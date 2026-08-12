using System.Text.RegularExpressions;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Holds <c>AGENTS.md</c> — the file a coding agent onboards from — to facts the repository can prove about
/// itself. It had drifted the way onboarding docs always do, silently and in the direction of the past: it
/// announced ".NET 8" long after every project moved to <c>net10.0</c>, never mentioned
/// <c>src/Charter.Server</c> at all, and called the repo a "Scaffold" whose renderer, review server and
/// annotation loop were "the next milestones" — years of shipped work described as not yet started.
///
/// That is worse than a stale README. An agent believes this file, plans against it, and a wrong premise
/// here becomes wrong work everywhere. Only the checkable claims are pinned — the target framework and the
/// set of source projects — because a test that tried to police prose would be deleted the first time it
/// was wrong, and take these two with it.
/// </summary>
[Trait("Category", "Cli")]
public class AgentsGuidanceTests
{
    [Fact]
    public void AgentsDoc_NamesTheRealTargetFramework()
    {
        string csproj = RepositoryFiles.ReadAllText("src", "Charter.Cli", "Charter.Cli.csproj");
        Match target = Regex.Match(csproj, @"<TargetFramework>net(?<major>\d+)\.\d+</TargetFramework>");
        Assert.True(target.Success, "Could not read <TargetFramework> from Charter.Cli.csproj.");

        string expected = $".NET {target.Groups["major"].Value}";
        Assert.True(
            RepositoryFiles.ReadAllText("AGENTS.md").Contains(expected, StringComparison.Ordinal),
            $"AGENTS.md does not say '{expected}', which is what the projects actually target. Update the "
                + "Project conventions bullet -- an onboarding doc that names the wrong runtime sends every "
                + "agent that reads it down a version-specific path the build will not accept.");
    }

    [Fact]
    public void AgentsDoc_NamesEverySourceProject()
    {
        string agents = RepositoryFiles.ReadAllText("AGENTS.md");

        foreach (string directory in Directory.GetDirectories(RepositoryFiles.PathTo("src")))
        {
            string project = Path.GetFileName(directory);
            Assert.True(
                agents.Contains(project, StringComparison.Ordinal),
                $"AGENTS.md never mentions the source project '{project}'. Name it in the Project conventions "
                    + "layout bullet, with what it holds -- a project an agent does not know exists is one it "
                    + "will duplicate or edit around.");
        }
    }
}
