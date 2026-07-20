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

    // FileSystemWatcher raises several events for a single logical write, and Claude Code can
    // append many lines in quick succession. Coalesce a burst into one callback so we don't kick
    // off a full history reparse per raw event; the timer fires once the writes go quiet.
    private readonly System.Threading.Timer? _debounce;
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(750);

    public ActivityWatcher(Action onActivity)
    {
        _onActivity = onActivity;
        var dir = AppPaths.ClaudeDir;
        if (!Directory.Exists(dir)) return;

        try
        {
            _debounce = new System.Threading.Timer(_ => _onActivity(), null,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
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

    private void OnChanged(object sender, FileSystemEventArgs e) =>
        _debounce?.Change(DebounceDelay, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        _debounce?.Dispose();
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnChanged;
        _watcher.Created -= OnChanged;
        _watcher.Renamed -= OnChanged;
        _watcher.Dispose();
    }
}
