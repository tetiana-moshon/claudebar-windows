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

    /// <summary>%APPDATA%\Claude — the Claude desktop (Electron) app's user-data directory.</summary>
    public static string DesktopDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");

    /// <summary>The desktop app's config store holding the encrypted OAuth token cache.</summary>
    public static string DesktopConfigFile => Path.Combine(DesktopDir, "config.json");

    /// <summary>Chromium/Electron "Local State" holding the DPAPI-wrapped os_crypt master key.</summary>
    public static string DesktopLocalStateFile => Path.Combine(DesktopDir, "Local State");

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
