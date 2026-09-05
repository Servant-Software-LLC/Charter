using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #246. The README's "Still ahead" section is the one place in the file whose whole job is to
/// describe what does NOT exist, so it rots in the opposite direction from everything else — and no test
/// looked at it.
///
/// `charter recap` shipped and sat under that heading as "a v2 addition" for releases afterwards, while the
/// Usage list above documented the verb in full. So the README documented the feature and denied it, and
/// AGENTS.md ("the `convert` / `recap` seeds … are all implemented and released") contradicted both.
///
/// <para>
/// <b>Why <see cref="DocumentedCommandsTests"/> did not catch it, and cannot.</b> That guard requires the
/// token <c>`charter recap</c> to appear SOMEWHERE in README.md. It appeared — inside the bullet saying the
/// verb was not built. A presence assertion is satisfied by a mention that denies the thing, which makes its
/// green weaker than its docstring implies. This test is the complement: the same catalog, the opposite
/// question, scoped to the one section where naming a verb means the verb does not exist.
/// </para>
///
/// <para>
/// Deliberately structural, not a prose parse: a shipped verb may not be NAMED below the "Still ahead"
/// heading at all. That is checkable, has no false-negative story, and needs no judgement about English.
/// </para>
/// </summary>
[Trait("Category", "Cli")]
public class ReadmeStillAheadTests
{
    private const string StillAheadHeading = "## Still ahead";

    [Theory]
    [MemberData(nameof(CommandCatalogTests.CatalogVerbs), MemberType = typeof(CommandCatalogTests))]
    public void StillAhead_DoesNotListAShippedVerb(string verb)
    {
        string section = StillAheadSection();

        Assert.False(
            section.Contains($"charter {verb}", StringComparison.Ordinal),
            $"README.md's \"Still ahead\" section names the SHIPPED verb '{verb}'. That section describes what "
                + "does not exist, so a verb listed there reads as considered-and-declined -- a more durable "
                + "wrong belief than silence, and the reason Charter #246 was filed. Document the verb in the "
                + "Usage list instead, and delete the bullet here.");
    }

    /// <summary>
    /// The text from the "Still ahead" heading to the NEXT HEADING OF ANY LEVEL. Fails loudly if the heading
    /// is renamed rather than silently scanning nothing — an empty section would pass every assertion above
    /// and prove nothing, which is the failure mode this whole file exists to avoid.
    /// </summary>
    /// <remarks>
    /// Stopping at any heading, not just <c>##</c>, is load-bearing. "Still ahead" used to run on into a
    /// second list of things Charter <b>won't build</b>, and those bullets name shipped verbs ON PURPOSE —
    /// "hosted share/publish: not building it, because `charter export` already produces a self-contained
    /// offline artifact". The shipped capability is the whole argument. Scanning that far flagged
    /// <c>export</c> as a false positive the first time this test ran, so the README now separates the two
    /// claims under <c>### Settled — not building these</c>, and this scan stops there. Do not widen it back:
    /// a rule that cannot tell "this does not exist" from "this exists, which is why we need no more" will be
    /// silenced by whoever hits it next.
    /// </remarks>
    private static string StillAheadSection()
    {
        string readme = RepositoryFiles.ReadAllText("README.md");

        int start = readme.IndexOf(StillAheadHeading, StringComparison.Ordinal);
        Assert.True(
            start >= 0,
            $"README.md no longer has a '{StillAheadHeading}' heading. If the section was renamed, point this "
                + "test at the new name; if it was deleted, delete this test. Do NOT leave it scanning nothing "
                + "-- an empty section passes vacuously.");

        int next = readme.IndexOf("\n#", start + StillAheadHeading.Length, StringComparison.Ordinal);
        return next < 0 ? readme[start..] : readme[start..next];
    }
}
