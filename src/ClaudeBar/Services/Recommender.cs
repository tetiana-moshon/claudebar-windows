using ClaudeBar.Models;

namespace ClaudeBar.Services;

public enum Urgency { Low = 0, Medium = 1, High = 2, Critical = 3 }

public sealed class Recommendation
{
    public Urgency Urgency { get; init; }
    public string Headline { get; init; } = "";
    public string SessionLine { get; init; } = "";
    public string? WeeklyLine { get; init; }
    public string? ModelLine { get; init; }
    /// <summary>Weekly quota is pacing so low that a large share will expire unused.</summary>
    public bool IsHeadroom { get; init; }

    /// <summary>A named glyph key the UI maps to an icon; mirrors the SF Symbol semantics.</summary>
    public string StatusSymbol => IsHeadroom
        ? "headroom"
        : Urgency switch
        {
            Urgency.Low => "ok",
            Urgency.Medium => "warn",
            Urgency.High => "alert",
            Urgency.Critical => "stop",
            _ => "ok"
        };
}

/// <summary>
/// Turns a usage snapshot into a single recommendation. Pure logic — a faithful port of the
/// macOS Recommender, including the off-hours discounting and headroom framing.
/// </summary>
public sealed class Recommender
{
    private readonly UsageSnapshot _snapshot;
    private readonly bool _newPaceUI;
    private readonly ActivityProfile _activity;

    public Recommender(UsageSnapshot snapshot, bool newPaceUI, ActivityProfile activity)
    {
        _snapshot = snapshot;
        _newPaceUI = newPaceUI;
        _activity = activity;
    }

    private sealed class WindowEval
    {
        public Urgency Urgency { get; init; }
        public string Line { get; init; } = "";
        /// <summary>Set when off-hours discounting downgraded this window.</summary>
        public string? SoftenedReason { get; init; }
    }

    public Recommendation Recommend()
    {
        var sessionRec = EvalSession(_snapshot.Session);
        var weeklyRec = _snapshot.Weekly is { } w ? EvalWeekly(w, "Weekly") : null;
        var modelRec = EvalModels();

        var urgency = new[] { sessionRec.Urgency, weeklyRec?.Urgency, modelRec?.Urgency }
            .Where(u => u.HasValue).Select(u => u!.Value)
            .DefaultIfEmpty(Urgency.Low).Max();

        if (_newPaceUI && urgency == Urgency.Low && _snapshot.Weekly is { } weekly &&
            weekly.PaceTier(WindowKind.Weekly) == PaceTier.Idle &&
            weekly.ProjectedLeftAtReset is { } left)
        {
            return new Recommendation
            {
                Urgency = Urgency.Low,
                Headline = $"Headroom — ~{Round(left)}% of weekly will go unused",
                SessionLine = sessionRec.Line,
                WeeklyLine = weeklyRec?.Line,
                ModelLine = HeadroomModelLine(),
                IsHeadroom = true
            };
        }

        // A high session pace that only bites during the user's off-hours was downgraded to Low
        // in EvalSession; surface its calm explanation instead of the generic "Ready".
        if (urgency == Urgency.Low && sessionRec.SoftenedReason is { } reason)
        {
            return new Recommendation
            {
                Urgency = Urgency.Low,
                Headline = reason,
                SessionLine = sessionRec.Line,
                WeeklyLine = weeklyRec?.Line,
                ModelLine = modelRec?.Line
            };
        }

        string headline;
        switch (urgency)
        {
            case Urgency.Low:
                headline = "Ready — run large tasks";
                break;
            case Urgency.Medium:
                if (sessionRec.Urgency == Urgency.Medium)
                    headline = _snapshot.Session.WaitToReachProjected(80) is { } wait
                        ? $"Ease off for {Format.Duration(wait)}"
                        : "Session running low";
                else if (weeklyRec?.Urgency == Urgency.Medium)
                    headline = _snapshot.Weekly?.WaitToReachProjected(80) is { } wwait
                        ? $"Ease off for {Format.Duration(wwait)}"
                        : "Weekly running low";
                else
                    headline = $"{ScopedModelName} limit low";
                break;
            case Urgency.High:
                var onlyModelsCausedHigh = modelRec?.Urgency == Urgency.High
                    && sessionRec.Urgency < Urgency.High
                    && (weeklyRec?.Urgency ?? Urgency.Low) < Urgency.High;
                if (onlyModelsCausedHigh)
                    headline = $"{ScopedModelName} weekly running low";
                else if (sessionRec.Urgency == Urgency.High)
                {
                    var s = _snapshot.Session;
                    if (_newPaceUI && s.RemainingPercent < 5 && s.TimeUntilReset is { } resetIn)
                        headline = $"Session exhausted — resets in {Format.Duration(resetIn)}";
                    else if (_newPaceUI && s.TimeToExhaustion is { } runOut &&
                             s.TimeUntilReset is { } resetIn2 && resetIn2 > runOut)
                        headline = $"Session runs out {Format.Duration(resetIn2 - runOut)} before reset — ease off";
                    else if (s.WaitToStabilize is { } waitStab)
                        headline = $"Wait {Format.Duration(waitStab)} — session will exhaust";
                    else
                        headline = "Wait — session will exhaust";
                }
                else
                    headline = "Slow down — weekly pace too high";
                break;
            case Urgency.Critical:
            default:
                headline = "Stop — limit exhausted";
                break;
        }

        return new Recommendation
        {
            Urgency = urgency,
            Headline = headline,
            SessionLine = sessionRec.Line,
            WeeklyLine = weeklyRec?.Line,
            ModelLine = modelRec?.Line
        };
    }

