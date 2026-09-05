using System.Threading.Tasks;
using Xunit;

namespace Charter.Browser.Tests;

// VALID sample. Two [SkippableFact] methods, each carrying the per-METHOD Feature trait, the trait
// NOWHERE above the class declaration, and the CORRECT wait helpers. It deliberately contains a string
// literal with a `//` in it - the shape that broke the earlier strip order and undercounted the methods.
public sealed partial class ReviewLoopBrowserTests
{
    [SkippableFact]
    [Trait("Feature", "ReviewLogNotLoaded")]
    public async Task An_unknown_view_does_not_empty_a_populated_panel()
    {
        await using var page = await OpenAsync();
        var ns = "const HTML = 'http://www.w3.org/1999/xhtml';";
        await WaitForEventCountAsync(page, "markers-rendered", 1);
        Assert.Equal(3, await CountMarkersAsync(page));
    }

    [SkippableFact]
    [Trait("Feature", "ReviewLogNotLoaded")]
    public async Task A_genuinely_empty_view_still_empties_the_panel()
    {
        await using var page = await OpenAsync();
        // A comment naming WaitForFunctionAsync must NOT trip the ban - a mention is not a use.
        await WaitForEventCountAsync(page, "review-log-loaded", 1);
        Assert.Equal(0, await CountMarkersAsync(page));
    }
}
