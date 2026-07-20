using System.IO;
using System.Text;
using System.Text.Json;
using ClaudeBar.Models;

namespace ClaudeBar.Services;

public static class ActivityHistory
{
    /// <summary>
    /// Only the tail of history.jsonl is read: the daily rhythm only needs recent activity, and
    /// the file grows unbounded (Claude Code appends one line per prompt forever, and a pasted
    /// block can make a single line multi-KB). Bounds the work regardless of size.
    /// </summary>
    public const long MaxBytes = 1_048_576;

    private static string HistoryPath =>
        Path.Combine(AppPaths.ClaudeDir, "history.jsonl");

    /// <summary>
    /// Parse ~/.claude/history.jsonl into an ActivityProfile. Each line is one submitted prompt
    /// with a <c>timestamp</c> in epoch milliseconds; everything else is ignored. Pure filesystem
    /// work — safe to call from a background task.
    /// </summary>
    public static ActivityProfile Load()
    {
        FileStream stream;
        try
        {
            stream = new FileStream(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch
        {
            return ActivityProfile.Empty;
        }

        using (stream)
        {
            var size = stream.Length;
            var truncated = size > MaxBytes;
            if (truncated) stream.Seek(size - MaxBytes, SeekOrigin.Begin);

            byte[] data;
            try
            {
                var toRead = (int)(truncated ? MaxBytes : size);
                data = new byte[toRead];
                var read = 0;
                while (read < toRead)
                {
                    var n = stream.Read(data, read, toRead - read);
                    if (n == 0) break;
                    read += n;
                }
                if (read != toRead) Array.Resize(ref data, read);
            }
            catch
            {
                return ActivityProfile.Empty;
            }

            // Lossy decode: a byte-aligned tail can start mid-character; we drop the first
            // (partial) line below when truncated, so a stray replacement char never matters.
            var text = Encoding.UTF8.GetString(data);

            var hourCounts = new int[24];
            var weekdayHourCounts = new int[7][];
            for (var i = 0; i < 7; i++) weekdayHourCounts[i] = new int[24];
            var total = 0;
            var dayKeys = new HashSet<DateTime>();
            DateTime? first = null, last = null;

            var lines = text.Split('\n');
            var startIdx = 0;
            if (truncated && lines.Length > 0) startIdx = 1; // drop the partial first line

            for (var li = startIdx; li < lines.Length; li++)
            {
                var line = lines[li];
                if (string.IsNullOrWhiteSpace(line)) continue;
                double ms;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (!doc.RootElement.TryGetProperty("timestamp", out var tsEl) ||
                        tsEl.ValueKind != JsonValueKind.Number)
                        continue;
                    ms = tsEl.GetDouble();
                }
                catch
                {
                    continue;
                }

                var date = DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime;
                var hour = date.Hour;
                var weekday = (int)date.DayOfWeek; // Sunday = 0 … Saturday = 6

                hourCounts[hour]++;
                weekdayHourCounts[weekday][hour]++;
                total++;
                dayKeys.Add(date.Date);
                if (first is null || date < first) first = date;
                if (last is null || date > last) last = date;
            }

            return new ActivityProfile(
                hourCounts,
                weekdayHourCounts.Select(a => (IReadOnlyList<int>)a).ToArray(),
                total,
                dayKeys.Count,
                first,
                last);
        }
    }
}
