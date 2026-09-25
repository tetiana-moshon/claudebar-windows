using ClaudeBar.Services;
using ClaudeBar.ViewModels;
using Xunit;

namespace ClaudeBar.Tests;

/// <summary>The bounds behind the two menu pickers: a stray or out-of-range stored value must never
/// let notifications banner on calm updates or let the poll cadence hammer or stall the API.</summary>
public class SettingsPickersTests
{
    [Theory]
    [InlineData(0, Urgency.Medium)]   // Low is never a valid threshold — clamped up
    [InlineData(-5, Urgency.Medium)]
    [InlineData(1, Urgency.Medium)]
    [InlineData(2, Urgency.High)]
    [InlineData(3, Urgency.Critical)]
    [InlineData(9, Urgency.Critical)] // above the range — clamped down
    public void ClampThreshold_keeps_it_in_medium_to_critical(int stored, Urgency expected) =>
        Assert.Equal(expected, NotificationManager.ClampThreshold(stored));

    [Theory]
    [InlineData(60, 60)]
    [InlineData(300, 300)]
    [InlineData(30, 60)]       // faster than 1 min — clamped up
    [InlineData(0, 60)]
    [InlineData(99999, 3600)]  // slower than 1 hour — clamped down
    public void ClampPollSeconds_keeps_it_between_a_minute_and_an_hour(int stored, int expected) =>
        Assert.Equal(expected, UsageStore.ClampPollSeconds(stored));
}
