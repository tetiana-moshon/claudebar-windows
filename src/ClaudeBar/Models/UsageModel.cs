using System.Globalization;
using System.Text.Json.Serialization;

namespace ClaudeBar.Models;

// Deliberately minimal: only the access token is ever parsed from the credentials file —
// the refresh token and the rest of the CLI's credential JSON are ignored.
public readonly record struct ClaudeCredentials(string AccessToken);

// MARK: - OAuth usage endpoint response shapes

public sealed class OAuthUsageResponse
{
    [JsonPropertyName("five_hour")] public OAuthWindow? FiveHour { get; init; }
    [JsonPropertyName("seven_day")] public OAuthWindow? SevenDay { get; init; }
    [JsonPropertyName("limits")] public List<OAuthLimit>? Limits { get; init; }
}

public sealed class OAuthWindow
{
    [JsonPropertyName("utilization")] public double? Utilization { get; init; }
    [JsonPropertyName("resets_at")] public string? ResetsAt { get; init; }
}

/// <summary>
/// One entry of the <c>limits</c> array the usage endpoint began returning with the Sonnet-5
/// rollout. It supersedes the fixed <c>seven_day_sonnet</c>/<c>seven_day_opus</c> keys (now
/// always null): the per-model weekly cap is a single <c>weekly_scoped</c> entry whose
/// <c>scope.model</c> names whichever model you're currently metered against, so we no longer
/// hardcode names.
/// </summary>
public sealed class OAuthLimit
{
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("percent")] public double? Percent { get; init; }
    [JsonPropertyName("resets_at")] public string? ResetsAt { get; init; }
    [JsonPropertyName("scope")] public OAuthScope? Scope { get; init; }
}

public sealed class OAuthScope
{
    [JsonPropertyName("model")] public OAuthModel? Model { get; init; }
}

public sealed class OAuthModel
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
}

public enum WindowKind { Session, Weekly }

/// <summary>
/// Pace verdict for a rate window: how actual consumption compares to an even burn across the
/// window. Single source of truth for the chip, the deviation band, the footer line, and the
/// Recommender, so they can never disagree.
/// </summary>
public enum PaceTier
{
    Early,   // no trustworthy forecast yet
    Idle,    // weekly only: a large share of the paid quota will expire unused
    OnPace,  // projected to land with a healthy margin
    Hot,     // tight margin projected at reset
    RunsOut  // projected to exhaust before reset, or already exhausted
}

public static class PaceTierExtensions
{
    public static string Word(this PaceTier tier) => tier switch
    {
        PaceTier.Early => "Early",
        PaceTier.Idle => "Idle",
        PaceTier.OnPace => "On pace",
        PaceTier.Hot => "Hot",
        PaceTier.RunsOut => "Runs out",
        _ => ""
    };
}

/// <summary>
/// A single rate window (session or weekly). All forecasting math lives here so every surface
/// derives its numbers from one place. Mirrors the Swift RateWindow exactly.
/// </summary>
public sealed class RateWindow
{
    public double UsedPercent { get; }
    public TimeSpan WindowDuration { get; }
    public DateTime? ResetsAt { get; }

    public RateWindow(double usedPercent, TimeSpan windowDuration, DateTime? resetsAt)
    {
        UsedPercent = usedPercent;
        WindowDuration = windowDuration;
        ResetsAt = resetsAt;
    }

    public double RemainingPercent => Math.Max(0, 100 - UsedPercent);

    /// <summary>Seconds until this window resets, or null if unknown.</summary>
    public double? TimeUntilReset =>
        ResetsAt is { } r ? Math.Max(0, (r - DateTime.Now).TotalSeconds) : null;

    /// <summary>Seconds elapsed since the window started (floored at 60s), or null.</summary>
    private double? Elapsed
    {
        get
        {
            if (ResetsAt is not { } r) return null;
            var start = r - WindowDuration;
            return Math.Max(60, (DateTime.Now - start).TotalSeconds);
        }
    }

    public double? WaitToReachProjected(double targetPercent)
    {
        if (Elapsed is not { } elapsed || TimeUntilReset is not { } resetIn) return null;
        var wait = UsedPercent * (elapsed + resetIn) / targetPercent - elapsed;
        return (wait > 60 && wait < resetIn) ? wait : null;
    }

    public double? WaitToStabilize => WaitToReachProjected(100);

    public double? BurnRatePerHour
    {
        get
        {
            if (Elapsed is not { } secs || secs <= 300) return null;
            return UsedPercent / (secs / 3600);
        }
    }

    public double? ProjectedUsageAtReset
    {
        get
        {
            if (BurnRatePerHour is not { } rate || TimeUntilReset is not { } remaining) return null;
            return UsedPercent + rate * (remaining / 3600);
        }
    }

