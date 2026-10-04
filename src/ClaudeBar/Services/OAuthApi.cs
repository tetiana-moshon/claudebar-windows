using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ClaudeBar.Models;

namespace ClaudeBar.Services;

public enum ApiErrorKind
{
    Unauthorized,
    TokenExpired,
    NetworkError,
    DecodingError,
    HttpError,
    RateLimited
}

public sealed class ApiException : Exception
{
    public ApiErrorKind Kind { get; }
    public int StatusCode { get; }

    /// <summary>How long a 429 asked us to wait (its <c>Retry-After</c> header), if it said.</summary>
    public TimeSpan? RetryAfter { get; }

    public ApiException(ApiErrorKind kind, string message, int statusCode = 0, TimeSpan? retryAfter = null)
        : base(message)
    {
        Kind = kind;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public static ApiException Unauthorized => new(ApiErrorKind.Unauthorized, "Token invalid. Run `claude login`.");
    public static ApiException TokenExpired => new(ApiErrorKind.TokenExpired, "Token expired — run 'claude login' in terminal");
    public static ApiException RateLimited(TimeSpan? retryAfter = null) =>
        new(ApiErrorKind.RateLimited, "Rate limited by API.", 429, retryAfter);
    public static ApiException Decoding => new(ApiErrorKind.DecodingError, "Could not parse response.");
    public static ApiException Http(int code) => new(ApiErrorKind.HttpError, $"HTTP error {code}", code);
    public static ApiException Network(Exception e) => new(ApiErrorKind.NetworkError, $"Network error: {e.Message}");
}

public static class OAuthApi
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string BetaHeader = "oauth-2025-04-20";

    // One shared HttpClient for the process lifetime (avoids socket exhaustion).
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<OAuthUsageResponse> FetchUsageAsync(string accessToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("anthropic-beta", BetaHeader);
            // Identify as the CLI: the usage endpoint is gated to the official client's User-Agent.
            request.Headers.TryAddWithoutValidation("User-Agent", "claude-code/2.1.152");

            using var response = await Http.SendAsync(request).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized) throw ApiException.TokenExpired;
            if ((int)response.StatusCode == 429)
                throw ApiException.RateLimited(ParseRetryAfter(response.Headers.RetryAfter, DateTimeOffset.UtcNow));
            if (response.StatusCode != HttpStatusCode.OK) throw ApiException.Http((int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<OAuthUsageResponse>(body);
            if (result is null) throw ApiException.Decoding;
            return result;
        }
        catch (ApiException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw ApiException.Decoding;
        }
        catch (Exception e)
        {
            throw ApiException.Network(e);
        }
    }

    /// <summary>
    /// <c>Retry-After</c> comes either as delta-seconds or as an HTTP date. Null when absent, or
    /// when the date is already in the past — there is then nothing extra to wait for.
    /// </summary>
    internal static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        if (header is null) return null;
        var wait = header.Delta ?? (header.Date is { } date ? date - now : null);
        return wait is { } w && w > TimeSpan.Zero ? w : null;
    }
}
