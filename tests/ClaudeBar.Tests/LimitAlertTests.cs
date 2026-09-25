using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeBar.Models;
using ClaudeBar.Services;
using Xunit;

namespace ClaudeBar.Tests;

/// <summary>The rules behind the focus-stealing "limit almost gone" dialog: which windows qualify,
/// the session's imminent-reset carve-out, and which crossings are new enough to interrupt for.</summary>
public class LimitAlertTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0);

    private static RateWindow Window(double remaining, TimeSpan? resetIn)
        => new(100 - remaining, TimeSpan.FromHours(5), resetIn is { } r ? Now + r : null);

    private static UsageSnapshot Snap(RateWindow session, RateWindow? weekly = null,
        RateWindow? scoped = null, string? model = null)
        => new(session, weekly, scoped, model);

    [Fact]
    public void Session_under_ten_percent_with_a_far_reset_qualifies()
    {
        var q = LimitAlert.QualifyingEntries(Snap(Window(8, TimeSpan.FromHours(2))), Now);
        Assert.Single(q);
        Assert.Equal(LimitScope.Session, q[0].Scope);
    }

    [Fact]
    public void Session_within_fifteen_minutes_of_reset_is_skipped()
    {
        // About to fix itself — the dialog honors the same softening rule as the Recommender.
        var q = LimitAlert.QualifyingEntries(Snap(Window(3, TimeSpan.FromMinutes(10))), Now);
        Assert.Empty(q);
    }

    [Fact]
    public void Session_at_or_above_ten_percent_does_not_qualify()
        => Assert.Empty(LimitAlert.QualifyingEntries(Snap(Window(10, TimeSpan.FromHours(2))), Now));

    [Fact]
    public void Weekly_uses_the_five_percent_threshold()
    {
        var healthy = Window(80, TimeSpan.FromHours(2));
        Assert.Empty(LimitAlert.QualifyingEntries(Snap(healthy, weekly: Window(7, TimeSpan.FromHours(20))), Now));

        var q = LimitAlert.QualifyingEntries(Snap(healthy, weekly: Window(4, TimeSpan.FromHours(20))), Now);
        Assert.Single(q);
        Assert.Equal(LimitScope.Weekly, q[0].Scope);
    }

    [Fact]
    public void Scoped_weekly_qualifies_with_the_model_name_in_the_title()
    {
        var q = LimitAlert.QualifyingEntries(
            Snap(Window(80, TimeSpan.FromHours(2)), scoped: Window(2, TimeSpan.FromHours(20)), model: "Opus"), Now);
        Assert.Single(q);
        Assert.Equal(LimitScope.ScopedWeekly, q[0].Scope);
        Assert.Equal("Weekly Opus limit", q[0].Title);
    }

    [Fact]
    public void A_window_without_a_reset_time_is_skipped()
        => Assert.Empty(LimitAlert.QualifyingEntries(Snap(Window(3, resetIn: null)), Now));

    [Fact]
    public void NewlyCrossed_excludes_a_scope_suppressed_into_the_future()
    {
        var q = LimitAlert.QualifyingEntries(Snap(Window(8, TimeSpan.FromHours(2))), Now);

        Assert.Empty(LimitAlert.NewlyCrossed(q, _ => Now.AddMinutes(10), Now));      // still snoozed
        Assert.Single(LimitAlert.NewlyCrossed(q, _ => Now.AddMinutes(-1), Now));     // snooze elapsed
        Assert.Single(LimitAlert.NewlyCrossed(q, _ => null, Now));                   // never suppressed
    }
}
