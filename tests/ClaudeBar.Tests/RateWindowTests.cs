using System;
using ClaudeBar.Models;
using Xunit;

namespace ClaudeBar.Tests;

/// <summary>The forecasting math every surface derives its numbers from — remaining, time-to-reset,
/// burn rate, and especially the PaceTier verdict.</summary>
public class RateWindowTests
{
    // used% + hours in the window + seconds until reset → a window pinned relative to now.
    private static RateWindow Window(double used, double windowHours, double remainingSeconds) =>
        new(used, TimeSpan.FromHours(windowHours), DateTime.Now.AddSeconds(remainingSeconds));

    [Theory]
    [InlineData(30, 70)]
    [InlineData(0, 100)]
    [InlineData(120, 0)]   // over-100 clamps, never negative
    public void RemainingPercent_clamps_to_zero(double used, double expected) =>
        Assert.Equal(expected, new RateWindow(used, TimeSpan.FromHours(5), null).RemainingPercent);

    [Fact]
    public void TimeUntilReset_is_null_without_a_reset() =>
        Assert.Null(new RateWindow(10, TimeSpan.FromHours(5), null).TimeUntilReset);

    [Fact]
    public void TimeUntilReset_is_positive_and_floored_at_zero()
    {
        Assert.InRange(Window(10, 5, 3600).TimeUntilReset!.Value, 3540, 3600);
        Assert.Equal(0, Window(10, 5, -3600).TimeUntilReset); // a past reset clamps to 0
    }

    [Fact]
    public void BurnRate_and_projection_need_more_than_five_minutes_elapsed()
    {
        var barelyStarted = Window(20, 5, 5 * 3600 - 100); // ~100s elapsed
        Assert.Null(barelyStarted.BurnRatePerHour);
        Assert.Null(barelyStarted.ProjectedUsageAtReset);

        var wellUnderway = Window(20, 5, 9000); // 2.5h elapsed
        Assert.NotNull(wellUnderway.BurnRatePerHour);
        Assert.NotNull(wellUnderway.ProjectedUsageAtReset);
    }

    [Fact]
    public void TimeToExhaustion_is_null_when_nothing_is_burning() =>
        Assert.Null(Window(0, 5, 9000).TimeToExhaustion); // 0% used ⇒ zero burn rate

    // MARK: - PaceTier

    [Fact]
    public void PaceTier_is_early_without_a_reset() =>
        Assert.Equal(PaceTier.Early, new RateWindow(30, TimeSpan.FromHours(5), null).PaceTier(WindowKind.Session));

    [Fact]
    public void PaceTier_runs_out_when_almost_nothing_remains() =>
        // 96% used ⇒ <5% left ⇒ measured exhaustion, reported regardless of forecast warmup.
        Assert.Equal(PaceTier.RunsOut, Window(96, 5, 9000).PaceTier(WindowKind.Session));

    [Fact]
    public void PaceTier_is_early_before_enough_of_the_window_elapsed() =>
        // 20% used, only ~3% of the window gone ⇒ too little to trust a whole-window average.
        Assert.Equal(PaceTier.Early, Window(20, 5, 5 * 3600 - 600).PaceTier(WindowKind.Session));

    [Fact]
    public void PaceTier_is_on_pace_for_a_healthy_projection() =>
        // 20% used at the half-way mark ⇒ ~40% projected.
        Assert.Equal(PaceTier.OnPace, Window(20, 5, 9000).PaceTier(WindowKind.Session));

    [Fact]
    public void PaceTier_is_hot_for_a_tight_projection() =>
        // 45% used at the half-way mark ⇒ ~90% projected.
        Assert.Equal(PaceTier.Hot, Window(45, 5, 9000).PaceTier(WindowKind.Session));

    [Fact]
    public void PaceTier_runs_out_for_a_projection_past_the_limit() =>
        // 60% used at the half-way mark ⇒ ~120% projected.
        Assert.Equal(PaceTier.RunsOut, Window(60, 5, 9000).PaceTier(WindowKind.Session));

    [Fact]
    public void PaceTier_is_idle_when_a_weekly_window_will_go_largely_unused() =>
        // 10% used halfway through the week ⇒ ~20% projected ⇒ paid quota left on the table.
        Assert.Equal(PaceTier.Idle, Window(10, 168, 84 * 3600).PaceTier(WindowKind.Weekly));
}
