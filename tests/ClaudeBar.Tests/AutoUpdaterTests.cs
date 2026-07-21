using System.IO;
using System.Security.Cryptography;
using ClaudeBar.Services;
using Xunit;

namespace ClaudeBar.Tests;

/// <summary>
/// Security-critical update logic: the host allowlist that guards where an update is fetched from,
/// the SHA-256 integrity gate, and the version comparison that decides whether to update at all.
/// </summary>
public class AutoUpdaterTests
{
    // MARK: - IsTrustedDownloadUrl (host allowlist)

    [Theory]
    [InlineData("https://github.com/o/r/releases/download/v1/ClaudeBar-1.0.0.zip")]
    [InlineData("https://github.com")]
    [InlineData("https://objects.githubusercontent.com/x/y/z")]
    [InlineData("https://release-assets.githubusercontent.com/a.zip")]
    [InlineData("https://raw.githubusercontent.com/o/r/main/a.zip")]
    [InlineData("https://GITHUB.COM/o/r/a.zip")] // scheme/host comparison is case-insensitive
    public void IsTrustedDownloadUrl_allows_github_over_https(string url) =>
        Assert.True(AutoUpdater.IsTrustedDownloadUrl(url));

    [Theory]
    [InlineData("http://github.com/o/r/a.zip")]                 // not HTTPS
    [InlineData("https://github.com.evil.com/a.zip")]           // suffix-spoof of the host
    [InlineData("https://evilgithub.com/a.zip")]                // lookalike host
    [InlineData("https://githubusercontent.com.evil.com/a.zip")]// suffix-spoof of the CDN host
    [InlineData("https://github.com@evil.com/a.zip")]           // userinfo trick — real host is evil.com
    [InlineData("https://githubXcom/a.zip")]                    // not a github host
    [InlineData("ftp://github.com/a.zip")]                      // wrong scheme
    [InlineData("https://githubusercontent.com/a.zip")]         // bare apex (assets live on subdomains)
    [InlineData("not a url")]
    [InlineData("")]
    public void IsTrustedDownloadUrl_rejects_everything_else(string url) =>
        Assert.False(AutoUpdater.IsTrustedDownloadUrl(url));

    // MARK: - VerifyDigest (integrity)

    [Fact]
    public void VerifyDigest_accepts_matching_sha256()
    {
        var (path, sha) = WriteTempWithSha("the update payload");
        try
        {
            Assert.True(AutoUpdater.VerifyDigest(path, $"sha256:{sha}", out var status));
            Assert.Contains("ok", status);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VerifyDigest_is_case_insensitive_on_hex_and_prefix()
    {
        var (path, sha) = WriteTempWithSha("payload");
        try
        {
            Assert.True(AutoUpdater.VerifyDigest(path, $"SHA256:{sha.ToUpperInvariant()}", out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VerifyDigest_rejects_mismatched_sha256()
    {
        var (path, _) = WriteTempWithSha("the real payload");
        try
        {
            var wrong = new string('a', 64);
            Assert.False(AutoUpdater.VerifyDigest(path, $"sha256:{wrong}", out var status));
            Assert.Contains("mismatch", status);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void VerifyDigest_passes_when_no_digest_published(string? digest)
    {
        var (path, _) = WriteTempWithSha("payload");
        try { Assert.True(AutoUpdater.VerifyDigest(path, digest, out _)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VerifyDigest_passes_but_notes_unsupported_algorithm()
    {
        // An unknown algorithm can't be checked here; integrity defers to the Authenticode gate.
        var (path, _) = WriteTempWithSha("payload");
        try
        {
            Assert.True(AutoUpdater.VerifyDigest(path, "md5:abcdef", out var status));
            Assert.Contains("unsupported", status);
        }
        finally { File.Delete(path); }
    }

    // MARK: - IsNewer (version comparison)

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.10.0", "1.9.0", true)]   // numeric, not lexical: 10 > 9
    [InlineData("1.0.0.1", "1.0.0", true)]  // extra segment counts
    [InlineData("1.0.0", "1.0.0", false)]   // equal
    [InlineData("1.0", "1.0.0", false)]     // equal once zero-padded
    [InlineData("1.0.0", "1.0.1", false)]   // older
    [InlineData("1.9.0", "1.10.0", false)]  // numeric: 9 < 10
    [InlineData("1.0.x", "1.0.0", false)]   // non-numeric segment parses as 0
    public void IsNewer_compares_semver_numerically(string remote, string local, bool expected) =>
        Assert.Equal(expected, AutoUpdater.IsNewer(remote, local));

    private static (string path, string sha256Hex) WriteTempWithSha(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"claudebar-test-{Guid.NewGuid():N}.bin");
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(path, bytes);
        var hex = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (path, hex);
    }
}
