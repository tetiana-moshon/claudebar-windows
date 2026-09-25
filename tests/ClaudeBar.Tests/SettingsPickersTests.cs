using ClaudeBar.Services;
using ClaudeBar.ViewModels;
using ClaudeBar.Views;
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

    // The poll picker seeds from the raw stored value; an off-bucket value (hand-edited or legacy
    // settings.json) must snap to the nearest option so the dropdown never renders blank.
    private static readonly (string text, int value)[] PollOptions =
        { ("1 min", 60), ("2 min", 120), ("5 min", 300), ("10 min", 600) };

    [Theory]
    [InlineData(60, 0)]     // exact match
    [InlineData(600, 3)]    // exact match
    [InlineData(180, 1)]    // between 120 and 300, closer to 120
    [InlineData(240, 2)]    // between 120 and 300, closer to 300
    [InlineData(10, 0)]     // below the range — nearest is the smallest
    [InlineData(99999, 3)]  // above the range — nearest is the largest
    public void NearestIndex_snaps_an_off_bucket_value_to_the_closest_option(int stored, int expectedIndex) =>
        Assert.Equal(expectedIndex, PreferencesWindow.NearestIndex(stored, PollOptions));
}
