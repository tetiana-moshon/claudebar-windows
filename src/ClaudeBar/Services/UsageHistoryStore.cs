using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using ClaudeBar.Models;

namespace ClaudeBar.Services;

/// <summary>
/// Append-only log of <see cref="UsageSample"/>s at
/// %APPDATA%\ClaudeBar\usage-history.jsonl. There is no retroactive data — the quota-burn chart
/// fills in as ClaudeBar keeps running.
/// </summary>
public sealed class UsageHistoryStore : INotifyPropertyChanged
{
    private readonly List<UsageSample> _samples = new();
    public IReadOnlyList<UsageSample> Samples => _samples;

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly string _filePath;

    /// <summary>
    /// Record at most one sample per this interval. Activity-driven refreshes can fire every ~45s;
    /// without this the log would bloat without adding any shape to the curve.
    /// </summary>
    private static readonly TimeSpan MinRecordInterval = TimeSpan.FromMinutes(4);

    /// <summary>Keep at most this much history; older samples are pruned when the file is loaded.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOpts = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public UsageHistoryStore()
    {
        _filePath = AppPaths.UsageHistoryFile;
        Load();
    }

    /// <summary>Append a sample for this snapshot unless the previous one is too recent.</summary>
    public void Record(UsageSnapshot snapshot)
    {
        var sample = new UsageSample
        {
            T = ((DateTimeOffset)snapshot.FetchedAt.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0,
            Session = snapshot.Session.UsedPercent,
            Weekly = snapshot.Weekly?.UsedPercent,
            Scoped = snapshot.ScopedWeekly?.UsedPercent,
            ScopedModel = snapshot.ScopedModelName,
            Sonnet = null,
            Opus = null
        };

        if (_samples.Count > 0)
        {
            var last = _samples[^1];
            if (snapshot.FetchedAt - last.Date < MinRecordInterval)
            {
                // Keep an open Statistics window current without bloating the persisted log.
                // Retain the previous timestamp so the next observation after the four-minute
                // boundary is still appended to disk instead of postponing persistence forever.
                _samples[^1] = new UsageSample
                {
                    T = last.T,
                    Session = sample.Session,
                    Weekly = sample.Weekly,
                    Scoped = sample.Scoped,
                    ScopedModel = sample.ScopedModel,
                    Sonnet = sample.Sonnet,
                    Opus = sample.Opus
                };
                OnChanged();
                return;
            }
        }

        _samples.Add(sample);
        Append(sample);
        OnChanged();
    }

    public IReadOnlyList<UsageSample> SamplesSince(DateTime since) =>
        _samples.Where(s => s.Date >= since).ToArray();

    private void OnChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Samples)));

    // MARK: - Persistence

    private void Load()
    {
        string text;
        try { text = File.ReadAllText(_filePath); }
        catch { return; }

        var cutoff = DateTime.Now - Retention;
        var loaded = new List<UsageSample>();
        var lineCount = 0;
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            lineCount++;
            UsageSample? sample;
            try { sample = JsonSerializer.Deserialize<UsageSample>(line); }
            catch { continue; }
            if (sample is not null && sample.Date >= cutoff) loaded.Add(sample);
        }

        _samples.Clear();
        _samples.AddRange(loaded.OrderBy(s => s.T));
        // Compact the file if we dropped expired or unparsable lines, keeping it bounded.
        if (_samples.Count != lineCount) Rewrite();
    }

    private void Append(UsageSample sample)
    {
        try
        {
            var line = JsonSerializer.Serialize(sample, JsonOpts) + "\n";
            File.AppendAllText(_filePath, line, new UTF8Encoding(false));
        }
        catch
        {
            // A transient write failure must not corrupt the accumulated history.
        }
    }

    private void Rewrite()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var s in _samples)
                sb.Append(JsonSerializer.Serialize(s, JsonOpts)).Append('\n');
            // Atomic-ish: write to a temp file then move over, so an interrupted write never
            // leaves a truncated file in place of the accumulated history.
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch
        {
            // ignore
        }
    }
}
