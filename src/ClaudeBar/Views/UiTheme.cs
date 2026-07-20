using System.Windows.Media;
using ClaudeBar.Models;
using ClaudeBar.ViewModels;

namespace ClaudeBar.Views;

/// <summary>
/// Central color mapping so the popover, statistics window, and tray icon all speak the same
/// visual language the macOS app did (green = on track, orange = tight, red = out, blue = headroom).
/// </summary>
public static class UiTheme
{
    // WPF media colors for on-screen UI.
    public static readonly Brush Green = Frozen(0x34, 0xC7, 0x59);
    public static readonly Brush Yellow = Frozen(0xFF, 0xCC, 0x00);
    public static readonly Brush Orange = Frozen(0xFF, 0x8C, 0x00);
    public static readonly Brush Red = Frozen(0xFF, 0x3B, 0x30);
    public static readonly Brush Blue = Frozen(0x0A, 0x84, 0xFF);
    public static readonly Brush Secondary = Frozen(0x8E, 0x8E, 0x93);
    public static readonly Brush Purple = Frozen(0xAF, 0x52, 0xDE);

    public static Brush ForStatus(StatusLevel level) => level switch
    {
        StatusLevel.Green => Green,
        StatusLevel.Yellow => Yellow,
        StatusLevel.Orange => Orange,
        StatusLevel.Red => Red,
        StatusLevel.Blue => Blue,
        _ => Secondary
    };

    public static Brush ForTier(PaceTier tier) => tier switch
    {
        PaceTier.Early => Secondary,
        PaceTier.Idle => Blue,
        PaceTier.OnPace => Green,
        PaceTier.Hot => Orange,
        PaceTier.RunsOut => Red,
        _ => Secondary
    };

    /// <summary>System.Drawing color for the GDI+-rendered tray icon.</summary>
    public static System.Drawing.Color DrawingForStatus(StatusLevel level) => level switch
    {
        StatusLevel.Green => System.Drawing.Color.FromArgb(0x34, 0xC7, 0x59),
        StatusLevel.Yellow => System.Drawing.Color.FromArgb(0xE6, 0xB0, 0x00),
        StatusLevel.Orange => System.Drawing.Color.FromArgb(0xFF, 0x8C, 0x00),
        StatusLevel.Red => System.Drawing.Color.FromArgb(0xFF, 0x3B, 0x30),
        StatusLevel.Blue => System.Drawing.Color.FromArgb(0x0A, 0x84, 0xFF),
        _ => System.Drawing.Color.FromArgb(0xB0, 0xB0, 0xB5)
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
