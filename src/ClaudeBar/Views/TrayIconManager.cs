using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ClaudeBar.Services;
using ClaudeBar.ViewModels;

namespace ClaudeBar.Views;

/// <summary>
/// The Windows analog of the macOS MenuBarExtra: a system-tray <see cref="NotifyIcon"/> whose icon
/// is drawn on the fly to show the session remaining-% in the status color, with the rich popover
/// on left-click, a fallback context menu on right-click, and limit banners as balloon tips.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly NotifyIcon _notifyIcon;
    private readonly UsageStore _store;
    private readonly AutoUpdater _updater;
    private MenuWindow? _popover;
    private StatisticsWindow? _statistics;
    private IntPtr _lastIconHandle = IntPtr.Zero;

    public TrayIconManager(UsageStore store, AutoUpdater updater)
    {
        _store = store;
        _updater = updater;

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = "Claude Code Limits"
        };
        _notifyIcon.MouseClick += OnMouseClick;
        _notifyIcon.ContextMenuStrip = BuildContextMenu();

        _store.PropertyChanged += OnStoreChanged;
        _store.NotificationRequested += OnNotificationRequested;

        Render();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Statistics", null, (_, _) => ShowStatistics());
        menu.Items.Add("Refresh", null, async (_, _) =>
        {
            await _store.RefreshAsync(force: true);
            // Only surface an available update here; installing it stays an explicit choice (the
            // Update button, or the opt-in Auto Update toggle) — a "Refresh" must never relaunch.
            await _updater.CheckForUpdatesAsync(autoInstall: false);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => System.Windows.Application.Current.Shutdown());
        return menu;
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) TogglePopover();
    }

    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void OnNotificationRequested(string title, string body, bool isCritical)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = body;
        _notifyIcon.BalloonTipIcon = isCritical ? ToolTipIcon.Warning : ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(isCritical ? 10000 : 5000);
    }

    private void TogglePopover()
    {
        if (_popover is { IsVisible: true })
        {
            _popover.Hide();
            return;
        }
        _popover ??= new MenuWindow(_store, _updater, ShowStatistics);
        _popover.ShowNearTray();
    }

    private void ShowStatistics()
    {
        _popover?.Hide();
        if (_statistics is null)
        {
            _statistics = new StatisticsWindow(_store);
            _statistics.Closed += (_, _) => _statistics = null;
            _statistics.Show();
        }
        else
        {
            _statistics.Activate();
        }
        _statistics.WindowState = System.Windows.WindowState.Normal;
    }

    // MARK: - Icon rendering

    private void Render()
    {
        _notifyIcon.Text = Truncate(TooltipText(), 63);
        var icon = BuildIcon();
        var old = _lastIconHandle;
        _notifyIcon.Icon = icon;
        _lastIconHandle = icon.Handle;
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    private string TooltipText()
    {
        if (_store.Snapshot is null)
            return _store.ErrorMessage ?? "Claude Code Limits";
        var rec = _store.Recommendation;
        var parts = new List<string> { _store.MenuBarText };
        if (rec is not null) parts.Add(rec.Headline);
        return string.Join("\n", parts);
    }

    /// <summary>Draw the glanceable tray glyph: a rounded chip in the status color with the label.</summary>
    private Icon BuildIcon()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            var color = UiTheme.DrawingForStatus(_store.Status);
            var label = TrayLabel();

            using var bg = new SolidBrush(color);
            using var path = RoundedRect(new Rectangle(1, 1, size - 2, size - 2), 8);
            g.FillPath(bg, path);

            using var textBrush = new SolidBrush(Color.White);
            // Scale font to the label length so 1–3 glyphs each fill the chip legibly.
            var fontSize = label.Length switch { <= 1 => 18f, 2 => 15f, 3 => 11f, _ => 9f };
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(label, font, textBrush, new RectangleF(0, 0, size, size), fmt);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Short glyph for the tray: session remaining %, or a status letter.</summary>
    private string TrayLabel()
    {
        if (_store.Snapshot is not { } snap) return "C";
        if (_store.IsStale) return "!";
        var remaining = (int)Math.Round(snap.Session.RemainingPercent);
        return remaining.ToString();
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        _store.PropertyChanged -= OnStoreChanged;
        _store.NotificationRequested -= OnNotificationRequested;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        if (_lastIconHandle != IntPtr.Zero) DestroyIcon(_lastIconHandle);
    }
}
