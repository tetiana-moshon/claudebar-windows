using System.Net.Http.Headers;
using ClaudeBar.Services;
using Xunit;

namespace ClaudeBar.Tests;

/// <summary>Parsing the 429's <c>Retry-After</c>, in both of its wire forms.</summary>
public class OAuthApiTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseRetryAfter_reads_delta_seconds() =>
        Assert.Equal(TimeSpan.FromSeconds(2539),
            OAuthApi.ParseRetryAfter(new RetryConditionHeaderValue(TimeSpan.FromSeconds(2539)), Now));

    [Fact]
    public void ParseRetryAfter_reads_an_http_date_relative_to_now() =>
        Assert.Equal(TimeSpan.FromMinutes(10),
            OAuthApi.ParseRetryAfter(new RetryConditionHeaderValue(Now.AddMinutes(10)), Now));

    [Fact]
    public void ParseRetryAfter_ignores_a_date_in_the_past() =>
        Assert.Null(OAuthApi.ParseRetryAfter(new RetryConditionHeaderValue(Now.AddMinutes(-1)), Now));

    [Fact]
    public void ParseRetryAfter_is_null_without_the_header() =>
        Assert.Null(OAuthApi.ParseRetryAfter(null, Now));
}
