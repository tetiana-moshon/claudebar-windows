using System.IO;
using System.Text.Json;
using ClaudeBar.Models;

namespace ClaudeBar.Services;

public sealed class CredentialException : Exception
{
    public CredentialException(string message) : base(message) { }

    public static CredentialException NotFound =>
        new("Claude credentials not found. Run `claude login`.");
    public static CredentialException DecodingFailed =>
        new("Could not decode Claude credentials.");
}

/// <summary>
/// Reads the Claude Code CLI's OAuth access token. On macOS this lived in the login keychain; on
/// Windows the CLI writes it as plaintext JSON to <c>~/.claude/.credentials.json</c> under
/// <c>claudeAiOauth.accessToken</c>, so this is a plain file read — no keychain, no prompt.
/// Only the access token is parsed; the refresh token is deliberately ignored.
/// </summary>
public static class CredentialReader
{
    public static ClaudeCredentials ReadCredentials()
    {
        string text;
        try
        {
            text = File.ReadAllText(AppPaths.CredentialsFile);
        }
        catch
        {
            throw CredentialException.NotFound;
        }

        if (string.IsNullOrWhiteSpace(text)) throw CredentialException.NotFound;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) &&
                oauth.TryGetProperty("accessToken", out var tokenEl) &&
                tokenEl.ValueKind == JsonValueKind.String)
            {
                var token = tokenEl.GetString();
                if (!string.IsNullOrEmpty(token)) return new ClaudeCredentials(token);
            }
        }
        catch
        {
            throw CredentialException.DecodingFailed;
        }

        throw CredentialException.DecodingFailed;
    }

    /// <summary>The access token, or null when the file is missing/unreadable (never throws).</summary>
    public static string? TryReadToken()
    {
        try { return ReadCredentials().AccessToken; }
        catch { return null; }
    }
}
