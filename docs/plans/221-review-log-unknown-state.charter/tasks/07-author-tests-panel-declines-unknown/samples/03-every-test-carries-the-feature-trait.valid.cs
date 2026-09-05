using System.Threading.Tasks;
using Xunit;

namespace Charter.Browser.Tests;

// A representative VALID sample: two [SkippableFact] methods, each carrying the per-METHOD Feature
// trait, and using WaitForEventCountAsync (the correct helper, which must NOT trip the ban).
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
        // A comment naming WaitForFunctionAsync must NOT trip the ban - it is a mention, not a use.
        await WaitForEventCountAsync(page, "log-loaded", 1);
        Assert.Equal(0, await CountMarkersAsync(page));
    }
}
