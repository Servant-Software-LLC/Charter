using System.Threading.Tasks;
using Xunit;

namespace Charter.Browser.Tests;

// INVALID: uses BOTH forbidden wait idioms as real dotted calls.
public sealed partial class ReviewLoopBrowserTests
{
    [SkippableFact]
    [Trait("Feature", "ReviewLogUnknownPanel")]
    public async Task An_unknown_view_does_not_empty_a_populated_panel()
    {
        await using var page = await OpenAsync();
        await page.WaitForFunctionAsync("() => document.querySelectorAll('.marker').length > 0");
        await page.WaitForEventAsync("console");
        Assert.Equal(3, await CountMarkersAsync(page));
    }
}
