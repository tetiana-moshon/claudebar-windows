using System.Collections.Generic;
using System.Text.Json;
using ClaudeBar.Services;
using Xunit;

namespace ClaudeBar.Tests;

/// <summary>Which desktop-cache token gets tried, and in what order — the logic split out from the
/// os_crypt decryption. Keys mirror the real "&lt;clientId&gt;:&lt;org&gt;:&lt;audience&gt;:&lt;scopes&gt;" shape.</summary>
public class DesktopTokenReaderTests
{
    private const long Now = 1_000_000;

    private static JsonElement Entry(string token, double? expiresAt)
    {
        var exp = expiresAt is { } e ? e.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
        return JsonDocument.Parse($$"""{"token":"{{token}}","expiresAt":{{exp}}}""").RootElement;
    }

    private static string Key(string scopes) => $"{DesktopTokenReader.ClientId}:org-x:aud-y:{scopes}";

    [Fact]
    public void Skips_entries_for_other_clients()
    {
        var cache = new Dictionary<string, JsonElement>
        {
            [$"other-client:org:aud:user:inference"] = Entry("nope", Now + 10_000),
        };
        Assert.Empty(DesktopTokenReader.SelectUsableTokens(cache, Now));
    }

    [Fact]
    public void Skips_entries_without_the_inference_scope()
    {
        var cache = new Dictionary<string, JsonElement>
        {
            [Key("user:profile")] = Entry("nope", Now + 10_000),
        };
        Assert.Empty(DesktopTokenReader.SelectUsableTokens(cache, Now));
    }

    [Fact]
    public void Skips_expired_tokens_but_keeps_unknown_expiry()
    {
        var cache = new Dictionary<string, JsonElement>
        {
            [Key("user:inference:a")] = Entry("expired", Now - 1),
            [Key("user:inference:b")] = Entry("no-expiry", null), // exp 0 ⇒ treated as usable
        };
        var tokens = DesktopTokenReader.SelectUsableTokens(cache, Now);
        Assert.Equal(new[] { "no-expiry" }, tokens);
    }

    [Fact]
    public void Skips_entries_with_a_missing_or_empty_token()
    {
        var cache = new Dictionary<string, JsonElement>
        {
            [Key("user:inference:a")] = Entry("", Now + 10_000),
            [Key("user:inference:b")] = JsonDocument.Parse($$"""{"expiresAt":{{Now + 10_000}}}""").RootElement,
        };
        Assert.Empty(DesktopTokenReader.SelectUsableTokens(cache, Now));
    }

    [Fact]
    public void Orders_by_latest_expiry_first_regardless_of_scope()
    {
        // The claude_code-scoped token expires sooner than a broader inference token; the broader,
        // later-expiring one must lead — a re-login revokes the claude_code one first.
        var cache = new Dictionary<string, JsonElement>
        {
            [Key("user:inference:claude_code")] = Entry("session-scoped", Now + 1_000),
            [Key("user:inference:broad")] = Entry("broad", Now + 9_000),
        };
        var tokens = DesktopTokenReader.SelectUsableTokens(cache, Now);
        Assert.Equal(new[] { "broad", "session-scoped" }, tokens);
    }
}
