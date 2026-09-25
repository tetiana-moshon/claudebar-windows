using System.Collections.Generic;
using ClaudeBar.Models;
using Xunit;

namespace ClaudeBar.Tests;

/// <summary>Mapping the raw usage-endpoint payload into the snapshot the app reasons about.</summary>
public class UsageSnapshotTests
{
    private static OAuthWindow Win(double util, string? resetsAt = "2999-01-01T00:00:00Z") =>
        new() { Utilization = util, ResetsAt = resetsAt };

    [Fact]
    public void From_returns_null_without_a_five_hour_window()
    {
        var snap = UsageSnapshot.From(new OAuthUsageResponse { FiveHour = null, SevenDay = Win(30) });
        Assert.Null(snap);
    }

    [Fact]
    public void From_maps_the_five_hour_session_window()
    {
        var snap = UsageSnapshot.From(new OAuthUsageResponse { FiveHour = Win(42) });
        Assert.NotNull(snap);
        Assert.Equal(42, snap!.Session.UsedPercent);
        Assert.Equal(5, snap.Session.WindowDuration.TotalHours);
        Assert.NotNull(snap.Session.ResetsAt);
        Assert.Null(snap.Weekly);
        Assert.Null(snap.ScopedWeekly);
        Assert.Null(snap.ScopedModelName);
    }

    [Fact]
    public void From_maps_the_seven_day_weekly_window()
    {
        var snap = UsageSnapshot.From(new OAuthUsageResponse { FiveHour = Win(10), SevenDay = Win(30) });
        Assert.NotNull(snap!.Weekly);
        Assert.Equal(30, snap.Weekly!.UsedPercent);
        Assert.Equal(168, snap.Weekly.WindowDuration.TotalHours);
    }

    [Fact]
    public void From_maps_the_scoped_weekly_limit_and_model_name()
    {
        var snap = UsageSnapshot.From(new OAuthUsageResponse
        {
            FiveHour = Win(10),
            Limits = new List<OAuthLimit>
            {
                new()
                {
                    Kind = "weekly_scoped",
                    Percent = 70,
                    ResetsAt = "2999-01-01T00:00:00Z",
                    Scope = new OAuthScope { Model = new OAuthModel { DisplayName = "Opus" } }
                }
            }
        });
        Assert.NotNull(snap!.ScopedWeekly);
        Assert.Equal(70, snap.ScopedWeekly!.UsedPercent);
        Assert.Equal(168, snap.ScopedWeekly.WindowDuration.TotalHours);
        Assert.Equal("Opus", snap.ScopedModelName);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(null)]
    public void From_drops_a_scoped_limit_with_no_usage(double? percent)
    {
        // A 0% (or absent-percent) weekly_scoped entry is a metered-but-unused model — nothing to
        // show — so the window and its model name are suppressed until real usage lands.
        var snap = UsageSnapshot.From(new OAuthUsageResponse
        {
            FiveHour = Win(10),
            Limits = new List<OAuthLimit>
            {
                new()
                {
                    Kind = "weekly_scoped",
                    Percent = percent,
                    ResetsAt = "2999-01-01T00:00:00Z",
                    Scope = new OAuthScope { Model = new OAuthModel { DisplayName = "Fable" } }
                }
            }
        });
        Assert.Null(snap!.ScopedWeekly);
        Assert.Null(snap.ScopedModelName);
    }

    [Fact]
    public void From_ignores_limits_that_are_not_weekly_scoped()
    {
        var snap = UsageSnapshot.From(new OAuthUsageResponse
        {
            FiveHour = Win(10),
            Limits = new List<OAuthLimit> { new() { Kind = "something_else", Percent = 70 } }
        });
        Assert.Null(snap!.ScopedWeekly);
        Assert.Null(snap.ScopedModelName);
    }

    [Fact]
    public void From_leaves_reset_null_for_an_unparseable_date()
    {
        var snap = UsageSnapshot.From(new OAuthUsageResponse { FiveHour = Win(10, "not-a-date") });
        Assert.NotNull(snap);
        Assert.Null(snap!.Session.ResetsAt);
    }
}
