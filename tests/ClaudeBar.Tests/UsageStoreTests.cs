using ClaudeBar.ViewModels;
using Xunit;

namespace ClaudeBar.Tests;

/// <summary>The exponential rate-limit backoff progression: 6 → 12 → 24 → 48 min, capped at 60.</summary>
public class UsageStoreTests
{
    [Theory]
    [InlineData(1, 6 * 60)]    // first hit: 6 min
    [InlineData(2, 12 * 60)]   // 12 min
    [InlineData(3, 24 * 60)]   // 24 min
    [InlineData(4, 48 * 60)]   // 48 min
    [InlineData(5, 60 * 60)]   // would be 96 min → capped at 60
    [InlineData(6, 60 * 60)]   // stays at the cap
    public void RateLimitBackoff_follows_capped_exponential(int streak, double expectedSeconds) =>
        Assert.Equal(expectedSeconds, UsageStore.RateLimitBackoff(streak));

    [Fact]
    public void RateLimitBackoff_never_exceeds_the_cap()
    {
        for (var streak = 1; streak <= 20; streak++)
            Assert.True(UsageStore.RateLimitBackoff(streak) <= 60 * 60);
    }

    [Fact]
    public void RateLimitBackoff_is_monotonic_until_the_cap()
    {
        double prev = 0;
        for (var streak = 1; streak <= 10; streak++)
        {
            var cur = UsageStore.RateLimitBackoff(streak);
            Assert.True(cur >= prev);
            prev = cur;
        }
    }

    [Fact]
    public void EffectiveBackoff_without_RetryAfter_is_our_schedule() =>
        Assert.Equal(12 * 60, UsageStore.EffectiveRateLimitBackoff(2, null));

    [Fact]
    public void EffectiveBackoff_waits_for_a_longer_RetryAfter() =>
        // The real case: our schedule said 12 min, the server asked for 2539s (~42 min).
        Assert.Equal(2539, UsageStore.EffectiveRateLimitBackoff(2, TimeSpan.FromSeconds(2539)));

    [Fact]
    public void EffectiveBackoff_keeps_our_floor_over_a_shorter_RetryAfter() =>
        Assert.Equal(6 * 60, UsageStore.EffectiveRateLimitBackoff(1, TimeSpan.FromSeconds(30)));

    [Fact]
    public void EffectiveBackoff_honours_RetryAfter_above_our_cap_up_to_the_bound()
    {
        Assert.Equal(2 * 60 * 60, UsageStore.EffectiveRateLimitBackoff(6, TimeSpan.FromHours(2)));
        Assert.Equal(6 * 60 * 60, UsageStore.EffectiveRateLimitBackoff(6, TimeSpan.FromDays(3)));
    }

    // A forced refresh 10 min into a window that still has 20 min to run.
    private static readonly DateTime Now = new(2026, 10, 2, 7, 0, 0);
    private static readonly DateTime Until = Now.AddMinutes(20);

    [Fact]
    public void StretchedDeadline_keeps_the_window_without_RetryAfter() =>
        Assert.Null(UsageStore.StretchedDeadline(Now, Until, null));

    [Fact]
    public void StretchedDeadline_keeps_the_window_for_a_shorter_or_equal_RetryAfter()
    {
        Assert.Null(UsageStore.StretchedDeadline(Now, Until, TimeSpan.FromMinutes(5)));
        Assert.Null(UsageStore.StretchedDeadline(Now, Until, TimeSpan.FromMinutes(20)));
    }

    [Fact]
    public void StretchedDeadline_extends_to_a_longer_RetryAfter() =>
        Assert.Equal(Now.AddSeconds(2539), UsageStore.StretchedDeadline(Now, Until, TimeSpan.FromSeconds(2539)));

    [Fact]
    public void StretchedDeadline_bounds_a_bogus_RetryAfter() =>
        Assert.Equal(Now.AddHours(6), UsageStore.StretchedDeadline(Now, Until, TimeSpan.FromDays(3)));
}
