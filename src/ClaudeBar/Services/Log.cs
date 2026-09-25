using System.IO;

namespace ClaudeBar.Services;

/// <summary>
/// A tiny append-only diagnostic log at %APPDATA%\ClaudeBar\error.log. A tray app has no console and
/// most failures here are deliberately swallowed so the UI keeps running, which historically left
/// "no data" or a long stale window with no way to tell whether the fetch crashed, the network
/// failed, or the app simply wasn't running. This gives those swallowed failures a durable trail.
///
/// Every method is best-effort and never throws back into its caller — logging must not become a new
/// failure path. The file is size-capped with one rollover generation so it can't grow unbounded or
/// have its earliest context erased by a tight failure loop.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>Roll over once the live log passes this size, keeping one previous generation.</summary>
    internal const long MaxBytes = 256 * 1024;

    public static void Error(string context, Exception? ex) => Write("ERROR", context, ex?.ToString());
    public static void Warn(string context, string? detail = null) => Write("WARN", context, detail);
    public static void Info(string context, string? detail = null) => Write("INFO", context, detail);

    private static void Write(string level, string context, string? detail)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDir, "error.log");
            var line = detail is null
                ? $"[{DateTime.Now:o}] {level} {context}\n"
                : $"[{DateTime.Now:o}] {level} {context}: {detail}\n";
            lock (Gate)
            {
                Rotate(path);
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Logging must never throw into the caller — there is nothing more we can do.
        }
    }

    private static void Rotate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;
            File.Copy(path, path + ".1", overwrite: true);
            File.WriteAllText(path, "");
        }
        catch
        {
            // If rotation fails the log simply keeps growing — acceptable next to losing the entry.
        }
    }
}
