using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// The stable-id derivation is the behavioral seam under test: content-derived anchors are what let a
/// human's annotation survive edits to unrelated blocks. These tests pin the three properties that
/// makes anchors trustworthy — same content =&gt; same id, different content =&gt; different id, and
/// determinism independent of parse instance / run.
/// </summary>
[Trait("Category", "CoreRenderer")]
public class StableIdTests
{
    [Fact]
    public void StableId_SameContent_YieldsSameId()
    {
        const string content = "The exact same block content.";

        // A pure, content-derived hash: identical content must map to an identical id.
        Assert.Equal(Block.StableId(content), Block.StableId(content));
    }

    [Fact]
    public void StableId_DifferentContent_YieldsDifferentId()
    {
        // Distinct block content must produce distinct anchors, or two blocks would collide onto one id.
        Assert.NotEqual(Block.StableId("one block of prose"), Block.StableId("a different block"));
    }

    [Fact]
    public void StableId_IsDeterministicAcrossRuns_ForIndependentParses()
    {
        // Two independent parses of identical markdown must derive the same id for the same block: the id
        // depends only on content, never on parse order, instance identity, or a run-varying seed. This
        // is the proxy for "deterministic across process runs" — a pure function of content.
        const string markdown = "# Title\n\nA stable paragraph.";

        var first = BlockDocument.Parse(markdown).Blocks;
        var second = BlockDocument.Parse(markdown).Blocks;

        Assert.Equal(first[1].Id, second[1].Id);
    }
}
