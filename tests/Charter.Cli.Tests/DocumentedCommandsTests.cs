using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #138, one surface further out. Making the <c>--help</c> banner generate itself from
/// <see cref="CharterCommands.Commands"/> stops the CLI omitting a verb, but the same drift had already
/// reached the docs: <c>charter reply</c> shipped in 0.13.0 and the README never mentioned it, so the two
/// places a newcomer or an agent looks first BOTH denied the feature existed. That is how #138 cost a review
/// cycle — an agent enumerated Charter's capabilities, found no reply path, and wrote its answers to a
/// reviewer's notes into the plan body.
///
/// A prose document cannot be generated from the catalog, but it can be held to it. Every verb in the catalog
/// must be named as a command in BOTH entry points: <c>README.md</c> for the human, and
/// <c>skills/charter/SKILL.md</c> for the agent. Adding a verb without documenting it now fails here, naming
/// the verb and the file.
/// </summary>
[Trait("Category", "Cli")]
public class DocumentedCommandsTests
{
    [Theory]
    [MemberData(nameof(CommandCatalogTests.CatalogVerbs), MemberType = typeof(CommandCatalogTests))]
    public void Readme_DocumentsEveryCatalogCommand(string name) =>
        AssertDocumentsVerb("README.md", name, "Add a `charter <verb> …` bullet to the Usage list");

    [Theory]
    [MemberData(nameof(CommandCatalogTests.CatalogVerbs), MemberType = typeof(CommandCatalogTests))]
    public void BundledSkill_DocumentsEveryCatalogCommand(string name) =>
        AssertDocumentsVerb(
            Path.Combine("skills", "charter", "SKILL.md"),
            name,
            "Name it in the skill an agent actually loads — a verb documented only in a reference file is one "
                + "the agent has to already suspect exists before it goes looking");

    /// <summary>
    /// Asserts the document names the verb as a command — the backticked <c>`charter &lt;verb&gt;</c> form every
    /// existing mention uses. Requiring the backtick is what keeps prose like "charter renders the plan" from
    /// passing for documentation of the <c>render</c> verb.
    /// </summary>
    private static void AssertDocumentsVerb(string relativePath, string verb, string remedy)
    {
        Assert.True(
            RepositoryFiles.ReadAllText(relativePath).Contains($"`charter {verb}", StringComparison.Ordinal),
            $"{relativePath} does not document the shipped verb '{verb}'. {remedy}. A capability its reader "
                + "cannot find does not exist as far as that reader is concerned -- which is precisely what "
                + "Charter #138 cost.");
    }
}
