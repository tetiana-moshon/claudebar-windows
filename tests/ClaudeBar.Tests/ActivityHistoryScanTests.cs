using System;
using System.IO;
using ClaudeBar.Services;
using Xunit;

namespace ClaudeBar.Tests;

/// <summary>
/// The IO-bounding half of the activity signal, exercised through the internal <c>Load</c> seam: the
/// shared tail-reader, the byte budget, the file cap, the 30-day window, and the newest-first ordering
/// the budget correctness depends on. Before the seam existed <c>Load()</c> read hardcoded
/// <see cref="AppPaths"/>, so a regression here would silently break all activity parsing with the
/// suite still green.
/// </summary>
public sealed class ActivityHistoryScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claudebar-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _projects;
    private readonly string _history;

    public ActivityHistoryScanTests()
    {
        _projects = Path.Combine(_root, "projects");
        _history = Path.Combine(_root, "history.jsonl");
        Directory.CreateDirectory(_projects);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static string UserLine(string iso) =>
        "{\"type\":\"user\",\"timestamp\":\"" + iso + "\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}";

    private string WriteTranscript(string name, string content, DateTime mtime)
    {
        var dir = Path.Combine(_projects, "proj-" + name);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".jsonl");
        File.WriteAllText(path, content);
        File.SetLastWriteTime(path, mtime);
        return path;
    }

    [Fact]
    public void Empty_transcript_file_contributes_nothing_and_does_not_crash()
    {
        WriteTranscript("empty", "", DateTime.Now);
        var profile = ActivityHistory.Load(_projects, _history);
        Assert.Equal(0, profile.TotalPrompts);
    }

    [Fact]
    public void Transcript_larger_than_the_tail_cap_drops_the_partial_first_line_and_counts_the_rest()
    {
        var good = UserLine("2026-09-24T11:00:00Z");
        // A valid first prompt padded so the byte-aligned tail must start inside it.
        var big = UserLine("2026-09-24T10:00:00Z") + new string(' ', 500);
        WriteTranscript("tail", big + "\n" + good, DateTime.Now);

        // Cap the tail just past the last line: the tail begins mid-"big", which must be dropped.
        var tailCap = System.Text.Encoding.UTF8.GetByteCount(good) + 10;
        var profile = ActivityHistory.Load(_projects, _history, transcriptTailBytes: tailCap);

        Assert.Equal(1, profile.TotalPrompts); // only the whole trailing line survives
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 11, 0, 0, TimeSpan.Zero).LocalDateTime, profile.LastPrompt);
    }

    [Fact]
    public void The_byte_budget_stops_the_scan_after_the_freshest_files()
    {
        var newest = WriteTranscript("newest", UserLine("2026-09-24T10:00:00Z"), DateTime.Now);
        WriteTranscript("middle", UserLine("2026-09-23T10:00:00Z"), DateTime.Now.AddDays(-1));
        WriteTranscript("oldest", UserLine("2026-09-22T10:00:00Z"), DateTime.Now.AddDays(-2));

        // A budget of exactly the newest file buys that file and nothing more — proving both that the
        // budget halts the scan and that files are visited newest-first.
        var budget = new FileInfo(newest).Length;
        var profile = ActivityHistory.Load(_projects, _history, transcriptTotalBytes: budget);

        Assert.Equal(1, profile.TotalPrompts);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero).LocalDateTime, profile.LastPrompt);
    }

    [Fact]
    public void A_transcript_older_than_the_window_is_skipped()
    {
        WriteTranscript("recent", UserLine("2026-09-24T09:00:00Z"), DateTime.Now);
        WriteTranscript("stale", UserLine("2026-09-24T08:00:00Z"), DateTime.Now.AddDays(-40));

        var profile = ActivityHistory.Load(_projects, _history); // default 30-day window

        Assert.Equal(1, profile.TotalPrompts);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero).LocalDateTime, profile.LastPrompt);
    }

    [Fact]
    public void A_prompt_in_both_sources_inflates_only_the_raw_total_not_the_distinct_days()
    {
        var instant = new DateTimeOffset(2026, 9, 24, 13, 30, 0, TimeSpan.Zero);
        File.WriteAllText(_history, "{\"timestamp\":" + instant.ToUnixTimeMilliseconds() + "}");
        WriteTranscript("dup", UserLine("2026-09-24T13:30:00Z"), DateTime.Now);

        var profile = ActivityHistory.Load(_projects, _history);

        Assert.Equal(2, profile.TotalPrompts); // both sources counted raw
        Assert.Equal(1, profile.DistinctDays);  // but it is the same day, so the day floor is not double-counted
    }

    [Fact]
    public void A_transcript_nested_below_a_project_folder_is_still_discovered()
    {
        // Proves the per-subfolder recursive walk that replaced the single all-directories GetFiles call.
        var deep = Path.Combine(_projects, "proj-a", "sessions", "2026");
        Directory.CreateDirectory(deep);
        var path = Path.Combine(deep, "deep.jsonl");
        File.WriteAllText(path, UserLine("2026-09-24T07:15:00Z"));
        File.SetLastWriteTime(path, DateTime.Now);

        var profile = ActivityHistory.Load(_projects, _history);

        Assert.Equal(1, profile.TotalPrompts);
    }
}
