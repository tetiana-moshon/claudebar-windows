using Microsoft.Win32;

namespace ClaudeBar.Services;

/// <summary>
/// Launch-at-login via the per-user Run key (HKCU\Software\Microsoft\Windows\CurrentVersion\Run).
/// The Windows equivalent of the macOS SMAppService/LaunchAgent registration. Per-user so it
/// needs no elevation.
/// </summary>
public static class LaunchAtLogin
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeBar";

    private static string ExecutablePath =>
        Environment.ProcessPath
        ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
        ?? "";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                var value = key?.GetValue(ValueName) as string;
                return !string.IsNullOrEmpty(value);
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        // Quote the path so a Program Files-style path with spaces still launches correctly.
        key.SetValue(ValueName, $"\"{ExecutablePath}\"");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
