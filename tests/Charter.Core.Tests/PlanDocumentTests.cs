using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

public class PlanDocumentTests
{
    [Fact]
    public void Empty_HasTitleAndNoBlocks()
    {
        Assert.Equal("Untitled plan", PlanDocument.Empty.Title);
        Assert.Empty(PlanDocument.Empty.Blocks);
    }
}
