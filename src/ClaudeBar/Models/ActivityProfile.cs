namespace ClaudeBar.Models;

/// <summary>
/// When the user actually engages Claude Code, derived from ~/.claude/history.jsonl — one entry
/// is appended there per submitted prompt. This is the cheapest, most direct "when do I work"
/// signal: it drives the activity chart and lets the Recommender stop raising alarms about
/// deadlines that land in hours the user is rarely at the keyboard.
/// </summary>
public sealed class ActivityProfile
{
    /// <summary>Prompt count per hour-of-day (0…23), local time. Always 24 entries.</summary>
    public IReadOnlyList<int> HourCounts { get; }

    /// <summary>Prompt count per weekday (index 0 = Sunday … 6 = Saturday) × hour-of-day. 7×24.</summary>
    public IReadOnlyList<IReadOnlyList<int>> WeekdayHourCounts { get; }

    public int TotalPrompts { get; }
    public int DistinctDays { get; }
    public DateTime? FirstPrompt { get; }
    public DateTime? LastPrompt { get; }

    public ActivityProfile(IReadOnlyList<int> hourCounts,
        IReadOnlyList<IReadOnlyList<int>> weekdayHourCounts,
        int totalPrompts, int distinctDays, DateTime? firstPrompt, DateTime? lastPrompt)
    {
        HourCounts = hourCounts;
        WeekdayHourCounts = weekdayHourCounts;
        TotalPrompts = totalPrompts;
        DistinctDays = distinctDays;
        FirstPrompt = firstPrompt;
        LastPrompt = lastPrompt;
    }

    public static readonly ActivityProfile Empty = new(
        new int[24],
        Enumerable.Range(0, 7).Select(_ => (IReadOnlyList<int>)new int[24]).ToArray(),
        0, 0, null, null);

    /// <summary>
    /// An hour counts as "active" once it reaches at least this share of a typical busy hour.
    /// 0.15 cleanly separates a real working hour from the stray late-night one-off.
    /// </summary>
    public const double ActiveThreshold = 0.15;

    /// <summary>
    /// Off-hours are only claimed for a contiguous inactive stretch at least this long, so a
    /// single sparse hour in the middle of the day is never treated as "off-hours".
    /// </summary>
    public const int MinDeadRun = 3;

    /// <summary>
    /// Enough signal to trust the active/dead-hour split. Below this the daily rhythm is noise,
    /// so the Recommender must not suppress anything and the UI shows a caveat.
    /// </summary>
    public bool HasEnoughData => TotalPrompts >= 40 && DistinctDays >= 5;

    public int PeakHourCount => HourCounts.Count == 0 ? 0 : HourCounts.Max();

    /// <summary>
    /// Reference level for "a typical busy hour": the second-highest hourly count, not the max.
    /// A single outlier burst would otherwise inflate the denominator and push genuine working
    /// hours below the threshold, wrongly marking them inactive.
    /// </summary>
    private double ReferenceLevel
    {
        get
        {
            var sorted = HourCounts.OrderByDescending(x => x).ToArray();
            var top = sorted.Length > 0 ? sorted[0] : 0;
            var second = sorted.Length > 1 ? sorted[1] : 0;
            return second > 0 ? second : top;
        }
    }

    /// <summary>
    /// How busy this hour is relative to a typical busy hour, 0…1+ (can exceed 1 for an outlier
    /// hour) — the shape of the daily rhythm.
    /// </summary>
    public double Intensity(int hour)
    {
        var reference = ReferenceLevel;
        if (reference <= 0 || hour < 0 || hour >= 24) return 0;
        return HourCounts[hour] / reference;
    }

    public bool IsActive(int hour) => Intensity(hour) >= ActiveThreshold;

    public ISet<int> ActiveHours => Enumerable.Range(0, 24).Where(IsActive).ToHashSet();

    /// <summary>
    /// Maximal runs of consecutive inactive hours around the 24-hour clock, including a run that
    /// wraps past midnight. Each is (start hour, length). Empty when every hour is active; a
    /// single full-day run when every hour is inactive.
    /// </summary>
    private List<(int start, int len)> InactiveRuns()
    {
        var active = Enumerable.Range(0, 24).Select(IsActive).ToArray();
        var firstActive = -1;
        for (var h = 0; h < 24; h++)
        {
            if (active[h]) { firstActive = h; break; }
        }
        if (firstActive < 0)
            return active.Contains(false) ? new() { (0, 24) } : new();

        var runs = new List<(int start, int len)>();
        var i = 0;
        while (i < 24)
        {
            var hour = (firstActive + i) % 24;
            if (active[hour]) { i++; continue; }
            var len = 0;
            while (i < 24 && !active[(firstActive + i) % 24]) { len++; i++; }
            runs.Add(((firstActive + (i - len)) % 24, len));
        }
        return runs;
    }

    /// <summary>
    /// The hours that belong to a genuine off-hours stretch (a contiguous inactive run of at
    /// least <see cref="MinDeadRun"/> hours). The single source of truth shared by
    /// <see cref="IsDeadTime"/> and <see cref="DeadRangeDescription"/>.
    /// </summary>
    public ISet<int> OffHours
    {
        get
        {
            var hours = new HashSet<int>();
            if (!HasEnoughData) return hours;
            foreach (var run in InactiveRuns().Where(r => r.len >= MinDeadRun))
                for (var k = 0; k < run.len; k++)
                    hours.Add((run.start + k) % 24);
            return hours;
        }
    }

    /// <summary>
    /// True when the given instant lands in an off-hours stretch — the hook the Recommender uses
    /// to discount deadlines (a 5-hour window resetting at 3 a.m. is moot if you stopped at 1 a.m.).
    /// </summary>
    public bool IsDeadTime(DateTime date) => OffHours.Contains(date.Hour);

    /// <summary>
    /// The longest off-hours stretch rendered as a human range like "1:00–11:00" (end = the hour
    /// activity resumes). null when there is no meaningful dead stretch.
    /// </summary>
    public string? DeadRangeDescription
    {
        get
        {
            if (!HasEnoughData) return null;
            var runs = InactiveRuns().Where(r => r.len >= MinDeadRun).ToArray();
            if (runs.Length == 0) return null;
            var longest = runs.Aggregate((a, b) => b.len > a.len ? b : a);
            var endHour = (longest.start + longest.len) % 24;
            return $"{longest.start}:00–{endHour}:00";
        }
    }
}
