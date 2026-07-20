using System.IO;
using System.Text.Json;

namespace ClaudeBar.Services;

/// <summary>
/// Tiny persisted key/value store — the Windows stand-in for the macOS UserDefaults /
/// <c>@AppStorage</c> the original used for toggles and the rate-limit backoff state. Backed by a
/// JSON file in %APPDATA%\ClaudeBar so settings survive restarts. Keys are unprefixed here (the
/// macOS keys carried a "com.claudebar." prefix that has no meaning on Windows).
/// </summary>
public static class Settings
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(AppPaths.DataDir, "settings.json");
    private static Dictionary<string, JsonElement> _cache = Load();

    private static Dictionary<string, JsonElement> Load()
    {
        try
        {
            var text = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static void Save()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cache));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Non-fatal: settings are convenience state.
        }
    }

    public static bool GetBool(string key, bool defaultValue)
    {
        lock (Gate)
        {
            if (_cache.TryGetValue(key, out var el) &&
                (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
                return el.GetBoolean();
            return defaultValue;
        }
    }

    public static void SetBool(string key, bool value)
    {
        lock (Gate)
        {
            _cache[key] = JsonSerializer.SerializeToElement(value);
            Save();
        }
    }

    public static double? GetDouble(string key)
    {
        lock (Gate)
        {
            if (_cache.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Number)
                return el.GetDouble();
            return null;
        }
    }

    public static void SetDouble(string key, double value)
    {
        lock (Gate)
        {
            _cache[key] = JsonSerializer.SerializeToElement(value);
            Save();
        }
    }

    public static int GetInt(string key, int defaultValue)
    {
        lock (Gate)
        {
            if (_cache.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Number)
                return el.GetInt32();
            return defaultValue;
        }
    }

    public static void SetInt(string key, int value)
    {
        lock (Gate)
        {
            _cache[key] = JsonSerializer.SerializeToElement(value);
            Save();
        }
    }

    public static void Remove(string key)
    {
        lock (Gate)
        {
            if (_cache.Remove(key)) Save();
        }
    }
}
