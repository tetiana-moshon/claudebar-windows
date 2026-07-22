using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeBar.Services;

/// <summary>
/// Reads the OAuth access token the Claude **desktop app** keeps continuously fresh, so
/// ClaudeBar never has to refresh the (aggressively rate-limited) OAuth token endpoint itself —
/// the source of the old daily-429 lockups.
///
/// The desktop app (Electron) caches its tokens in <c>config.json</c> under the
/// keys <c>oauth:tokenCache</c> and <c>oauth:tokenCacheV2</c> (both are read and merged — the
/// desktop app has been observed moving which key holds the live client-9d1c250a entry across
/// releases), each a Chromium <c>os_crypt</c> "v10" blob.
///
/// On Windows the blob is: "v10" (3 bytes) + 12-byte GCM nonce + ciphertext + 16-byte GCM tag,
/// AES-256-GCM. The key is the app's random os_crypt key stored — DPAPI-wrapped, with a 5-byte
/// "DPAPI" prefix — in <c>%APPDATA%\Claude\Local State</c> under <c>os_crypt.encrypted_key</c>.
/// (This differs from macOS, where the key is PBKDF2-derived from a keychain secret and the
/// cipher is AES-128-CBC with a fixed IV.)
///
/// config.json and "Local State" live in whichever directory <see cref="AppPaths.DesktopDirCandidates"/>
/// resolves — the classic <c>%APPDATA%\Claude</c> or the Store/MSIX package container.
///
/// The decrypted JSON is keyed <c>"&lt;clientId&gt;:&lt;org&gt;:&lt;audience&gt;:&lt;scopes&gt;"</c>
/// → <c>{ token, refreshToken, expiresAt, … }</c>. The desktop app caches several tokens for
/// client 9d1c250a under different scope keys; we return all usable ones (freshest first) so the
/// caller can try each, because the usage endpoint accepts whichever the server still honours —
/// and that is NOT always the <c>user:sessions:claude_code</c> one. An empty result lets the
/// caller fall back to the CLI credentials token — the format is undocumented and may change.
/// </summary>
public static class DesktopTokenReader
{
    internal const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    /// <summary>
    /// The desktop app has been observed migrating its live tokens from <c>oauth:tokenCache</c>
    /// to a newer <c>oauth:tokenCacheV2</c> key while leaving the old key holding only stale
    /// entries — so both must be decrypted and merged.
    /// </summary>
    private static readonly string[] CacheKeys = { "oauth:tokenCache", "oauth:tokenCacheV2" };