    private WindowEval EvalSession(RateWindow w)
    {
        var remaining = w.RemainingPercent;
        var resetIn = w.TimeUntilReset ?? double.PositiveInfinity;
        var tier = w.PaceTier(WindowKind.Session);

        // Will exhaust before reset? — but only once the forecast is trustworthy.
        if (tier != PaceTier.Early && w.ProjectedUsageAtReset is { } projected && projected >= 100)
        {
            if (w.BurnRatePerHour is { } rate && rate > 0)
            {
                var hoursLeft = (100 - w.UsedPercent) / rate;
                var exhaustAt = DateTime.Now.AddSeconds(hoursLeft * 3600);
                if (remaining >= 10 && _activity.IsDeadTime(exhaustAt))
                    return new WindowEval
                    {
                        Urgency = Urgency.Low,
                        Line = "Session pace high, but runs out during your off-hours",
                        SoftenedReason = "Pace high — but you usually stop before it runs out"
                    };
                return new WindowEval { Urgency = Urgency.High, Line = $"Session exhausts in {Format.Duration(hoursLeft * 3600)}" };
            }
            return new WindowEval { Urgency = Urgency.High, Line = "Session will exhaust before reset" };
        }

        // Critically low in absolute terms.
        if (remaining < 10)
        {
            if (resetIn < 15 * 60)
                return new WindowEval { Urgency = Urgency.Medium, Line = $"Session nearly empty, resets in {Format.Duration(resetIn)}" };
            return new WindowEval { Urgency = Urgency.Critical, Line = $"Session exhausted. Resets in {Format.Duration(resetIn)}" };
        }

        // Pace is high but won't exhaust (projected 85–99%).
        if (tier != PaceTier.Early && w.ProjectedUsageAtReset is { } projected2 && projected2 >= 85)
        {
            if (w.ResetsAt is { } resetsAt && _activity.IsDeadTime(resetsAt))
                return new WindowEval
                {
                    Urgency = Urgency.Low,
                    Line = "Session pace high, but resets during your off-hours",
                    SoftenedReason = "Pace high — but it resets while you're away"
                };
            if (w.WaitToReachProjected(80) is { } wait)
                return new WindowEval { Urgency = Urgency.Medium, Line = $"Session pace high — ease off for {Format.Duration(wait)}" };
            return new WindowEval { Urgency = Urgency.Medium, Line = $"Session pace high — {Round(projected2)}% projected" };
        }

        // Running low but pace is fine.
        if (remaining < 25)
        {
            if (resetIn < 30 * 60)
                return new WindowEval { Urgency = Urgency.Low, Line = $"{Pct(remaining)} left, resets in {Format.Duration(resetIn)}" };
            if (w.ProjectedUsageAtReset is null)
                return new WindowEval { Urgency = Urgency.Medium, Line = $"Session: {Pct(remaining)} left ({Format.Duration(resetIn)} until reset)" };
            return new WindowEval { Urgency = Urgency.Low, Line = $"{Pct(remaining)} left · resets in {Format.Duration(resetIn)}" };
        }

        if (resetIn < 20 * 60)
            return new WindowEval { Urgency = Urgency.Low, Line = $"Session resets in {Format.Duration(resetIn)} · {Pct(remaining)} left" };

        return new WindowEval { Urgency = Urgency.Low, Line = $"Session: {Pct(remaining)} left · resets in {Format.Duration(resetIn)}" };
    }

    private WindowEval EvalWeekly(RateWindow w, string label)
    {
        var remaining = w.RemainingPercent;
        var resetIn = w.TimeUntilReset ?? double.PositiveInfinity;
        var tier = w.PaceTier(WindowKind.Weekly);

        if (tier != PaceTier.Early && w.ProjectedUsageAtReset is { } projected)
        {
            if (projected >= 100)
                return new WindowEval { Urgency = Urgency.High, Line = $"{label}: limit will exhaust before reset (pace too high)" };
            return new WindowEval { Urgency = Urgency.Low, Line = $"{label}: {Pct(remaining)} left · resets in {Format.Duration(resetIn)}" };
        }

        if (remaining < 10)
            return new WindowEval { Urgency = Urgency.Critical, Line = $"{label}: {Pct(remaining)} left, resets in {Format.Duration(resetIn)}" };
        if (remaining < 25)
            return new WindowEval { Urgency = Urgency.Medium, Line = $"{label}: {Pct(remaining)} left" };
        return new WindowEval { Urgency = Urgency.Low, Line = $"{label}: {Pct(remaining)} left · resets in {Format.Duration(resetIn)}" };
    }

    /// <summary>
    /// Nudge to spend the per-model weekly capacity that would otherwise expire — only while that
    /// scoped window isn't itself already tight.
    /// </summary>
    private string? HeadroomModelLine()
    {
        if (_snapshot.ScopedWeekly is not { } window) return null;
        var tier = window.PaceTier(WindowKind.Weekly);
        if (tier is PaceTier.Hot or PaceTier.RunsOut) return null;
        return $"Run heavy {ScopedModelName} tasks — this capacity expires";
    }

    private WindowEval? EvalModels()
    {
        if (_snapshot.ScopedWeekly is not { } w) return null;
        var remaining = w.RemainingPercent;
        if (remaining < 10)
            return new WindowEval { Urgency = Urgency.High, Line = $"{ScopedModelName} weekly: {Pct(remaining)} left" };
        if (remaining < 20)
            return new WindowEval { Urgency = Urgency.Medium, Line = $"{ScopedModelName} weekly: {Pct(remaining)} left" };
        return null;
    }

    private string ScopedModelName => _snapshot.ScopedModelName ?? "Model";

    private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
    private static string Pct(double v) => $"{Round(v)}%";
}
