using System.Windows;
using System.Windows.Threading;
using ClaudeBar.Models;
using ClaudeBar.Services;

namespace ClaudeBar.Views;

/// <summary>
/// Owns the single outstanding limit dialog and drives it from each usage snapshot, applying the
/// rules in <see cref="LimitAlert"/>. A port of the macOS LimitAlertPresenter: a later evaluate that
/// finds the window already open updates it in place with the full current qualifying set (so a scope
/// that alerted first is never silently dropped), and only a genuinely new crossing steals focus.
/// </summary>
public sealed class LimitAlertPresenter
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private LimitAlertWindow? _window;
    // The set currently on screen — the single source of truth for the snooze action. Refreshed on
    // every Show so "Remind in 15 min" snoozes what the user is actually looking at, not the scopes
    // captured when the dialog first opened (later SetEntries refreshes never rebind the closure).
    private IReadOnlyList<LimitAlertEntry> _currentEntries = Array.Empty<LimitAlertEntry>();

    /// <summary>Called after every snapshot refresh. Decides whether to show, refresh, or close.</summary>
    public void Evaluate(UsageSnapshot snapshot)
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => Evaluate(snapshot)); return; }

        if (!LimitAlert.IsEnabled) { Close(); return; }

        var now = DateTime.Now;
        var qualifying = LimitAlert.QualifyingEntries(snapshot, now);
        if (qualifying.Count == 0) { Close(); return; } // every window recovered

        var newlyCrossed = LimitAlert.NewlyCrossed(qualifying, LimitAlert.SuppressedUntil, now);
        // Nothing new and nothing open: stay quiet. Nothing new but a window IS open: still refresh
        // it below so its percentages don't go stale while it sits.
        if (_window is null && newlyCrossed.Count == 0) return;

        // Suppress each newly crossed scope until its window resets, so it won't re-interrupt until
        // there is genuinely a fresh crossing.
        foreach (var entry in newlyCrossed) LimitAlert.Suppress(entry.Scope, entry.ResetsAt);

        Show(qualifying, stealFocus: newlyCrossed.Count > 0);
    }

    /// <summary>Close an open dialog when the user turns the feature off in Settings.</summary>
    public void SettingChanged(bool enabled)
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => SettingChanged(enabled)); return; }
        if (!enabled) Close();
    }

    private void Show(IReadOnlyList<LimitAlertEntry> entries, bool stealFocus)
    {
        _currentEntries = entries;
        if (_window is null)
        {
            _window = new LimitAlertWindow(entries, onDismiss: Close, onSnooze: Snooze);
            _window.Closed += (_, _) => _window = null; // titlebar close clears the reference too
            _window.Show();
        }
        else
        {
            _window.SetEntries(entries);
        }

        if (!stealFocus) return; // content-only refresh of an already-open dialog
        _window.Topmost = true;
        _window.Activate();
    }

    private void Snooze()
    {
        var until = DateTime.Now.AddMinutes(15);
        foreach (var entry in _currentEntries) LimitAlert.Suppress(entry.Scope, until);
        Close();
    }

    private void Close()
    {
        _window?.Close();
        _window = null;
    }
}
