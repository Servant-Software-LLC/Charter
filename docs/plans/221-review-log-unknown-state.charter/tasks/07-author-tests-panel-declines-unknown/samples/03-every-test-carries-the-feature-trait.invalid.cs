using System.Threading.Tasks;
using Xunit;

namespace Charter.Browser.Tests;

// INVALID. The Feature trait is on the CLASS *and* on every method, so a pure count comparison passes -
// this is the exact shape that defeated the first version of the guardrail. A C# attribute on any
// partial declaration applies to the whole type, so the pair's filter is widened to every browser test.
[Trait("Feature", "ReviewLogUnknownPanel")]
public sealed partial class ReviewLoopBrowserTests
{
    [SkippableFact]
    [Trait("Feature", "ReviewLogUnknownPanel")]
    public async Task An_unknown_view_does_not_empty_a_populated_panel()
    {
        await using var page = await OpenAsync();
        await WaitForEventCountAsync(page, "markers-rendered", 1);
        Assert.Equal(3, await CountMarkersAsync(page));
    }

    [SkippableFact]
    [Trait("Feature", "ReviewLogUnknownPanel")]
    public async Task A_genuinely_empty_view_still_empties_the_panel()
    {
        await using var page = await OpenAsync();
        await WaitForEventCountAsync(page, "review-log-loaded", 1);
        Assert.Equal(0, await CountMarkersAsync(page));
    }
}
