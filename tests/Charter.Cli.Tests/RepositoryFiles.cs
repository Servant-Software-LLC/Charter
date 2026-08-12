using System.Reflection;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Reads files from the repository itself — README.md, AGENTS.md, the bundled skill, a csproj — for the
/// tests that hold Charter's documentation to what the code actually does. The root comes from the
/// <c>CharterRepositoryRoot</c> assembly-metadata attribute the test csproj stamps at build: config-matched
/// and cross-platform, the same idiom as <c>CharterCliPath</c>, rather than walking up from the bin
/// directory (which breaks the moment the output layout changes).
/// </summary>
internal static class RepositoryFiles
{
    /// <summary>Absolute path to the repository root, with a trailing separator.</summary>
    public static string Root()
    {
        AssemblyMetadataAttribute? metadata = typeof(RepositoryFiles).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "CharterRepositoryRoot");

        string? root = metadata?.Value;
        Assert.False(string.IsNullOrEmpty(root), "The build did not set the CharterRepositoryRoot assembly metadata.");
        return root!;
    }

    /// <summary>Absolute path to a repo file named by its path segments, e.g. <c>("skills", "charter", "SKILL.md")</c>.</summary>
    public static string PathTo(params string[] segments) =>
        Path.Combine(Root(), Path.Combine(segments));

    /// <summary>Reads a repo file, failing with the resolved path when it is not where the test expected.</summary>
    public static string ReadAllText(params string[] segments)
    {
        string path = PathTo(segments);
        Assert.True(File.Exists(path), $"Expected to find {Path.Combine(segments)} at '{path}'.");
        return File.ReadAllText(path);
    }
}
