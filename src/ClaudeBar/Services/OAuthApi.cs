using System.Net;
using System.Net.Http;
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

    public ApiException(ApiErrorKind kind, string message, int statusCode = 0) : base(message)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public static ApiException Unauthorized => new(ApiErrorKind.Unauthorized, "Token invalid. Run `claude login`.");
    public static ApiException TokenExpired => new(ApiErrorKind.TokenExpired, "Token expired — run 'claude login' in terminal");
    public static ApiException RateLimited => new(ApiErrorKind.RateLimited, "Rate limited by API.");
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
            if ((int)response.StatusCode == 429) throw ApiException.RateLimited;
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
}
