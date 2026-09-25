using ClaudeBar.Models;

namespace ClaudeBar.Services;

public enum LimitScope { Session, Weekly, ScopedWeekly }

/// <summary>One rate window that has crossed into "almost gone" territory, ready for display.</summary>
public sealed record LimitAlertEntry(LimitScope Scope, string Title, RateWindow Window, DateTime ResetsAt);

/// <summary>
/// The decision logic behind the last-resort, impossible-to-miss limit dialog (a port of the macOS
/// LimitAlertPresenter's rules). Kept UI-free and mostly pure so it can be unit-tested: the window
/// itself lives in <see cref="Views.LimitAlertPresenter"/>. The banner (NotificationManager) can be
/// dismissed unseen or hidden behind a fullscreen app; this escalation exists for when a limit is
/// genuinely about to run out and must not be missed.
/// </summary>
public static class LimitAlert
{
    /// <summary>Dialog on by default; the Settings window exposes a checkbox bound to this key.</summary>
    public const string EnabledKey = "limitDialogEnabled";
    public static bool IsEnabled => Settings.GetBool(EnabledKey, defaultValue: true);

    /// <summary>Below this remaining %, the 5-hour session window earns a dialog.</summary>
    public const double SessionThreshold = 10;

    /// <summary>Below this remaining %, either weekly window (general or per-model) earns one.</summary>
    public const double WeeklyThreshold = 5;

    /// <summary>
    /// Mirrors Recommender's own carve-out: a session this close to its reset is about to fix itself,
    /// so the banner and menu bar deliberately stay calm. The dialog is a blunter, threshold-only
    /// instrument, but it honors this one softening rule rather than alarm the user for nothing.
    /// </summary>
    public static readonly TimeSpan ImminentReset = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Every window currently under its threshold — what the dialog DISPLAYS. A window with no known
    /// reset can neither be dedup'd nor recovered from, so it is skipped (same guard as PaceTier).
    /// </summary>
    public static IReadOnlyList<LimitAlertEntry> QualifyingEntries(UsageSnapshot snapshot, DateTime now)
    {
        var result = new List<LimitAlertEntry>();

        void Consider(LimitScope scope, string title, RateWindow? window, double threshold)
        {
            if (window is null || window.RemainingPercent >= threshold) return;
            if (window.ResetsAt is not { } reset) return;
            if (scope == LimitScope.Session && reset - now < ImminentReset) return;
            result.Add(new LimitAlertEntry(scope, title, window, reset));
        }

        Consider(LimitScope.Session, "5-hour limit", snapshot.Session, SessionThreshold);
        Consider(LimitScope.Weekly, "Weekly limit", snapshot.Weekly, WeeklyThreshold);
        var scopedTitle = snapshot.ScopedModelName is { } m ? $"Weekly {m} limit" : "Weekly model limit";
        Consider(LimitScope.ScopedWeekly, scopedTitle, snapshot.ScopedWeekly, WeeklyThreshold);
        return result;
    }

    /// <summary>
    /// Of the qualifying windows, those the user hasn't already been interrupted for — i.e. with no
    /// live suppression. Pure over an injected suppression lookup so the interrupt rule is testable.
    /// </summary>
    public static IReadOnlyList<LimitAlertEntry> NewlyCrossed(
        IReadOnlyList<LimitAlertEntry> qualifying, Func<LimitScope, DateTime?> suppressedUntil, DateTime now) =>
        qualifying.Where(e => suppressedUntil(e.Scope) is not { } until || until <= now).ToList();

    // MARK: - Persisted suppression
    //
    // Persisted (not in-memory) so both the "already shown, wait for reset" state and the "snoozed
    // 15 minutes" state survive a relaunch — an in-memory snooze used to get wiped by a restart
    // mid-snooze, immediately re-showing the dialog the user had just asked to defer.

    private static string SuppressKey(LimitScope scope) => $"limitDialogSuppressedUntil.{scope}";

    public static DateTime? SuppressedUntil(LimitScope scope)
    {
        if (Settings.GetDouble(SuppressKey(scope)) is not { } secs) return null;
        return DateTimeOffset.FromUnixTimeSeconds((long)secs).LocalDateTime;
    }

    public static void Suppress(LimitScope scope, DateTime until) =>
        Settings.SetDouble(SuppressKey(scope), ((DateTimeOffset)until.ToUniversalTime()).ToUnixTimeSeconds());

    /// <summary>Forget a scope's suppression (used by the fake-limit verification mode so a leftover
    /// snooze from a previous run doesn't swallow the forced alert).</summary>
    public static void ClearSuppression(LimitScope scope) => Settings.Remove(SuppressKey(scope));
}
