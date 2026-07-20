using System.IO;

namespace ClaudeBar.Services;

/// <summary>
/// Watches ~/.claude/history.jsonl for writes. Claude Code appends to this file whenever it
/// processes a message, meaning the OAuth token is fresh at that moment. We use this as a signal
/// to refresh usage data without managing our own token refresh cycle. Windows equivalent of the
/// macOS FSEvents watcher, using <see cref="FileSystemWatcher"/>.
/// </summary>
public sealed class ActivityWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly Action _onActivity;

    public ActivityWatcher(Action onActivity)
    {
        _onActivity = onActivity;
        var dir = AppPaths.ClaudeDir;
        if (!Directory.Exists(dir)) return;

        try
        {
            _watcher = new FileSystemWatcher(dir, "history.jsonl")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Renamed += OnChanged;
        }
        catch
        {
            _watcher = null;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => _onActivity();

    public void Dispose()
    {
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnChanged;
        _watcher.Created -= OnChanged;
        _watcher.Renamed -= OnChanged;
        _watcher.Dispose();
    }
}
