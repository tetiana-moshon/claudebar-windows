using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeBar.Models;
using Xunit;

namespace ClaudeBar.Tests;

/// <summary>The active/off-hours split derived from the daily prompt rhythm — the signal that lets
/// the Recommender stop raising alarms about deadlines landing while the user is away.</summary>
public class ActivityProfileTests
{
    private static ActivityProfile Profile(int[] hourCounts, int total = 200, int days = 7)
    {
        var weekday = Enumerable.Range(0, 7).Select(_ => (IReadOnlyList<int>)new int[24]).ToArray();
        return new ActivityProfile(hourCounts, weekday, total, days,
            DateTime.Now.AddDays(-30), DateTime.Now);
    }

    private static int[] HoursWith(int value, params int[] hours)
    {
        var a = new int[24];
        foreach (var h in hours) a[h] = value;
        return a;
    }

    [Theory]
    [InlineData(40, 5, true)]
    [InlineData(39, 5, false)]  // too few prompts
    [InlineData(40, 4, false)]  // too few distinct days
    public void HasEnoughData_needs_both_thresholds(int total, int days, bool expected) =>
        Assert.Equal(expected, Profile(HoursWith(10, 9, 12, 15), total, days).HasEnoughData);

    [Fact]
    public void OffHours_is_empty_without_enough_data()
    {
        // A clear 9–17 rhythm, but too little history to trust it.
        var p = Profile(HoursWith(10, 9, 10, 11, 12, 13, 14, 15, 16, 17), total: 39, days: 5);
        Assert.Empty(p.OffHours);
        Assert.False(p.IsDeadTime(new DateTime(2030, 1, 1, 3, 0, 0)));
    }

    [Fact]
    public void IsActive_uses_the_fifteen_percent_threshold()
    {
        var a = HoursWith(10, 9, 10, 11, 12, 13, 14, 15, 16, 17); // reference busy level = 10
        a[2] = 1;  // 10% of reference ⇒ below threshold ⇒ inactive
        a[3] = 2;  // 20% of reference ⇒ active
        var p = Profile(a);
        Assert.True(p.IsActive(10));
        Assert.True(p.IsActive(3));
        Assert.False(p.IsActive(2));
        Assert.False(p.IsActive(0));
    }

    [Fact]
    public void ReferenceLevel_ignores_a_single_outlier_hour()
    {
        // One burst of 100 at 3am must not push the genuine 9–17 working hours below threshold —
        // the reference is the second-highest hour (10), not the max (100).
        var a = HoursWith(10, 9, 10, 11, 12, 13, 14, 15, 16, 17);
        a[3] = 100;
        var p = Profile(a);
        Assert.True(p.IsActive(9));
        Assert.True(p.IsActive(3));
    }

    [Fact]
    public void OffHours_spans_a_run_that_wraps_past_midnight()
    {
        // Active 9–17; the inactive stretch 18:00→09:00 wraps midnight as one contiguous run.
        var p = Profile(HoursWith(10, 9, 10, 11, 12, 13, 14, 15, 16, 17));
        foreach (var h in new[] { 18, 20, 23, 0, 3, 8 }) Assert.Contains(h, p.OffHours);
        foreach (var h in new[] { 9, 12, 17 }) Assert.DoesNotContain(h, p.OffHours);

        Assert.True(p.IsDeadTime(new DateTime(2030, 1, 1, 3, 0, 0)));
        Assert.False(p.IsDeadTime(new DateTime(2030, 1, 1, 12, 0, 0)));
        Assert.Equal("18:00–9:00", p.DeadRangeDescription);
    }

    [Fact]
    public void A_short_gap_is_not_treated_as_off_hours()
    {
        // Busy all day except a 2-hour lull — shorter than the 3-hour minimum run.
        var a = Enumerable.Repeat(10, 24).ToArray();
        a[12] = 0;
        a[13] = 0;
        var p = Profile(a);
        Assert.Empty(p.OffHours);
        Assert.Null(p.DeadRangeDescription);
    }
}