    /// <summary>Share of the window already behind us, 0…1.</summary>
    public double? ElapsedFraction
    {
        get
        {
            if (Elapsed is not { } elapsed || WindowDuration.TotalSeconds <= 0) return null;
            return Math.Min(1, Math.Max(0, elapsed / WindowDuration.TotalSeconds));
        }
    }

    /// <summary>
    /// Unclamped: goes negative when the window is projected to exhaust before reset, so
    /// severity is never silently flattened to a calm "0% left".
    /// </summary>
    public double? ProjectedLeftAtReset =>
        ProjectedUsageAtReset is { } p ? 100 - p : null;

    /// <summary>Seconds until the quota hits 100% at the average burn rate so far.</summary>
    public double? TimeToExhaustion
    {
        get
        {
            if (BurnRatePerHour is not { } rate || rate <= 0) return null;
            return RemainingPercent / rate * 3600;
        }
    }

    public PaceTier PaceTier(WindowKind kind)
    {
        if (ResetsAt is null) return Models.PaceTier.Early;
        // Measured exhaustion is a fact, not a forecast — don't wait out the burn-rate warmup.
        if (RemainingPercent < 5) return Models.PaceTier.RunsOut;
        if (ProjectedUsageAtReset is not { } projected || ElapsedFraction is not { } fraction)
            return Models.PaceTier.Early;
        // Too little of the window elapsed to trust a whole-window average — unless usage is
        // already so high that even an early forecast is clearly real.
        var earlyGate = kind == WindowKind.Session ? 0.10 : 0.05;
        if (fraction < earlyGate && UsedPercent < 50) return Models.PaceTier.Early;
        if (projected >= 100)
        {
            // Anti-flap: minutes after a reset the average blows up on a tiny denominator;
            // don't go red until real usage or time backs it.
            if (UsedPercent < 20 && fraction < 0.10) return Models.PaceTier.Hot;
            return Models.PaceTier.RunsOut;
        }
        if (projected >= 85) return Models.PaceTier.Hot;
        if (kind == WindowKind.Weekly && projected < 60 && fraction >= 0.25) return Models.PaceTier.Idle;
        return Models.PaceTier.OnPace;
    }

    public static RateWindow? From(OAuthWindow? window, double durationHours)
    {
        if (window is null) return null;
        return From(window.Utilization, window.ResetsAt, durationHours);
    }

    /// <summary>
    /// Build from a bare percent + reset string — shared by the legacy top-level windows and the
    /// newer <c>limits</c> entries, which report <c>percent</c> rather than <c>utilization</c>.
    /// </summary>
    public static RateWindow? From(double? percent, string? resetsAt, double durationHours)
    {
        if (percent is not { } p) return null;
        return new RateWindow(p, TimeSpan.FromHours(durationHours), ParseDate(resetsAt));
    }

    private static DateTime? ParseDate(string? str)
    {
        if (string.IsNullOrEmpty(str)) return null;
        // ISO 8601 with or without fractional seconds; normalize to local time.
        if (DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var dto))
            return dto.LocalDateTime;
        return null;
    }
}

public sealed class UsageSnapshot
{
    public RateWindow Session { get; }
    public RateWindow? Weekly { get; }

    /// <summary>
    /// The per-model weekly cap (<c>weekly_scoped</c>), scoped to whichever model the plan is
    /// currently metering — its name lives in <see cref="ScopedModelName"/>. Replaces the old
    /// fixed Sonnet/Opus windows, which the endpoint stopped populating after the Sonnet-5 rollout.
    /// </summary>
    public RateWindow? ScopedWeekly { get; }
    public string? ScopedModelName { get; }
    public DateTime FetchedAt { get; }

    public UsageSnapshot(RateWindow session, RateWindow? weekly, RateWindow? scopedWeekly, string? scopedModelName)
    {
        Session = session;
        Weekly = weekly;
        ScopedWeekly = scopedWeekly;
        ScopedModelName = scopedModelName;
        FetchedAt = DateTime.Now;
    }

    public static UsageSnapshot? From(OAuthUsageResponse response)
    {
        var session = RateWindow.From(response.FiveHour, 5);
        if (session is null) return null;
        var scoped = response.Limits?.FirstOrDefault(l => l.Kind == "weekly_scoped");
        return new UsageSnapshot(
            session,
            RateWindow.From(response.SevenDay, 168),
            RateWindow.From(scoped?.Percent, scoped?.ResetsAt, 168),
            scoped?.Scope?.Model?.DisplayName);
    }
}

public static class Format
{
    public static string Duration(double intervalSeconds)
    {
        if (double.IsNaN(intervalSeconds) || double.IsInfinity(intervalSeconds)) return "—";
        var total = (int)Math.Max(0, intervalSeconds);
        var days = total / 86400;
        var hours = (total % 86400) / 3600;
        var minutes = (total % 3600) / 60;

        if (days > 0) return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
        if (hours > 0) return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        return $"{minutes}m";
    }
}
