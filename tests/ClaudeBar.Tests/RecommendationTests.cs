using ClaudeBar.Services;
using Xunit;

namespace ClaudeBar.Tests;

/// <summary>The urgency → status-glyph mapping the tray icon and popover both key off.</summary>
public class RecommendationTests
{
    [Theory]
    [InlineData(Urgency.Low, "ok")]
    [InlineData(Urgency.Medium, "warn")]
    [InlineData(Urgency.High, "alert")]
    [InlineData(Urgency.Critical, "stop")]
    public void StatusSymbol_maps_urgency(Urgency urgency, string expected) =>
        Assert.Equal(expected, new Recommendation { Urgency = urgency }.StatusSymbol);

    [Theory]
    [InlineData(Urgency.Low)]
    [InlineData(Urgency.Critical)]
    public void StatusSymbol_is_headroom_regardless_of_urgency(Urgency urgency) =>
        Assert.Equal("headroom", new Recommendation { Urgency = urgency, IsHeadroom = true }.StatusSymbol);
}
