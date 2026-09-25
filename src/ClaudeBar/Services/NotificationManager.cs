namespace ClaudeBar.Services;

/// <summary>
/// Standard Windows tray notifications for when a usage limit needs attention. Edge-triggered off
/// the same Recommendation the menu already shows, so the banner can never disagree with the
/// popover. The whole point is to surface a limit the user isn't looking at — so it fires only on
/// the *rise* into an attention-worthy urgency, never repeatedly while it stays there.
///
/// Delivery is delegated to the tray host (a <c>NotifyIcon</c> balloon tip, which Windows 10/11
/// render as a modern toast) via the injected <see cref="_show"/> callback — the equivalent of
/// the macOS UserNotifications banner.
/// </summary>
public sealed class NotificationManager
{
    /// <summary>Notifications on by default; the menu exposes a checkbox bound to this key.</summary>
    public const string EnabledKey = "notificationsEnabled";

    /// <summary>The lowest urgency that fires a banner; the menu exposes a picker bound to this key.</summary>
    public const string ThresholdKey = "notifyThreshold";

    public static bool IsEnabled => Settings.GetBool(EnabledKey, defaultValue: true);

    /// <summary>
    /// The configured attention threshold, read live so a change takes effect on the next refresh.
    /// Low is never a valid threshold (it would banner every calm update), so the stored value is
    /// clamped into Medium…Critical.
    /// </summary>
    public static Urgency Threshold => ClampThreshold(Settings.GetInt(ThresholdKey, (int)Urgency.High));

    internal static Urgency ClampThreshold(int stored)
    {
        if (stored < (int)Urgency.Medium) return Urgency.Medium;
        if (stored > (int)Urgency.Critical) return Urgency.Critical;
        return (Urgency)stored;
    }

    /// <summary>show(title, body, isCritical).</summary>
    private readonly Action<string, string, bool> _show;

    /// <summary>
    /// The urgency of the last banner we posted. We notify only when urgency climbs *above* this;
    /// a level that merely holds is silent; reset once urgency falls back below the attention
    /// threshold, so the next real spike alerts again. null means "nothing outstanding".
    /// </summary>
    private Urgency? _lastNotifiedUrgency;

    public NotificationManager(Action<string, string, bool> show) => _show = show;

    /// <summary>
    /// Called after every snapshot refresh. Decides — from the transition, not the level alone —
    /// whether this update deserves a banner.
    /// </summary>
    public void Evaluate(Recommendation? recommendation)
    {
        if (!IsEnabled || recommendation is null) return;
        var urgency = recommendation.Urgency;

        if (urgency < Threshold)
        {
            // Back in calm territory — arm the next rise.
            _lastNotifiedUrgency = null;
            return;
        }

        // Already alerted at this level or higher; don't nag until it recovers and spikes anew.
        if (_lastNotifiedUrgency is { } last && urgency <= last) return;

        _lastNotifiedUrgency = urgency;
        Post(recommendation);
    }

    private void Post(Recommendation recommendation)
    {
        var isCritical = recommendation.Urgency == Urgency.Critical;
        var title = isCritical ? "Claude Code limit exhausted" : "Claude Code limit running low";
        var body = string.Join("\n", new[]
            {
                recommendation.Headline,
                recommendation.SessionLine,
                recommendation.WeeklyLine,
                recommendation.ModelLine
            }
            .Where(s => !string.IsNullOrEmpty(s))
            .Take(3));
        _show(title, body, isCritical);
    }
}
