// INVALID sample for 03-every-test-carries-the-feature-trait.ps1 -> expect exit 1.
// The one defect this guardrail exists to catch: a [SkippableFact] method with NO per-pair Feature
// trait. It compiles, it runs in the full browser suite, and it is invisible to BOTH halves of this
// task pair - the red census reports it as "no test named X ran" and the implementation task's forward
// check never asserts it. Two fact-attributes, one trait.
using Xunit;

namespace Charter.Browser.Tests;

public sealed partial class ReviewLoopBrowserTests
{
    [SkippableFact]
    [Trait("Feature", "DiagramExpandAffordance")]
    public async Task An_oversized_diagram_offers_an_expand_control()
    {
        await Task.CompletedTask;
    }

    // No Feature trait - the defect.
    [SkippableFact]
    public async Task Escape_collapses_the_expanded_diagram()
    {
        await Task.CompletedTask;
    }
}
