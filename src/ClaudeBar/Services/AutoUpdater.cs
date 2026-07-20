using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;

namespace ClaudeBar.Services;

/// <summary>
/// Optional auto-update from this repo's GitHub releases. A faithful port of the macOS updater,
/// adapted for Windows: the release asset is a .zip containing <c>ClaudeBar.exe</c>, and because
/// Windows cannot overwrite a running executable, the swap+relaunch is handed to a short-lived
/// detached cmd script that waits for this process to exit first.
/// </summary>
public sealed class AutoUpdater : INotifyPropertyChanged
{
    /// <summary>
    /// The running build's version — the single source of truth is the assembly stamped from the
    /// csproj &lt;Version&gt;, so it can never drift from what publish.ps1 ships. The
    /// AssemblyInformationalVersion can carry a "+&lt;gitsha&gt;" SourceLink suffix, which we strip
    /// so <see cref="IsNewer"/> only ever compares the numeric semver.
    /// </summary>
    public static readonly string CurrentVersion = ResolveVersion();
    public const string AutoUpdateKey = "autoUpdate";

    private static string ResolveVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info)) return info.Split('+')[0];
        var ver = asm.GetName().Version;
        return ver is null ? "0.0.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
    }

    private const string Repo = "tetiana-moshon/claudebar-windows";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public event PropertyChangedEventHandler? PropertyChanged;

    private string? _latestVersion;
    public string? LatestVersion { get => _latestVersion; private set => Set(ref _latestVersion, value); }

    private bool _updateAvailable;
    public bool UpdateAvailable { get => _updateAvailable; private set => Set(ref _updateAvailable, value); }

    private bool _isChecking;
    public bool IsChecking { get => _isChecking; private set => Set(ref _isChecking, value); }

    private bool _isUpdating;
    public bool IsUpdating { get => _isUpdating; private set => Set(ref _isUpdating, value); }

    private string? _error;
    public string? Error { get => _error; private set => Set(ref _error, value); }

    private string? _progress;
    public string? Progress { get => _progress; private set => Set(ref _progress, value); }

    private DispatcherTimer? _timer;

    public AutoUpdater()
    {
        var auto = Settings.GetBool(AutoUpdateKey, defaultValue: true);
        if (auto) StartPeriodicCheck();
    }

    public async Task CheckForUpdatesAsync(bool autoInstall = false)
    {
        if (IsChecking) return;
        IsChecking = true;
        Error = null;
        try
        {
            var release = await FetchLatestReleaseAsync().ConfigureAwait(true);
            var remote = release.TagName.StartsWith("v") ? release.TagName[1..] : release.TagName;
            LatestVersion = remote;
            UpdateAvailable = IsNewer(remote, CurrentVersion);
            if (UpdateAvailable && autoInstall) await PerformUpdateAsync().ConfigureAwait(true);
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    public async Task PerformUpdateAsync()
    {
        if (IsUpdating) return;
        IsUpdating = true;
        Error = null;
        try
        {
            var release = await FetchLatestReleaseAsync().ConfigureAwait(true);
            var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (asset is null) throw new Exception("No downloadable asset in release");

            Progress = "Downloading…";
            var zipPath = await DownloadAssetAsync(asset.BrowserDownloadUrl).ConfigureAwait(true);

            Progress = "Extracting…";
            var extractDir = ExtractZip(zipPath);

            Progress = "Installing…";
            var newExe = FindExe(extractDir) ?? throw new Exception("ClaudeBar.exe not found in archive");
            // The whole folder (exe + WPF native DLLs) is the update unit, so swap the directory
            // that contains the new exe — not just the exe.
            var newDir = System.IO.Path.GetDirectoryName(newExe)!;

            Progress = "Relaunching…";
            SwapAndRelaunch(newDir);
        }
        catch (Exception e)
        {
            Error = e.Message;
            IsUpdating = false;
            Progress = null;
        }
    }

    public void StartPeriodicCheck()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer { Interval = CheckInterval };
        _timer.Tick += async (_, _) => await CheckForUpdatesAsync(autoInstall: true);
        _timer.Start();
        // Kick off an initial check shortly after launch.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await OnUi(async () => await CheckForUpdatesAsync(autoInstall: true)).ConfigureAwait(false);
        });
    }

    public void StopPeriodicCheck()
    {
        _timer?.Stop();
        _timer = null;
    }

    // MARK: - GitHub API

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; init; } = "";
        [JsonPropertyName("assets")] public List<Asset> Assets { get; init; } = new();

        public sealed class Asset
        {
            [JsonPropertyName("name")] public string Name { get; init; } = "";
            [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; init; } = "";
        }
    }

    private async Task<GitHubRelease> FetchLatestReleaseAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Repo}/releases/latest");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("User-Agent", "ClaudeBar");
        using var response = await Http.SendAsync(request).ConfigureAwait(true);
        if (!response.IsSuccessStatusCode) throw new Exception($"GitHub returned {(int)response.StatusCode}");
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        return JsonSerializer.Deserialize<GitHubRelease>(body) ?? throw new Exception("Bad release JSON");
    }

    private async Task<string> DownloadAssetAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "ClaudeBar");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(true);
        if (!response.IsSuccessStatusCode) throw new Exception($"Download returned {(int)response.StatusCode}");

        var dest = Path.Combine(Path.GetTempPath(), "ClaudeBar-update.zip");
        if (File.Exists(dest)) File.Delete(dest);
        await using (var fs = File.Create(dest))
            await response.Content.CopyToAsync(fs).ConfigureAwait(true);
        return dest;
    }

    private static string ExtractZip(string zipPath)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ClaudeBar-update");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        ZipFile.ExtractToDirectory(zipPath, dir);
        return dir;
    }

    private static string? FindExe(string dir)
    {
        var direct = Path.Combine(dir, "ClaudeBar.exe");
        if (File.Exists(direct)) return direct;
        return Directory.EnumerateFiles(dir, "ClaudeBar.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>
    /// Windows can't replace files of a running app, so write a detached batch script that: waits
    /// for this PID to exit, copies the whole new folder over the install directory, relaunches the
    /// exe, and cleans up. The folder (not just the exe) is copied because WPF's self-contained
    /// build keeps several native DLLs beside the exe.
    /// </summary>
    private void SwapAndRelaunch(string newDir)
    {
        var currentExe = Environment.ProcessPath
            ?? throw new Exception("Cannot resolve current executable path");
        var installDir = Path.GetDirectoryName(currentExe)!;
        var pid = Environment.ProcessId;
        var scriptPath = Path.Combine(Path.GetTempPath(), "claudebar-update.cmd");

        // Wait for exit, then robocopy the new folder over the install dir. /PURGE deletes files
        // in the install dir that the new release no longer ships (e.g. a native DLL dropped or
        // renamed between versions), so the installed set exactly matches the release instead of
        // accumulating stale DLLs a later build could mis-load. Our own data lives in
        // %APPDATA%\ClaudeBar, never here, so purging the program directory is safe. robocopy exit
        // codes 0–7 are success; >=8 is a real failure, in which case we still relaunch the old exe.
        var script = $"""
            @echo off
            :waitloop
            tasklist /FI "PID eq {pid}" | find "{pid}" >nul
            if not errorlevel 1 (
                timeout /t 1 /nobreak >nul
                goto waitloop
            )
            robocopy "{newDir}" "{installDir}" /E /PURGE /NFL /NDL /NJH /NJS /NC /NS >nul
            start "" "{currentExe}"
            del "%~f0" >nul 2>&1
            """;
        File.WriteAllText(scriptPath, script);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        Process.Start(psi);
        Application.Current.Shutdown();
    }

    // MARK: - Version comparison

    public static bool IsNewer(string remote, string local)
    {
        var r = remote.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var l = local.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        for (var i = 0; i < Math.Max(r.Length, l.Length); i++)
        {
            var rv = i < r.Length ? r[i] : 0;
            var lv = i < l.Length ? l[i] : 0;
            if (rv > lv) return true;
            if (rv < lv) return false;
        }
        return false;
    }

    // MARK: - Helpers

    private static Task OnUi(Func<Task> action)
    {
        var app = Application.Current;
        if (app is null) return action();
        return app.Dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
            app.Dispatcher.Invoke(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
        else
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
