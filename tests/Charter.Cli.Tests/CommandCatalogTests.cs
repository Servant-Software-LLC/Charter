using System.CommandLine;
using System.Text.RegularExpressions;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// The anti-drift guard for Charter #138. `charter --help` used to list nine verbs while eleven were
/// dispatched — <c>reply</c> was missing from the banner and <c>recap</c> from BOTH the banner and the
/// unknown-verb error — because dispatch and the two help surfaces were three hand-maintained lists. An agent
/// enumerating Charter's capabilities from <c>--help</c> (the consumer that reads help, not release notes)
/// concluded <c>charter reply</c> did not exist and wrote its review responses into the plan body instead.
///
/// Those three lists are now one enumerable catalog, <see cref="CharterCommands.Commands"/>, and these tests
/// hold that line: every catalog verb must appear in BOTH user-facing command lists, in catalog order, and must
/// actually dispatch. A verb added to the catalog and forgotten in help is no longer expressible; a refactor
/// that reintroduces a hand-written list — or drops <c>recap</c>/<c>reply</c> again — fails here.
/// </summary>
[Trait("Category", "Cli")]
public class CommandCatalogTests
{
    /// <summary>One test case per catalog verb, so a failure names the verb rather than the whole set.</summary>
    public static IEnumerable<object[]> CatalogVerbs =>
        CharterCommands.Names.Select(name => new object[] { name });

    [Fact]
    public void Catalog_ContainsRecapAndReply()
    {
        // The two verbs #138 was about, asserted BY NAME: `reply` was absent from the banner, `recap` from both
        // surfaces. A future refactor that quietly drops either fails loudly here rather than in an agent's face.
        Assert.Contains("recap", CharterCommands.Names);
        Assert.Contains("reply", CharterCommands.Names);
    }

    [Fact]
    public void Catalog_NamesAreUnique()
    {
        // Duplicate names would make dispatch order-dependent and print the same verb twice in help.
        Assert.Equal(CharterCommands.Names.Count, CharterCommands.Names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void HelpBanner_ListsEveryCatalogCommand()
    {
        var result = CharterCliRunner.Run();

        Assert.Equal(0, result.ExitCode);

        // Spectre styles and word-wraps the banner to the console width, so compare against the text with colour
        // codes stripped and runs of whitespace collapsed — the verbs then read back as one line.
        string banner = Normalize(result.StdOut);

        foreach (string name in CharterCommands.Names)
        {
            Assert.True(
                banner.Contains(name, StringComparison.Ordinal),
                $"`charter --help` does not list the dispatched verb '{name}'. Banner was:\n{banner}");
        }

        // Stronger than per-name containment: the banner must print the catalog's names, in catalog order — which
        // is only true if it is GENERATED from the catalog rather than hand-maintained beside it.
        Assert.Contains($"Commands: {string.Join(", ", CharterCommands.Names)}.", banner, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitHelpFlag_ListsEveryCatalogCommand()
    {
        // `--help` takes the same banner path as no-args, and stays exit 0.
        var result = CharterCliRunner.Run("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            $"Commands: {string.Join(", ", CharterCommands.Names)}.", Normalize(result.StdOut), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownVerb_ListsEveryCatalogCommand_OnStderr_AndExitsNonZero()
    {
        var result = CharterCliRunner.Run("bogus-verb");

        // The deliberate non-zero exit (a silent fall-through to help + exit 0 was the footgun) and the
        // stderr channel both stay exactly as they were.
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("charter: unknown command 'bogus-verb'", result.StdErr, StringComparison.Ordinal);

        foreach (string name in CharterCommands.Names)
        {
            Assert.True(
                result.StdErr.Contains(name, StringComparison.Ordinal),
                $"The unknown-command error does not list the dispatched verb '{name}'. Stderr was:\n{result.StdErr}");
        }

        // The error prints the DERIVED line, not a literal: re-hardcoding a list in Program.cs fails here.
        Assert.Contains(CharterCommands.CommandListLine, result.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CatalogVerbs))]
    public void CatalogCommand_BuildsARootHostingASubcommandOfItsOwnName(string name)
    {
        // In-proc pairing check: an entry wired to the wrong builder — ("recap", BuildConvertRoot) — would list a
        // verb in help that dispatches to something else. Cheap, and it names the mis-paired verb.
        RootCommand root = CharterCommands.Commands.Single(command => command.Name == name).Build();

        Assert.Contains(root.Subcommands, subcommand => subcommand.Name == name);
    }

    [Theory]
    [MemberData(nameof(CatalogVerbs))]
    public void CatalogCommand_IsDispatchable(string name)
    {
        // End-to-end: the real binary accepts the verb and prints its own help (exit 0). A name that is listed but
        // not dispatched, or dispatched to another verb's root, fails one of these two assertions.
        var result = CharterCliRunner.Run(name, "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(name, result.StdOut, StringComparison.Ordinal);
    }

    /// <summary>Strip ANSI colour codes and collapse whitespace, so wrapped, styled console text compares as one line.</summary>
    private static string Normalize(string consoleText) =>
        Regex.Replace(Regex.Replace(consoleText, @"\x1B\[[0-9;?]*[ -/]*[@-~]", string.Empty), @"\s+", " ");
}
