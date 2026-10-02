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
    /// ~/.claude/projects — one subdirectory per working directory, each holding the session
    /// transcript <c>.jsonl</c> files Claude Code writes for both the CLI and the desktop app. Every
    /// line carries an ISO-8601 <c>timestamp</c>, so these transcripts are a far richer "when do I
    /// work" signal than <see cref="HistoryFile"/> — which only the CLI appends to, and so stays
    /// empty for anyone who drives Claude Code mostly from the desktop app.
    /// </summary>
    public static string ProjectsDir => Path.Combine(ClaudeDir, "projects");

    /// <summary>The desktop app's config store holding the encrypted OAuth token cache.</summary>
    public const string DesktopConfigFileName = "config.json";

    /// <summary>Chromium/Electron "Local State" holding the DPAPI-wrapped os_crypt master key.</summary>
    public const string DesktopLocalStateFileName = "Local State";

    /// <summary>
    /// Every Claude desktop (Electron) user-data directory that could hold the live token cache,
    /// freshest first. See <see cref="DesktopDirCandidates"/>.
    /// </summary>
    public static IReadOnlyList<string> DesktopDirs => DesktopDirCandidates(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// The installer build writes to %APPDATA%\Claude; the Microsoft Store (MSIX) build has its
    /// AppData writes virtualized into %LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude,
    /// which unpackaged processes like ours never see under %APPDATA%. Someone who moved from the
    /// installer build to the Store build keeps a frozen %APPDATA%\Claude behind, so neither location
    /// gets priority: keep every folder holding both the cache and its key, ordered by the newest
    /// <c>config.json</c> — the desktop app rewrites it on each token refresh, so the live install
    /// leads. Split out with explicit roots so the selection is testable.
    /// </summary>
    internal static IReadOnlyList<string> DesktopDirCandidates(string appDataDir, string localAppDataDir)
    {
        var dirs = new List<string> { Path.Combine(appDataDir, "Claude") };
        try
        {
            dirs.AddRange(Directory.EnumerateDirectories(Path.Combine(localAppDataDir, "Packages"), "Claude_*")
                .Select(p => Path.Combine(p, "LocalCache", "Roaming", "Claude")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No Packages folder (or no access to it): only the installer location is left.
        }

        return dirs
            .Where(d => File.Exists(Path.Combine(d, DesktopConfigFileName)) &&
                        File.Exists(Path.Combine(d, DesktopLocalStateFileName)))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, DesktopConfigFileName)))
            .ToArray();
    }

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
