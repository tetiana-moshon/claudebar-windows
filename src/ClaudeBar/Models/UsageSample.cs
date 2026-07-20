using System.Text.Json.Serialization;

namespace ClaudeBar.Models;

/// <summary>
/// One persisted observation of quota utilization. Appended on each successful refresh so the
/// Statistics window can chart how the 5-hour and weekly windows burn over time — data the OAuth
/// endpoint only ever exposes as a single current value, never as a history.
/// </summary>
public sealed class UsageSample
{
    [JsonPropertyName("t")] public double T { get; init; }              // epoch seconds
    [JsonPropertyName("session")] public double Session { get; init; }  // % of the 5-hour window used
    [JsonPropertyName("weekly")] public double? Weekly { get; init; }   // % of the weekly window used
    [JsonPropertyName("scoped")] public double? Scoped { get; init; }   // % of the per-model window
    [JsonPropertyName("scopedModel")] public string? ScopedModel { get; init; } // e.g. "Fable"

    // Legacy per-model fields: kept only so pre-Sonnet-5 history still decodes and charts. New
    // samples never write them (the endpoint stopped populating the fixed windows).
    [JsonPropertyName("sonnet")] public double? Sonnet { get; init; }
    [JsonPropertyName("opus")] public double? Opus { get; init; }

    [JsonIgnore]
    public DateTime Date => DateTimeOffset.FromUnixTimeMilliseconds((long)(T * 1000)).LocalDateTime;
}
