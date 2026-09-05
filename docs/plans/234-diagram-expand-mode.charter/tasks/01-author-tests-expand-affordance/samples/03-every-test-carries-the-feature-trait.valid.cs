// VALID sample for 03-every-test-carries-the-feature-trait.ps1 -> expect exit 0.
// A complete, representative correct artifact: every [SkippableFact] carries the per-pair Feature trait.
// The decoy [Trait(...)] in the comment and in the string literal below must NOT be counted as a use -
// the guardrail strips comments and string literals before it counts the fact-attributes.
// [Trait("Feature", "DiagramExpandAffordance")]  <- a MENTION in a comment, not a use
using Xunit;

namespace Charter.Browser.Tests;

public sealed partial class ReviewLoopBrowserTests
{
    [SkippableFact]
    [Trait("Feature", "DiagramExpandAffordance")]
    public async Task An_oversized_diagram_offers_an_expand_control()
    {
        var note = "[Trait(\"Feature\", \"DiagramExpandAffordance\")] inside a string is not a use either";
        await Task.CompletedTask;
        Assert.NotNull(note);
    }

    [SkippableFact]
    [Trait("Feature", "DiagramExpandAffordance")]
    public async Task Escape_collapses_the_expanded_diagram()
    {
        await Task.CompletedTask;
    }
}
