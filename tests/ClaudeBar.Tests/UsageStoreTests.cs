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
}
