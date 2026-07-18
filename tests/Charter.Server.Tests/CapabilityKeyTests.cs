using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Capability-key tests: a freshly minted <see cref="CapabilityKey"/> must recognise its own value and
/// reject everything else — a different session's key, and empty/null presentations — so the per-session
/// secret is the only thing that authorizes access to a served plan.
/// </summary>
[Trait("Category", "ReviewServer")]
public class CapabilityKeyTests
{
    [Fact]
    public void Matches_ItsOwnValue_IsTrue()
    {
        var key = CapabilityKey.Create();

        Assert.True(key.Matches(key.Value));
    }

    [Fact]
    public void Matches_ADifferentSessionsKey_IsFalse()
    {
        var key = CapabilityKey.Create();
        var other = CapabilityKey.Create();

        // Two Create()d keys must be distinct secrets, so one must not admit the other's value.
        Assert.NotEqual(key.Value, other.Value);
        Assert.False(key.Matches(other.Value));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Matches_EmptyOrNullPresentation_IsFalse(string? presented)
    {
        var key = CapabilityKey.Create();

        Assert.False(key.Matches(presented));
    }
}
