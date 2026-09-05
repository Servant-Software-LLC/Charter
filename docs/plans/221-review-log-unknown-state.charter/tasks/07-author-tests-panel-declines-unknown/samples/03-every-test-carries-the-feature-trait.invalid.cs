using System.Threading.Tasks;
using Xunit;

namespace Charter.Browser.Tests;

// INVALID: the Feature trait is written ONCE on the partial CLASS instead of per method. A C# attribute
// on a partial declaration applies to the WHOLE type, so this silently widens the pair's filter to every
// browser test in the project - the exact defect this guardrail exists to catch. Two [SkippableFact]
// methods, ZERO per-method traits.
[Trait("Feature", "ReviewLogUnknownPanel")]
public sealed partial class ReviewLoopBrowserTests
{
    [SkippableFact]
    public async Task An_unknown_view_does_not_empty_a_populated_panel()
    {
        await using var page = await OpenAsync();
        await WaitForEventCountAsync(page, "markers-rendered", 1);
        Assert.Equal(3, await CountMarkersAsync(page));
    }

    [SkippableFact]
    public async Task A_genuinely_empty_view_still_empties_the_panel()
    {
        await using var page = await OpenAsync();
        await WaitForEventCountAsync(page, "log-loaded", 1);
        Assert.Equal(0, await CountMarkersAsync(page));
    }
}