    /// <summary>
    /// Every usable access token from the desktop cache, freshest expiry first, for the caller to
    /// try in order. Empty if the desktop store is absent/unreadable or holds no currently-valid
    /// token.
    /// </summary>
    public static IReadOnlyList<string> CurrentTokens()
    {
        // Probe every install shape (classic + Store/MSIX) and merge their caches; realistically
        // only one exists, but merging lets SelectUsableTokens pick the freshest token across all.
        var merged = new Dictionary<string, JsonElement>();
        foreach (var dir in AppPaths.DesktopDirCandidates())
        {
            var cache = DecryptedTokenCache(dir);
            if (cache is null) continue;
            foreach (var (name, value) in cache) merged[name] = value;
        }
        if (merged.Count == 0) return Array.Empty<string>();
        return SelectUsableTokens(merged, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// The token-selection logic, split out from the decryption so it can be tested directly:
    /// keep client-9d1c250a <c>user:inference</c> entries that carry a non-empty, still-valid token
    /// (exp unknown = 0 is treated as usable — the server is the final authority via a 401), and
    /// order by latest expiry, since a later-expiring token is the more likely to still be live.
    /// We deliberately do NOT prefer the claude_code session scope — that is exactly the token a
    /// re-login revokes first, while a broader token the desktop keeps refreshing still returns 200.
    /// </summary>
    internal static IReadOnlyList<string> SelectUsableTokens(
        IReadOnlyDictionary<string, JsonElement> cache, long nowMillis)
    {
        var candidates = new List<(string token, double exp)>();

        foreach (var (key, value) in cache)
        {
            if (!key.Contains(ClientId) || !key.Contains("user:inference")) continue;
            if (value.ValueKind != JsonValueKind.Object) continue;
            if (!value.TryGetProperty("token", out var tokenEl) ||
                tokenEl.ValueKind != JsonValueKind.String) continue;
            var token = tokenEl.GetString();
            if (string.IsNullOrEmpty(token)) continue;

            double exp = 0;
            if (value.TryGetProperty("expiresAt", out var expEl) &&
                expEl.ValueKind == JsonValueKind.Number)
                exp = expEl.GetDouble();

            candidates.Add((token, exp));
        }

        return candidates
            .Where(c => c.exp == 0 || c.exp > nowMillis)
            .OrderByDescending(c => c.exp)
            .Select(c => c.token)
            .ToArray();
    }

    private static Dictionary<string, JsonElement>? DecryptedTokenCache(string dir)
    {
        var key = DeriveKey(dir);
        if (key is null) return null;

        JsonDocument root;
        try
        {
            root = JsonDocument.Parse(File.ReadAllText(AppPaths.DesktopConfigFile(dir)));
        }
        catch
        {
            return null;
        }

        using (root)
        {
            var merged = new Dictionary<string, JsonElement>();
            foreach (var cacheKey in CacheKeys)
            {
                if (!root.RootElement.TryGetProperty(cacheKey, out var blobEl) ||
                    blobEl.ValueKind != JsonValueKind.String)
                    continue;
                var b64 = blobEl.GetString();
                if (string.IsNullOrEmpty(b64)) continue;

                byte[] blob;
                try { blob = Convert.FromBase64String(b64); }
                catch { continue; }
                if (blob.Length <= 3) continue;

                var plaintext = DecryptV10(blob, key);
                if (plaintext is null) continue;

                try
                {
                    // Parse and clone each entry so it outlives this JsonDocument's disposal.
                    using var doc = JsonDocument.Parse(plaintext);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        merged[prop.Name] = prop.Value.Clone();
                }
                catch
                {
                    // ignore an unparsable blob
                }
            }
            return merged.Count == 0 ? null : merged;
        }
    }

    /// <summary>
    /// The AES-256 key: read <c>os_crypt.encrypted_key</c> from Local State, base64-decode, strip
    /// the 5-byte "DPAPI" prefix, and DPAPI-unprotect it for the current user.
    /// </summary>
    private static byte[]? DeriveKey(string dir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.DesktopLocalStateFile(dir)));
            if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt) ||
                !osCrypt.TryGetProperty("encrypted_key", out var keyEl) ||
                keyEl.ValueKind != JsonValueKind.String)
                return null;

            var encrypted = Convert.FromBase64String(keyEl.GetString()!);
            const string prefix = "DPAPI";
            if (encrypted.Length <= prefix.Length) return null;
            if (Encoding.ASCII.GetString(encrypted, 0, prefix.Length) != prefix) return null;

            var dpapiBlob = encrypted[prefix.Length..];
            return ProtectedData.Unprotect(dpapiBlob, null, DataProtectionScope.CurrentUser);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Decrypt a Chromium "v10" AES-256-GCM blob: v10 | 12-byte nonce | ciphertext | 16-byte tag.</summary>
    private static byte[]? DecryptV10(byte[] blob, byte[] key)
    {
        const int nonceLen = 12;
        const int tagLen = 16;
        if (blob.Length < 3 + nonceLen + tagLen) return null;

        try
        {
            var nonce = blob[3..(3 + nonceLen)];
            var cipherAndTag = blob[(3 + nonceLen)..];
            var tag = cipherAndTag[^tagLen..];
            var cipher = cipherAndTag[..^tagLen];
            var plaintext = new byte[cipher.Length];

            using var gcm = new AesGcm(key, tagLen);
            gcm.Decrypt(nonce, cipher, tag, plaintext);
            return plaintext;
        }
        catch
        {
            return null;
        }
    }
}
