using ClaudeBar.Models;
using ClaudeBar.Services;
using Xunit;

namespace ClaudeBar.Tests;

/// <summary>
/// The recommendation engine end-to-end, over representative snapshots. All windows are built
/// relative to "now" so the forecasting math (elapsed/remaining/burn-rate) is deterministic, and
/// an empty <see cref="ActivityProfile"/> disables off-hours softening so verdicts don't depend on
/// the wall-clock hour.
/// </summary>
public class RecommenderTests
{
    // used% + how much of the window is left → a window whose elapsed/remaining are pinned to now.
    private static RateWindow Window(double usedPercent, double windowHours, double remainingSeconds)
    {
        var resetsAt = DateTime.Now.AddSeconds(remainingSeconds);
        return new RateWindow(usedPercent, TimeSpan.FromHours(windowHours), resetsAt);
    }

    private static Recommendation Recommend(RateWindow session, RateWindow? weekly = null,
        RateWindow? scoped = null, string? model = null) =>
        new Recommender(new UsageSnapshot(session, weekly, scoped, model), ActivityProfile.Empty).Recommend();

    [Fact]
    public void Ready_when_everything_is_on_pace()
    {
        // 20% used, half the 5h window gone → projects ~40% at reset: comfortably low.
        var rec = Recommend(Window(20, 5, 9000));
        Assert.Equal(Urgency.Low, rec.Urgency);
        Assert.Equal("Ready — run large tasks", rec.Headline);
        Assert.False(rec.IsHeadroom);
        Assert.Equal("ok", rec.StatusSymbol);
    }

    [Fact]
    public void Stop_when_session_is_exhausted()
    {
        // 92% used but only ~100s elapsed → no trustworthy forecast (Early), so it's judged on the
        // absolute floor: <10% left with a distant reset ⇒ Critical.
        var rec = Recommend(Window(92, 5, 5 * 3600 - 100));
        Assert.Equal(Urgency.Critical, rec.Urgency);
        Assert.Equal("Stop — limit exhausted", rec.Headline);
        Assert.Equal("stop", rec.StatusSymbol);
    }

    [Fact]
    public void EaseOff_when_session_pace_is_high_but_survives()
    {
        // 45% used at the half-way mark → projects ~90% at reset: high pace, not exhaustion.
        var rec = Recommend(Window(45, 5, 9000));
        Assert.Equal(Urgency.Medium, rec.Urgency);
        Assert.StartsWith("Ease off for", rec.Headline);
        Assert.Equal("warn", rec.StatusSymbol);
    }

    [Fact]
    public void Headroom_when_weekly_will_go_largely_unused()
    {
        var session = Window(20, 5, 9000);          // Low
        var weekly = Window(10, 168, 84 * 3600);    // ~20% projected over a week ⇒ Idle pace
        var rec = Recommend(session, weekly);
        Assert.Equal(Urgency.Low, rec.Urgency);
        Assert.True(rec.IsHeadroom);
        Assert.StartsWith("Headroom", rec.Headline);
        Assert.Equal("headroom", rec.StatusSymbol);
    }

    [Fact]
    public void Scoped_model_line_appears_when_that_weekly_cap_is_tight()
    {
        var session = Window(20, 5, 9000);              // Low
        var scoped = Window(85, 168, 84 * 3600);        // 15% left ⇒ Medium model line
        var rec = Recommend(session, weekly: null, scoped: scoped, model: "Opus");
        Assert.NotNull(rec.ModelLine);
        Assert.Contains("Opus", rec.ModelLine!);
    }
}
