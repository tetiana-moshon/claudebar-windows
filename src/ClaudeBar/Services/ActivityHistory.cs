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

    // MARK: - Project-transcript scan bounds
    //
    // ~/.claude/projects can be gigabytes across thousands of session files, so the scan is bounded
    // on three axes: only files touched inside the window matter to a *recent* rhythm; only a tail
    // of each is read (the tail is the session's latest activity); and a global byte budget caps the
    // total IO. Files are visited newest-first, so the budget always buys the most recent activity.

    /// <summary>Ignore transcripts not written to within this window — older rhythm is irrelevant.</summary>
    private static readonly TimeSpan TranscriptWindow = TimeSpan.FromDays(30);

    /// <summary>At most this many (newest) transcript files are considered.</summary>
    private const int MaxTranscriptFiles = 400;

    /// <summary>Read at most this much from the end of each transcript.</summary>
    private const long MaxTranscriptTailBytes = 262_144; // 256 KB

    /// <summary>Stop reading transcripts once this much has been read in total.</summary>
    private const long MaxTranscriptTotalBytes = 48L * 1024 * 1024; // 48 MB

    /// <summary>
    /// Build an <see cref="ActivityProfile"/> from every "when did I engage Claude Code" signal on
    /// disk: the CLI's history.jsonl plus the session transcripts under ~/.claude/projects (which the
    /// desktop app writes even though it never touches history.jsonl). Pure filesystem work — safe to
    /// call from a background task. The two sources are summed; a prompt counted by both (a CLI prompt
    /// present in history.jsonl and again in its transcript) only inflates the raw count, which the
    /// rhythm normalizes away — every downstream signal is a ratio or a set except the data-sufficiency
    /// floor, which it can only help clear.
    /// </summary>
    public static ActivityProfile Load()
    {
        var tally = new Tally();
        AddHistoryFile(tally);
        AddProjectTranscripts(tally);
        return tally.ToProfile();
    }

    // MARK: - Source: the CLI history.jsonl (epoch-millisecond timestamps)

    private static void AddHistoryFile(Tally tally)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(AppPaths.HistoryFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch
        {
            return;
        }

        using (stream)
        {
            if (ReadTail(stream, MaxBytes, out var text, out var truncated) is false) return;

            var lines = text.Split('\n');
            var startIdx = truncated && lines.Length > 0 ? 1 : 0; // drop the partial first line
            for (var li = startIdx; li < lines.Length; li++)
            {
                var line = lines[li];
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (!doc.RootElement.TryGetProperty("timestamp", out var tsEl) ||
                        tsEl.ValueKind != JsonValueKind.Number)
                        continue;
                    var date = DateTimeOffset.FromUnixTimeMilliseconds((long)tsEl.GetDouble()).LocalDateTime;
                    tally.Add(date);
                }
                catch
                {
                    // ignore an unparsable line
                }
            }
        }
    }

    // MARK: - Source: the project session transcripts (ISO-8601 string timestamps)

    private static void AddProjectTranscripts(Tally tally)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(AppPaths.ProjectsDir)) return;
            files = Directory.GetFiles(AppPaths.ProjectsDir, "*.jsonl", SearchOption.AllDirectories);
        }
        catch
        {
            return;
        }

        var cutoff = DateTime.Now - TranscriptWindow;
        var recent = files
            .Select(path => { try { return (path, mtime: File.GetLastWriteTime(path)); } catch { return (path, mtime: DateTime.MinValue); } })
            .Where(f => f.mtime >= cutoff)
            .OrderByDescending(f => f.mtime) // newest first, so the byte budget buys the freshest rhythm
            .Take(MaxTranscriptFiles)
            .ToArray();

        long budget = MaxTranscriptTotalBytes;
        foreach (var (path, _) in recent)
        {
            if (budget <= 0) break;
            var cap = Math.Min(MaxTranscriptTailBytes, budget);
            FileStream stream;
            try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
            catch { continue; }
            using (stream)
            {
                if (ReadTail(stream, cap, out var text, out var truncated) is false) continue;
                budget -= Encoding.UTF8.GetByteCount(text);
                AddTranscriptText(tally, text, truncated);
            }
        }
    }

    private static void AddTranscriptText(Tally tally, string text, bool truncated)
    {
        foreach (var date in ParseTranscriptPromptTimes(text, truncated))
            tally.Add(date);
    }

    /// <summary>
    /// The human-prompt timestamps in a transcript tail, in file order. A prompt line is
    /// <c>type:"user"</c> and carries no <c>tool_result</c>; the tool-result echoes (also
    /// <c>type:"user"</c>) and the assistant/tool traffic all happen inside a session the user already
    /// started, so counting them would smear a long autonomous run across hours the user was away —
    /// exactly the off-hours the profile exists to detect. A cheap substring gate keeps the JSON parse
    /// off the vast majority of lines. Pure and allocation-light so it can be unit-tested directly.
    /// </summary>
    internal static List<DateTime> ParseTranscriptPromptTimes(string text, bool truncated)
    {
        var times = new List<DateTime>();
        var lines = text.Split('\n');
        var startIdx = truncated && lines.Length > 0 ? 1 : 0; // drop the partial first line
        for (var li = startIdx; li < lines.Length; li++)
        {
            var line = lines[li];
            if (line.Length == 0) continue;
            if (line.IndexOf("\"type\":\"user\"", StringComparison.Ordinal) < 0) continue;
            if (line.IndexOf("tool_result", StringComparison.Ordinal) >= 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("timestamp", out var tsEl) ||
                    tsEl.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(tsEl.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal, out var dto))
                    continue;
                times.Add(dto.LocalDateTime);
            }
            catch
            {
                // ignore an unparsable line
            }
        }
        return times;
    }

    // MARK: - Shared helpers

    /// <summary>
    /// Read at most <paramref name="cap"/> bytes from the end of a stream and UTF-8 decode them.
    /// <paramref name="truncated"/> is true when the file was longer than the cap, so the caller must
    /// drop the (possibly partial) first line. Returns false only when nothing could be read.
    /// </summary>
    private static bool ReadTail(FileStream stream, long cap, out string text, out bool truncated)
    {
        text = "";
        truncated = false;
        try
        {
            var size = stream.Length;
            truncated = size > cap;
            if (truncated) stream.Seek(size - cap, SeekOrigin.Begin);
            var toRead = (int)(truncated ? cap : size);
            if (toRead <= 0) return false;

            var data = new byte[toRead];
            var read = 0;
            while (read < toRead)
            {
                var n = stream.Read(data, read, toRead - read);
                if (n == 0) break;
                read += n;
            }
            if (read != toRead) Array.Resize(ref data, read);
            // Lossy decode: a byte-aligned tail can start mid-character, but the partial first line
            // is dropped by the caller, so a stray replacement char never reaches the tally.
            text = Encoding.UTF8.GetString(data);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Mutable accumulator so both sources fold into one profile.</summary>
    private sealed class Tally
    {
        private readonly int[] _hourCounts = new int[24];
        private readonly int[][] _weekdayHourCounts = Enumerable.Range(0, 7).Select(_ => new int[24]).ToArray();
        private int _total;
        private readonly HashSet<DateTime> _dayKeys = new();
        private DateTime? _first, _last;

        public void Add(DateTime date)
        {
            var hour = date.Hour;
            _hourCounts[hour]++;
            _weekdayHourCounts[(int)date.DayOfWeek][hour]++;
            _total++;
            _dayKeys.Add(date.Date);
            if (_first is null || date < _first) _first = date;
            if (_last is null || date > _last) _last = date;
        }

        public ActivityProfile ToProfile() => new(
            _hourCounts,
            _weekdayHourCounts.Select(a => (IReadOnlyList<int>)a).ToArray(),
            _total,
            _dayKeys.Count,
            _first,
            _last);
    }
}
