using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
            // Automatic installs are non-interactive: they proceed ONLY if the update is
            // cryptographically verified. An unverified update is left for the user to install
            // explicitly via the "Update" button (which then asks for confirmation).
            if (UpdateAvailable && autoInstall) await PerformUpdateAsync(interactive: false).ConfigureAwait(true);
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

    /// <param name="interactive">
    /// true for a user-initiated install (the "Update" button): an update that cannot be
    /// cryptographically verified prompts for explicit confirmation before installing.
    /// false for the automatic timer path: an unverified update is NOT installed — it is left for
    /// the user to install explicitly, so untrusted code is never run silently.
    /// </param>
    public async Task PerformUpdateAsync(bool interactive = true)
    {
        if (IsUpdating) return;
        IsUpdating = true;
        Error = null;
        try
        {
            var release = await FetchLatestReleaseAsync().ConfigureAwait(true);
            var version = release.TagName.StartsWith("v") ? release.TagName[1..] : release.TagName;
            var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (asset is null) throw new Exception("No downloadable asset in release");

            Progress = "Downloading…";
            var zipPath = await DownloadAssetAsync(asset.BrowserDownloadUrl).ConfigureAwait(true);

            // Integrity: the downloaded bytes must match the digest GitHub attests for the asset.
            // A mismatch means the archive was corrupted or tampered in transit — a hard failure.
            if (!VerifyDigest(zipPath, asset.Digest, out var digestStatus))
                throw new Exception($"Integrity check failed: {digestStatus}");

            Progress = "Extracting…";
            var extractDir = ExtractZip(zipPath);

            Progress = "Installing…";
            var newExe = FindExe(extractDir) ?? throw new Exception("ClaudeBar.exe not found in archive");
            // The whole folder (exe + WPF native DLLs) is the update unit, so swap the directory
            // that contains the new exe — not just the exe.
            var newDir = System.IO.Path.GetDirectoryName(newExe)!;

            // Authenticity: require a valid Authenticode signature whose publisher matches the
            // running build's. This is what makes a silent auto-install safe against a tampered or
            // malicious release. When it cannot be established (e.g. builds are not yet code-signed),
            // never install silently; only proceed on explicit user confirmation.
            var authentic = IsUpdateAuthentic(newExe, out var authStatus);
            if (!authentic)
            {
                if (!interactive)
                {
                    // Leave UpdateAvailable set so the menu still offers a manual "Update".
                    Progress = null;
                    IsUpdating = false;
                    return;
                }

                var proceed = MessageBox.Show(
                    $"This update (v{version}) could not be automatically verified:\n\n{authStatus}\n\n" +
                    "Install it anyway? Only continue if you trust this release.",
                    "ClaudeBar — update not verified",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
                if (!proceed)
                {
                    Progress = null;
                    IsUpdating = false;
                    return;
                }
            }

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
            // GitHub attaches a "sha256:<hex>" content digest to release assets; used as an integrity
            // check on the downloaded bytes. Absent on older releases (then integrity is unverified).
            [JsonPropertyName("digest")] public string? Digest { get; init; }
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
        // Only ever fetch the update from GitHub over HTTPS. A tampered release JSON could otherwise
        // point browser_download_url at an arbitrary host; refuse anything off the allowlist.
        if (!IsTrustedDownloadUrl(url))
            throw new Exception($"Refusing to download update from untrusted URL: {url}");

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

    // MARK: - Update verification

    private static bool IsTrustedDownloadUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Compare the downloaded file's SHA-256 against the "sha256:&lt;hex&gt;" digest GitHub reports.
    /// Returns false only on a real mismatch. When no (or an unrecognised) digest is published the
    /// bytes can't be checked — that's not a hard failure here, because authenticity is enforced
    /// separately by the Authenticode gate.
    /// </summary>
    private static bool VerifyDigest(string filePath, string? digest, out string status)
    {
        if (string.IsNullOrWhiteSpace(digest)) { status = "no digest published"; return true; }
        var parts = digest.Split(':', 2);
        if (parts.Length != 2 || !parts[0].Equals("sha256", StringComparison.OrdinalIgnoreCase))
        {
            status = $"unsupported digest '{digest}'";
            return true;
        }

        using var fs = File.OpenRead(filePath);
        var actual = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        var expected = parts[1].Trim().ToLowerInvariant();
        if (actual == expected) { status = "sha256 ok"; return true; }
        status = $"sha256 mismatch (expected {Short(expected)}, got {Short(actual)})";
        return false;

        static string Short(string h) => h.Length > 12 ? h[..12] + "…" : h;
    }

    /// <summary>
    /// The update is authentic iff the new exe carries a valid, trusted Authenticode signature whose
    /// publisher matches the running build's. Continuity (same publisher) is what lets us trust an
    /// automatic install without a pinned certificate. If the running build is itself unsigned there
    /// is no publisher to match against, so authenticity cannot be established and the caller must
    /// fall back to explicit user confirmation.
    /// </summary>
    private static bool IsUpdateAuthentic(string newExe, out string status)
    {
        var (newTrusted, newSubject) = Authenticode.Verify(newExe);
        if (!newTrusted)
        {
            status = "the downloaded file has no valid, trusted Authenticode signature";
            return false;
        }

        var currentExe = Environment.ProcessPath;
        var (curTrusted, curSubject) = currentExe is null
            ? (false, null)
            : Authenticode.Verify(currentExe);
        if (!curTrusted || curSubject is null)
        {
            status = $"signed by \"{newSubject}\", but the current build is unsigned — publisher continuity can't be verified";
            return false;
        }

        if (!string.Equals(curSubject, newSubject, StringComparison.Ordinal))
        {
            status = $"publisher mismatch — update signed by \"{newSubject}\", current build by \"{curSubject}\"";
            return false;
        }

        status = $"verified — signed by \"{newSubject}\"";
        return true;
    }

    /// <summary>
    /// Minimal Authenticode check via WinVerifyTrust: does the file have a signature that chains to a
    /// trusted root and is otherwise valid? Also returns the signer's certificate subject.
    /// </summary>
    private static class Authenticode
    {
        public static (bool trusted, string? subject) Verify(string path)
        {
            var trusted = IsTrusted(path);
            string? subject = null;
            if (trusted)
            {
                try { subject = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)).Subject; }
                catch { /* trusted but subject unreadable — leave null */ }
            }
            return (trusted, subject);
        }

        private static bool IsTrusted(string path)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = path,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };
            var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            var pData = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    pPolicyCallbackData = IntPtr.Zero,
                    pSIPClientData = IntPtr.Zero,
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pInfoUnion = pFile,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    hWVTStateData = IntPtr.Zero,
                    pwszURLReference = IntPtr.Zero,
                    dwProvFlags = WTD_SAFER_FLAG,
                    dwUIContext = 0,
                    pSignatureSettings = IntPtr.Zero,
                };
                pData = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
                Marshal.StructureToPtr(data, pData, false);

                int result;
                try { result = WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, pData); }
                finally
                {
                    // Always release WinVerifyTrust's state, whatever the verdict.
                    data.dwStateAction = WTD_STATEACTION_CLOSE;
                    Marshal.StructureToPtr(data, pData, true);
                    WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);
                }
                return result == 0; // ERROR_SUCCESS — signed, valid, and trusted
            }
            catch (DllNotFoundException)
            {
                return false; // no wintrust.dll (non-Windows) — treat as unverifiable
            }
            finally
            {
                Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
                Marshal.FreeHGlobal(pFile);
                if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
            }
        }

        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
            new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;
        private const uint WTD_SAFER_FLAG = 0x100;

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pInfoUnion;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }
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
