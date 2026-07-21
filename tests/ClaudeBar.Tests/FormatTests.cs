using ClaudeBar.Models;
using Xunit;

namespace ClaudeBar.Tests;

/// <summary>The human-readable duration formatting used across the menu, recommendations, and the
/// rate-limit countdown. Sub-minute values collapsing to "0m" is intentional (see the rate-limit
/// live-countdown behavior).</summary>
public class FormatTests
{
    [Theory]
    [InlineData(0, "0m")]
    [InlineData(59, "0m")]      // under a minute rounds down to 0m
    [InlineData(60, "1m")]
    [InlineData(599, "9m")]
    [InlineData(3600, "1h")]    // exact hour drops the trailing 0m
    [InlineData(3660, "1h 1m")]
    [InlineData(7200, "2h")]
    [InlineData(86400, "1d")]   // exact day drops the trailing 0h
    [InlineData(90000, "1d 1h")]
    [InlineData(172800, "2d")]
    public void Duration_formats_seconds(double seconds, string expected) =>
        Assert.Equal(expected, Format.Duration(seconds));

    [Fact]
    public void Duration_clamps_negative_to_zero() =>
        Assert.Equal("0m", Format.Duration(-120));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Duration_returns_placeholder_for_non_finite(double value) =>
        Assert.Equal("—", Format.Duration(value));
}
