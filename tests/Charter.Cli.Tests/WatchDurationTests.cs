using System;
using Charter.Cli;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// <c>--for</c>'s duration parser (#118). Deliberately tiny — a CLI convenience, not a scheduling language —
/// but it gates how long a watch listens, so a silently-misread value would end a review early with the
/// reviewer believing an agent was still there.
/// </summary>
[Trait("Category", "WatchDuration")]
public class WatchDurationTests
{
    [Theory]
    [InlineData("90s", 90)]
    [InlineData("45m", 45 * 60)]
    [InlineData("2h", 2 * 60 * 60)]
    [InlineData("30", 30)]          // a bare number is seconds
    [InlineData("1.5h", 5400)]      // fractional, because "90m" and "1.5h" are the same intent
    [InlineData("  15m  ", 900)]    // trimmed
    [InlineData("2H", 7200)]        // case-insensitive unit
    public void ParsesTheDurationsAnAgentWouldActuallyType(string text, int expectedSeconds)
    {
        Assert.True(PollCommand.TryParseDuration(text, out var value));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), value);
    }

    /// <summary>
    /// Anything unreadable must FAIL rather than fall back to a default. A typo silently becoming "2 hours"
    /// is the same class of defect as the status line that claimed an agent was revising: the caller believes
    /// something the system does not know.
    /// </summary>
    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("0s")]       // a zero budget would exit instantly and look like a broken watch
    [InlineData("-5m")]
    [InlineData("5d")]       // days deliberately unsupported — a review is not a multi-day daemon
    [InlineData("m")]
    public void RefusesAnythingItCannotReadExactly(string? text)
    {
        Assert.False(PollCommand.TryParseDuration(text, out var value));
        Assert.Equal(default, value);
    }
}
