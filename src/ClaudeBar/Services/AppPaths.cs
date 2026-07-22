using System.IO;

namespace ClaudeBar.Services;

/// <summary>
/// Centralizes every filesystem location ClaudeBar touches, so the Windows-specific paths live
/// in exactly one place (the macOS original scattered these across each reader).
/// </summary>
public static class AppPaths
{
    /// <summary>%USERPROFILE% — the user's home directory.</summary>
    public static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>~/.claude — where the Claude Code CLI keeps its state.</summary>
    public static string ClaudeDir => Path.Combine(Home, ".claude");

    /// <summary>
    /// The CLI's OAuth credential file. On Windows (and Linux) the CLI stores credentials as
    /// plaintext JSON here rather than in a keychain — so reading the token is a plain file read.
    /// </summary>
    public static string CredentialsFile => Path.Combine(ClaudeDir, ".credentials.json");

    /// <summary>~/.claude/history.jsonl — one line appended per submitted prompt.</summary>
    public static string HistoryFile => Path.Combine(ClaudeDir, "history.jsonl");

    /// <summary>
    /// Candidate user-data directories for the Claude desktop (Electron) app, in priority order,
    /// filtered to those that actually exist. Two install shapes must be probed:
    /// <list type="bullet">
    /// <item>the classic installer writes to <c>%APPDATA%\Claude</c>;</item>
    /// <item>the Microsoft Store (MSIX) build redirects Roaming into its package container,
    /// <c>%LOCALAPPDATA%\Packages\Claude_&lt;publisherHash&gt;\LocalCache\Roaming\Claude</c> — so the
    /// classic path simply does not exist for a Store install, which is why the desktop token was
    /// silently missed before.</item>
    /// </list>
    /// The package family name is matched by glob (<c>Claude_*</c>) rather than a hardcoded publisher
    /// hash, so a reinstall or republish still resolves. config.json and "Local State" must always be
    /// read as a pair from the *same* directory, since each install's os_crypt key decrypts only its
    /// own blobs.
    /// </summary>
    public static IEnumerable<string> DesktopDirCandidates()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var classic = Path.Combine(appData, "Claude");
        if (Directory.Exists(classic)) yield return classic;

        var packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packages)) yield break;

        IEnumerable<string> packaged;
        try { packaged = Directory.EnumerateDirectories(packages, "Claude_*"); }
        catch { yield break; }
        foreach (var pkg in packaged)
        {
            var dir = Path.Combine(pkg, "LocalCache", "Roaming", "Claude");
            if (Directory.Exists(dir)) yield return dir;
        }
    }

    /// <summary>The desktop app's config store (encrypted OAuth token cache) inside a candidate dir.</summary>
    public static string DesktopConfigFile(string desktopDir) => Path.Combine(desktopDir, "config.json");

    /// <summary>Chromium/Electron "Local State" (DPAPI-wrapped os_crypt key) inside a candidate dir.</summary>
    public static string DesktopLocalStateFile(string desktopDir) => Path.Combine(desktopDir, "Local State");

    /// <summary>%APPDATA%\ClaudeBar — our own persisted data (usage history, etc.).</summary>
    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeBar");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string UsageHistoryFile => Path.Combine(DataDir, "usage-history.jsonl");
}
