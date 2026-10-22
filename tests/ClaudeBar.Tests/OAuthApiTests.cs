using System.Net;
using System.Net.Http;
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
    public void ParseRetryAfter_treats_a_zero_wait_as_nothing_to_wait_for()
    {
        Assert.Null(OAuthApi.ParseRetryAfter(new RetryConditionHeaderValue(TimeSpan.Zero), Now));
        Assert.Null(OAuthApi.ParseRetryAfter(new RetryConditionHeaderValue(Now), Now));
    }

    [Fact]
    public void A_malformed_header_reads_as_absent_rather_than_throwing()
    {
        // If the typed getter threw, the 429 would surface as a network error and skip the backoff.
        using var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.TryAddWithoutValidation("Retry-After", "soon");
        Assert.Null(OAuthApi.ParseRetryAfter(response.Headers.RetryAfter, Now));
    }

    [Fact]
    public void ParseRetryAfter_is_null_without_the_header() =>
        Assert.Null(OAuthApi.ParseRetryAfter(null, Now));
}
