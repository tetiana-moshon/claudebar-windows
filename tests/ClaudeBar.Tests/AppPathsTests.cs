using System;
using System.IO;
using ClaudeBar.Services;
using Xunit;

namespace ClaudeBar.Tests;

/// <summary>
/// Which Claude desktop directories the token reader tries, exercised through the internal
/// <c>DesktopDirCandidates</c> seam with fake roots. A wrong pick here silently drops the desktop
/// token and falls back to the CLI one — the days-long 429 lockout — with the suite still green.
/// </summary>
public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claudebar-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _appData;
    private readonly string _localAppData;
    private readonly string _classic;

    public AppPathsTests()
    {
        _appData = Path.Combine(_root, "Roaming");
        _localAppData = Path.Combine(_root, "Local");
        _classic = Path.Combine(_appData, "Claude");
        Directory.CreateDirectory(_appData);
        Directory.CreateDirectory(Path.Combine(_localAppData, "Packages"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static void WriteDesktopDir(string dir, DateTime configMtimeUtc, bool withLocalState = true)
    {
        Directory.CreateDirectory(dir);
        var config = Path.Combine(dir, AppPaths.DesktopConfigFileName);
        File.WriteAllText(config, "{}");
        File.SetLastWriteTimeUtc(config, configMtimeUtc);
        if (withLocalState) File.WriteAllText(Path.Combine(dir, AppPaths.DesktopLocalStateFileName), "{}");
    }

    private string Package(string name) =>
        Path.Combine(_localAppData, "Packages", name, "LocalCache", "Roaming", "Claude");

    private string[] Candidates() =>
        [.. AppPaths.DesktopDirCandidates(_appData, _localAppData)];

    [Fact]
    public void Installer_folder_only_is_returned()
    {
        WriteDesktopDir(_classic, DateTime.UtcNow);
        Assert.Equal([_classic], Candidates());
    }

    [Fact]
    public void Store_folder_only_is_returned()
    {
        var msix = Package("Claude_pzs8sxrjxfjjc");
        WriteDesktopDir(msix, DateTime.UtcNow);
        Assert.Equal([msix], Candidates());
    }

    [Fact]
    public void Stale_installer_folder_ranks_below_a_fresher_Store_folder()
    {
        // Moved from the installer build to the Store build: %APPDATA%\Claude stays behind, frozen.
        var msix = Package("Claude_pzs8sxrjxfjjc");
        WriteDesktopDir(_classic, DateTime.UtcNow.AddDays(-30));
        WriteDesktopDir(msix, DateTime.UtcNow);
        Assert.Equal([msix, _classic], Candidates());
    }

    [Fact]
    public void Fresher_installer_folder_ranks_above_a_stale_Store_folder()
    {
        var msix = Package("Claude_pzs8sxrjxfjjc");
        WriteDesktopDir(msix, DateTime.UtcNow.AddDays(-30));
        WriteDesktopDir(_classic, DateTime.UtcNow);
        Assert.Equal([_classic, msix], Candidates());
    }

    [Fact]
    public void Two_Store_packages_are_ordered_newest_first()
    {
        var newer = Package("Claude_aaaaaaaaaaaaa");
        var older = Package("Claude_zzzzzzzzzzzzz");
        // Create the newer one first so enumeration order cannot produce the expected result.
        WriteDesktopDir(newer, DateTime.UtcNow);
        WriteDesktopDir(older, DateTime.UtcNow.AddHours(-5));
        Assert.Equal([newer, older], Candidates());
    }

    [Fact]
    public void Folder_without_config_json_is_skipped()
    {
        var empty = Package("Claude_empty");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, AppPaths.DesktopLocalStateFileName), "{}");
        var live = Package("Claude_live");
        WriteDesktopDir(live, DateTime.UtcNow);
        Assert.Equal([live], Candidates());
    }

    [Fact]
    public void Folder_without_Local_State_is_skipped()
    {
        // Its cache could never be decrypted, so it must not shadow a complete folder.
        var keyless = Package("Claude_keyless");
        WriteDesktopDir(keyless, DateTime.UtcNow, withLocalState: false);
        WriteDesktopDir(_classic, DateTime.UtcNow.AddDays(-1));
        Assert.Equal([_classic], Candidates());
    }

    [Fact]
    public void Non_matching_package_folder_is_ignored()
    {
        WriteDesktopDir(Path.Combine(_localAppData, "Packages", "Other_x", "LocalCache", "Roaming", "Claude"), DateTime.UtcNow);
        Assert.Empty(Candidates());
    }

    [Fact]
    public void Missing_Packages_folder_falls_back_to_the_installer_folder()
    {
        Directory.Delete(Path.Combine(_localAppData, "Packages"));
        WriteDesktopDir(_classic, DateTime.UtcNow);
        Assert.Equal([_classic], Candidates());
    }

    [Fact]
    public void Nothing_anywhere_yields_no_candidates() =>
        Assert.Empty(Candidates());
}
