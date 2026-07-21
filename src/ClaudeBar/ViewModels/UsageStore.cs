using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using ClaudeBar.Models;
using ClaudeBar.Services;

namespace ClaudeBar.ViewModels;

/// <summary>Coarse status used by both the tray icon and the popover to pick a color.</summary>
public enum StatusLevel { Neutral, Green, Yellow, Orange, Red, Blue }

/// <summary>
/// The app's single source of truth: reads a token something else keeps fresh, fetches usage, and
/// exposes everything the UI binds to. A faithful port of the macOS UsageStore, including the
/// exponential rate-limit backoff, token-source fallback, and off-hours-aware recommendation.
/// </summary>
public sealed class UsageStore : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when a limit banner should be shown: (title, body, isCritical).</summary>
    public event Action<string, string, bool>? NotificationRequested;

    private UsageSnapshot? _snapshot;
    public UsageSnapshot? Snapshot { get => _snapshot; private set { _snapshot = value; RaiseAll(); } }

    private string? _errorMessage;
    // While a rate-limit window is active the message is derived live from _rateLimitedUntil, so the
    // 30s UI tick renders a real countdown instead of a value frozen at the moment of the 429. The
    // stored _errorMessage is the fallback for every other (genuinely static) error and for the brief
    // gap between the window elapsing and the scheduled retry firing.
    public string? ErrorMessage
    {
        get => _rateLimitedUntil is { } until && until > DateTime.Now
            ? RateLimitedMessage((until - DateTime.Now).TotalSeconds)
            : _errorMessage;
        private set { _errorMessage = value; RaiseAll(); }
    }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set { _isLoading = value; RaiseAll(); } }

    private DateTime? _lastUpdated;
    public DateTime? LastUpdated { get => _lastUpdated; private set { _lastUpdated = value; RaiseAll(); } }

    private bool _launchAtLogin = Services.LaunchAtLogin.IsEnabled;
    public bool LaunchAtLogin { get => _launchAtLogin; private set { _launchAtLogin = value; RaiseAll(); } }

    private ActivityProfile _activity = ActivityProfile.Empty;
    public ActivityProfile Activity { get => _activity; private set { _activity = value; RaiseAll(); } }

    /// <summary>Persisted quota-burn samples backing the Statistics window's chart.</summary>
    public UsageHistoryStore History { get; } = new();

    private readonly NotificationManager _notifications;
    // Captured on the UI thread at construction so background callbacks (FileSystemWatcher,
    // retry tasks) can always marshal work back to the UI, even before Application.Current is set.
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private DispatcherTimer? _timer;
    private DispatcherTimer? _uiTick;
    private DateTime? _rateLimitedUntil;
    private CancellationTokenSource? _retryCts;
    private ActivityWatcher? _activityWatcher;
    private DateTime? _lastAttempt;
    private int _rateLimitStreak;

    private enum TokenSource { Desktop, Cli }
    private TokenSource? _preferredTokenSource;

    // Backoff: 6 → 12 → 24 → 48 min, capped at 60 (see the macOS original for the livelock rationale).
    private static readonly double RateLimitBackoffBase = 6 * 60;
    private static readonly double RateLimitBackoffCap = 60 * 60;
    private const int MaxRateLimitStreak = 6;
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinFetchInterval = TimeSpan.FromSeconds(45);

    private const string RateLimitedUntilKey = "rateLimitedUntil";
    private const string RateLimitStreakKey = "rateLimitStreak";

    public UsageStore()
    {
        _notifications = new NotificationManager((t, b, c) => NotificationRequested?.Invoke(t, b, c));

        LoadPersistedRateLimit();
        ReloadActivity();

        // Skip the startup probe while a persisted backoff window is still active, but schedule the
        // automatic retry so the state clears itself the moment the window elapses.
        if (_rateLimitedUntil is { } until && until > DateTime.Now)
        {
            ErrorMessage = RateLimitedMessage((until - DateTime.Now).TotalSeconds);
            ScheduleRateLimitRetry();
        }
        else
            _ = RefreshAsync();

        ScheduleTimer();
        ScheduleUiTick();

        _activityWatcher = new ActivityWatcher(() =>
        {
            // FileSystemWatcher fires on a threadpool thread; marshal onto the UI dispatcher
            // captured at construction (CurrentDispatcher here would be a dead threadpool one).
            _dispatcher.BeginInvoke(async () =>
            {
                ReloadActivity();
                await RefreshOnActivityAsync();
            });
        });
    }

    /// <summary>Re-derive the active-hours profile from history.jsonl off the UI thread.</summary>
    public void ReloadActivity()
    {
        _ = Task.Run(() =>
        {
            var profile = ActivityHistory.Load();
            _dispatcher.Invoke(() => Activity = profile);
        });
    }

    /// <summary>
    /// ClaudeBar never refreshes OAuth tokens itself — it reads a token that something else keeps
    /// fresh: the Claude desktop app's encrypted token cache (primary) or the CLI's credentials
    /// token (fallback). A manual Refresh (force: true) bypasses the rate-limit backoff.
    /// </summary>
    public async Task RefreshAsync(bool force = false)
    {
        if (IsLoading) return;

        if (!force)
        {
            if (_rateLimitedUntil is { } until && until > DateTime.Now)
            {
                ErrorMessage = RateLimitedMessage((until - DateTime.Now).TotalSeconds);
                return;
            }
            if (_lastAttempt is { } la && DateTime.Now - la < MinFetchInterval) return;
        }

        _lastAttempt = DateTime.Now;
        IsLoading = true;
        try
        {
            var response = await FetchUsageTryingCandidatesAsync().ConfigureAwait(true);
            // A 200 reached us, so we're no longer rate limited.
            ClearRateLimit();
            _retryCts?.Cancel();
            _retryCts = null;
            var snap = UsageSnapshot.From(response);
            if (snap is not null)
            {
                Snapshot = snap;
                LastUpdated = DateTime.Now;
                ErrorMessage = null;
                History.Record(snap);
                _notifications.Evaluate(Recommendation);
            }
            else
            {
                ErrorMessage = "No session data from API.";
            }
        }
        catch (ApiException ex) when (ex.Kind == ApiErrorKind.Unauthorized)
        {
            ErrorMessage = ex.Message;
        }
        catch (ApiException ex) when (ex.Kind == ApiErrorKind.RateLimited)
        {
            var now = DateTime.Now;
            if (_rateLimitedUntil is { } until && until > now)
            {
                ErrorMessage = RateLimitedMessage((until - now).TotalSeconds);
            }
            else
            {
                _rateLimitStreak = Math.Min(_rateLimitStreak + 1, MaxRateLimitStreak);
                var backoff = RateLimitBackoff(_rateLimitStreak);
                _rateLimitedUntil = now.AddSeconds(backoff);
                PersistRateLimit();
                ErrorMessage = RateLimitedMessage(backoff);
            }
            ScheduleRateLimitRetry();
        }
        catch (ApiException ex) when (ex.Kind == ApiErrorKind.TokenExpired)
        {
            ErrorMessage = ex.Message;
            ScheduleRetry();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Refresh when the menu opens if data is older than ~60s.</summary>
    public async Task RefreshIfStaleAsync()
    {
        if (_lastUpdated is { } lu && (DateTime.Now - lu).TotalSeconds < 60) return;
        await RefreshAsync();
    }

    public void SetLaunchAtLogin(bool enabled)
    {
        try
        {
            if (enabled) Services.LaunchAtLogin.Enable();
            else Services.LaunchAtLogin.Disable();
        }
        catch
        {
            // fall through to reading the actual state
        }
        LaunchAtLogin = Services.LaunchAtLogin.IsEnabled;
    }

    private async Task RefreshOnActivityAsync()
    {
        if (_retryCts is not null) return; // pending retry owns error recovery
        await RefreshAsync();
    }

    /// <summary>
    /// The access tokens to try, in priority order: the token the Claude desktop app keeps fresh
    /// in its own encrypted store, then the CLI credentials token. De-duped, ordered so the
    /// last-successful source leads.
    /// </summary>
    private List<(TokenSource source, string token)> CandidateTokens()
    {
        var tokens = new List<(TokenSource, string)>();
        foreach (var desktop in DesktopTokenReader.CurrentTokens()) tokens.Add((TokenSource.Desktop, desktop));
        if (CredentialReader.TryReadToken() is { } cli) tokens.Add((TokenSource.Cli, cli));

        var seen = new HashSet<string>();
        var deduped = tokens.Where(t => seen.Add(t.Item2))
            .Select(t => (source: t.Item1, token: t.Item2)).ToList();

        if (_preferredTokenSource is { } pref)
        {
            var idx = deduped.FindIndex(t => t.source == pref);
            if (idx > 0)
            {
                var item = deduped[idx];
                deduped.RemoveAt(idx);
                deduped.Insert(0, item);
            }
        }
        return deduped;
    }

    private async Task<OAuthUsageResponse> FetchUsageTryingCandidatesAsync()
    {
        var tokens = CandidateTokens();
        if (tokens.Count == 0)
        {
            CredentialReader.ReadCredentials(); // throws NotFound → "run claude login"
            throw ApiException.Unauthorized;
        }

        var authError = ApiException.TokenExpired;
        foreach (var candidate in tokens)
        {
            try
            {
                var response = await OAuthApi.FetchUsageAsync(candidate.token).ConfigureAwait(true);
                _preferredTokenSource = candidate.source;
                return response;
            }
            catch (ApiException ex) when (ex.Kind == ApiErrorKind.TokenExpired)
            {
                authError = ApiException.TokenExpired;
            }
            catch (ApiException ex) when (ex.Kind == ApiErrorKind.Unauthorized)
            {
                authError = ApiException.Unauthorized;
            }
            // A 429 / network error is identical for every token, so it propagates immediately.
        }
        throw authError;
    }

    private void ScheduleRetry(TimeSpan? delay = null)
    {
        if (_retryCts is not null) return;
        _retryCts = new CancellationTokenSource();
        var token = _retryCts.Token;
        var wait = delay ?? TimeSpan.FromSeconds(60);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(wait, token).ConfigureAwait(false); }
            catch { return; }
            await _dispatcher.InvokeAsync(async () =>
            {
                _retryCts = null; // clear before refresh so next failure can re-schedule
                await RefreshAsync();
            });
        });
    }

    /// <summary>
    /// Schedule the automatic re-fetch for the moment the backoff window elapses (plus a small
    /// buffer). Without this the rate-limit state would only clear on the 5-minute timer or on user
    /// activity, leaving the countdown stuck at "0m" long after the window actually expired.
    /// </summary>
    private void ScheduleRateLimitRetry()
    {
        if (_rateLimitedUntil is not { } until) return;
        var delay = until - DateTime.Now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        ScheduleRetry(delay + TimeSpan.FromSeconds(2));
    }

    private void ScheduleTimer()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(300) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
    }

    /// <summary>A light UI heartbeat so relative "updated N ago" text and staleness re-render.</summary>
    private void ScheduleUiTick()
    {
        _uiTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _uiTick.Tick += (_, _) => RaiseAll();
        _uiTick.Start();
    }

    internal static double RateLimitBackoff(int streak)
    {
        var multiplier = (double)(1 << Math.Max(streak - 1, 0));
        return Math.Min(RateLimitBackoffBase * multiplier, RateLimitBackoffCap);
    }

    private string RateLimitedMessage(double intervalSeconds) =>
        $"Rate limited — retry in {Format.Duration(intervalSeconds)}";

    private void ClearRateLimit()
    {
        if (_rateLimitedUntil is null && _rateLimitStreak == 0) return;
        _rateLimitedUntil = null;
        _rateLimitStreak = 0;
        Settings.Remove(RateLimitedUntilKey);
        Settings.Remove(RateLimitStreakKey);
    }

    private void PersistRateLimit()
    {
        if (_rateLimitedUntil is not { } until) return;
        Settings.SetDouble(RateLimitedUntilKey, ((DateTimeOffset)until.ToUniversalTime()).ToUnixTimeSeconds());
        Settings.SetInt(RateLimitStreakKey, _rateLimitStreak);
    }

    private void LoadPersistedRateLimit()
    {
        if (Settings.GetDouble(RateLimitedUntilKey) is not { } secs) return;
        var until = DateTimeOffset.FromUnixTimeSeconds((long)secs).LocalDateTime;
        if (until <= DateTime.Now)
        {
            Settings.Remove(RateLimitedUntilKey);
            Settings.Remove(RateLimitStreakKey);
            return;
        }
        _rateLimitedUntil = until;
        _rateLimitStreak = Settings.GetInt(RateLimitStreakKey, 0);
    }

    // MARK: - Derived UI state

    public Recommendation? Recommendation =>
        Snapshot is { } s ? new Recommender(s, Activity).Recommend() : null;

    public bool IsStale =>
        _lastUpdated is { } lu && DateTime.Now - lu > StaleThreshold;

    public bool IsDegraded => Snapshot is not null && IsStale;

    public string MenuBarText
    {
        get
        {
            if (Snapshot is not { } snap) return "C";
            if (IsStale) return "—";
            if (RecoveryText(snap.Session) is { } sessionRecovery) return sessionRecovery;
            var session = MenuBarSegment("5h", snap.Session);
            if (snap.Weekly is not { } weekly) return session;
            return $"{session} · {MenuBarSegment("7d", weekly)}";
        }
    }

    public StatusLevel Status
    {
        get
        {
            if (Snapshot is null || IsStale) return StatusLevel.Neutral;
            if (Recommendation?.IsHeadroom == true) return StatusLevel.Blue;
            return Recommendation?.Urgency switch
            {
                Urgency.Low => StatusLevel.Green,
                Urgency.Medium => StatusLevel.Yellow,
                Urgency.High => StatusLevel.Orange,
                Urgency.Critical => StatusLevel.Red,
                _ => StatusLevel.Neutral
            };
        }
    }

    private string MenuBarSegment(string label, RateWindow window)
    {
        if (window.RemainingPercent < 5 && RecoveryText(window) is { } recovery)
            return $"{label} {recovery}";
        return $"{label} {Math.Round(window.RemainingPercent)}%";
    }

    private string? RecoveryText(RateWindow window)
    {
        if (window.RemainingPercent < 5 && window.ResetsAt is { } resetsAt &&
            window.TimeUntilReset is { } resetIn)
            return $"{FormatResetMoment(resetsAt)} ({Format.Duration(resetIn)})";
        return null;
    }

    private static string FormatResetMoment(DateTime date) =>
        date.Date == DateTime.Now.Date ? date.ToString("HH:mm") : date.ToString("d MMM HH:mm");

    private void RaiseAll([CallerMemberName] string? _ = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public void Dispose()
    {
        _timer?.Stop();
        _uiTick?.Stop();
        _retryCts?.Cancel();
        _activityWatcher?.Dispose();
    }
}
