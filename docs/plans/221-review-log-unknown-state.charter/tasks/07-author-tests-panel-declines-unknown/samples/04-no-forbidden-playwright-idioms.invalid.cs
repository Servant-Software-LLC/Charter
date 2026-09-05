using System.Threading.Tasks;
using Xunit;

namespace Charter.Browser.Tests;

// INVALID. Uses both forbidden wait idioms in the form this codebase actually writes them - UNDOTTED,
// because WaitForEventAsync is a private static helper on this partial class, not a Playwright method.
// The earlier sample used `page.WaitForEventAsync(...)`, a dotted shape that occurs zero times in this
// repository, which is how a passing sample pair certified a ban that could never fire.
public sealed partial class ReviewLoopBrowserTests
{
    [SkippableFact]
    [Trait("Feature", "ReviewLogUnknownPanel")]
    public async Task An_unknown_view_does_not_empty_a_populated_panel()
    {
        await using var page = await OpenAsync();
        await WaitForFunctionAsync(page, "() => document.querySelectorAll('.marker').length > 0");
        await WaitForEventAsync(page, "review-log-loaded");
        Assert.Equal(3, await CountMarkersAsync(page));
    }
}
