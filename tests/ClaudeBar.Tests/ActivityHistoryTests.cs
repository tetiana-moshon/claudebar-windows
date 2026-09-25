using System;
using System.Linq;
using ClaudeBar.Services;
using Xunit;

namespace ClaudeBar.Tests;

/// <summary>
/// The transcript-parsing half of the activity signal: which lines under ~/.claude/projects count as
/// "the human engaged Claude Code". This is the source that lets the profile see desktop-app usage,
/// which never reaches history.jsonl.
/// </summary>
public class ActivityHistoryTests
{
    private static string Line(string type, string ts, string extra = "") =>
        $$"""{"type":"{{type}}","timestamp":"{{ts}}"{{extra}}}""";

    [Fact]
    public void Counts_a_user_prompt_line()
    {
        var text = Line("user", "2026-09-24T10:15:00.000Z", ",\"message\":{\"role\":\"user\",\"content\":\"hi\"}");
        var times = ActivityHistory.ParseTranscriptPromptTimes(text, truncated: false);
        Assert.Single(times);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 10, 15, 0, TimeSpan.Zero).LocalDateTime, times[0]);
    }

    [Fact]
    public void Ignores_assistant_and_other_non_user_lines()
    {
        var text = string.Join("\n",
            Line("assistant", "2026-09-24T10:00:00Z"),
            Line("summary", "2026-09-24T10:01:00Z"),
            "{\"type\":\"queue-operation\",\"operation\":\"dequeue\",\"timestamp\":\"2026-09-24T10:02:00Z\"}");
        Assert.Empty(ActivityHistory.ParseTranscriptPromptTimes(text, truncated: false));
    }

    [Fact]
    public void Ignores_tool_result_echoes_even_though_they_are_type_user()
    {
        // A tool result comes back as a type:"user" message — but the user wasn't at the keyboard,
        // so it must not count as activity (or a long autonomous run would forge off-hours activity).
        var text = Line("user", "2026-09-24T03:30:00Z",
            ",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"content\":\"ok\"}]}");
        Assert.Empty(ActivityHistory.ParseTranscriptPromptTimes(text, truncated: false));
    }

    [Fact]
    public void Counts_a_prompt_whose_text_merely_mentions_tool_result()
    {
        // The tool-result exclusion is structural (a tool_result content block), not a substring — a
        // genuine prompt that only talks about "tool_result" must still count.
        var text = Line("user", "2026-09-24T12:00:00Z",
            ",\"message\":{\"role\":\"user\",\"content\":\"how do I read a tool_result?\"}");
        var times = ActivityHistory.ParseTranscriptPromptTimes(text, truncated: false);
        Assert.Single(times);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero).LocalDateTime, times[0]);
    }

    [Fact]
    public void Drops_the_partial_first_line_when_truncated()
    {
        // A byte-aligned tail can start mid-line; that fragment must be dropped, not misparsed.
        var text = "5:00.000Z\",\"junk\":true}\n" + Line("user", "2026-09-24T11:00:00Z");
        var times = ActivityHistory.ParseTranscriptPromptTimes(text, truncated: true);
        Assert.Single(times);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 11, 0, 0, TimeSpan.Zero).LocalDateTime, times[0]);
    }

    [Fact]
    public void Skips_lines_without_a_string_timestamp()
    {
        var text = string.Join("\n",
            "{\"type\":\"user\",\"message\":{\"content\":\"no timestamp here\"}}",
            "{\"type\":\"user\",\"timestamp\":123456789}",           // numeric, not the ISO string shape
            "not json at all",
            Line("user", "2026-09-24T09:45:00Z"));
        var times = ActivityHistory.ParseTranscriptPromptTimes(text, truncated: false);
        Assert.Single(times);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 9, 45, 0, TimeSpan.Zero).LocalDateTime, times[0]);
    }
}
