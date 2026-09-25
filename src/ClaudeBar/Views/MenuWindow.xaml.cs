using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ClaudeBar.Models;
using ClaudeBar.Services;
using ClaudeBar.ViewModels;

namespace ClaudeBar.Views;

/// <summary>
/// The popover shown on tray left-click — the Windows counterpart of the SwiftUI MenuView. Content
/// is rebuilt imperatively on every store/updater change because the pace bars and colors are all
/// computed from the current snapshot.
/// </summary>
public partial class MenuWindow : Window
{
    private const double InnerWidth = 306;

    private static readonly Brush Primary = Freeze(0xF2, 0xF2, 0xF7);
    private static readonly Brush SecondaryText = Freeze(0x9A, 0x9A, 0xA0);
    private static readonly Brush TrackFill = Freeze(0xFF, 0xFF, 0xFF, 0x20);
    private static readonly Brush DividerBrush = Freeze(0xFF, 0xFF, 0xFF, 0x1A);
    // Cool neutral for the "used" fill: bright enough to read clearly against the faint track, but
    // kept below full opacity so the saturated tier colors (orange/blue/red) stay the loudest
    // element on the bar — used = visible-but-secondary "past", color = the pace signal.
    private static readonly Brush UsedFill = Freeze(0xC0, 0xC0, 0xCA, 0x99);

    private readonly UsageStore _store;
    private readonly AutoUpdater _updater;
    private readonly Action _showStatistics;
    private readonly Action _showPreferences;

    public MenuWindow(UsageStore store, AutoUpdater updater, Action showStatistics, Action showPreferences)
    {
        InitializeComponent();
        _store = store;
        _updater = updater;
        _showStatistics = showStatistics;
        _showPreferences = showPreferences;

        _store.PropertyChanged += OnChanged;
        _updater.PropertyChanged += OnChanged;
        Deactivated += (_, _) => Hide();
        Closed += (_, _) =>
        {
            _store.PropertyChanged -= OnChanged;
            _updater.PropertyChanged -= OnChanged;
        };
        Rebuild();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.Invoke(Rebuild);

    public void ShowNearTray()
    {
        Rebuild();
        // Anchor to the bottom-right of the working area, just above the tray, DPI-scaled.
        var wa = SystemParameters.WorkArea;
        var source = PresentationSource.FromVisual(this);
        var dipX = 1.0; var dipY = 1.0;
        if (source?.CompositionTarget is { } ct)
        {
            dipX = ct.TransformFromDevice.M11;
            dipY = ct.TransformFromDevice.M22;
        }
        // WorkArea is already in DIPs for the primary screen; place with an 8px margin.
        Left = wa.Right - Width - 8;
        Show();
        // Height is known only after layout; nudge up so the whole popover stays on screen.
        UpdateLayout();
        Top = Math.Max(wa.Top + 8, wa.Bottom - ActualHeight - 8);
        Activate();
        Topmost = true;
        Focus();
        _ = _store.RefreshIfStaleAsync();
    }

    private void Rebuild()
    {
        Body.Children.Clear();
        Body.Children.Add(BuildHeader());
        Body.Children.Add(Divider());

        if (_store.Snapshot is { } snap)
        {
            Body.Children.Add(BuildWindowRow("Session (5 hours)", snap.Session, WindowKind.Session));
            if (snap.Weekly is { } weekly)
            {
                Body.Children.Add(ThinDivider());
                Body.Children.Add(BuildWindowRow("Weekly", weekly, WindowKind.Weekly));
            }
            if (snap.ScopedWeekly is { } scoped)
            {
                Body.Children.Add(ThinDivider());
                var title = snap.ScopedModelName is { } m ? $"{m} (weekly)" : "Model (weekly)";
                Body.Children.Add(BuildWindowRow(title, scoped, WindowKind.Weekly));
            }
        }
        else if (_store.ErrorMessage is { } error)
        {
            Body.Children.Add(BuildError(error));
        }
        else
        {
            Body.Children.Add(BuildLoading());
        }

        Body.Children.Add(Divider());
        Body.Children.Add(BuildFooter());
    }

    // MARK: - Header

    private UIElement BuildHeader()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 12, 16, 12) };
        var dot = new Ellipse { Width = 14, Height = 14, Margin = new Thickness(0, 2, 8, 0) };

        string headline;
        string? subline;
        if (_store.IsDegraded)
        {
            dot.Fill = UiTheme.Orange;
            headline = "Data may be outdated";
            subline = _store.ErrorMessage ?? RelativeUpdated();
        }
        else if (_store.Recommendation is { } rec)
        {
            dot.Fill = UiTheme.ForStatus(_store.Status);
            headline = rec.Headline;
            subline = rec.ModelLine;
        }
        else if (_store.IsLoading)
        {
            dot.Fill = SecondaryText;
            headline = "Loading…";
            subline = null;
        }
        else
        {
            dot.Fill = SecondaryText;
            headline = "Claude Code Limits";
            subline = null;
        }

        panel.Children.Add(dot);
        var texts = new StackPanel { Orientation = Orientation.Vertical };
        texts.Children.Add(Text(headline, 13, FontWeights.SemiBold, Primary, wrap: true, maxWidth: 280));
        if (!string.IsNullOrEmpty(subline))
            texts.Children.Add(Text(subline, 11, FontWeights.Normal, SecondaryText, wrap: true, maxWidth: 280));
        panel.Children.Add(texts);
        return panel;
    }

    private string RelativeUpdated() =>
        _store.LastUpdated is { } lu ? $"Updated {Relative(lu)} ago" : "";

    // MARK: - Window row

    private UIElement BuildWindowRow(string title, RateWindow window, WindowKind kind)
    {
        var tier = window.PaceTier(kind);
        var root = new StackPanel { Margin = new Thickness(16, 10, 16, 10) };

        // Title line: name … chip + "N% left"
        var titleGrid = new Grid();
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleText = Text(title, 12, FontWeights.Medium, Primary);
        Grid.SetColumn(titleText, 0);
        titleGrid.Children.Add(titleText);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(BuildChip(tier));
        right.Children.Add(Text($"{(int)Math.Round(window.RemainingPercent)}% left", 11, FontWeights.Normal, Primary,
            mono: true, margin: new Thickness(6, 0, 0, 0)));
        Grid.SetColumn(right, 1);
        titleGrid.Children.Add(right);
        root.Children.Add(titleGrid);

        // Pace bar
        root.Children.Add(BuildPaceBar(window, tier, kind));

        // Footer line: resets in … + forecast
        if (window.TimeUntilReset is { } resetIn)
        {
            var footer = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var resets = Text($"Resets in {Format.Duration(resetIn)}", 10, FontWeights.Normal, SecondaryText);
            Grid.SetColumn(resets, 0);
            footer.Children.Add(resets);
            var (fText, fBrush) = ForecastText(window, tier, kind);
            if (!string.IsNullOrEmpty(fText))
            {
                var forecast = Text(fText, 10, FontWeights.Normal, fBrush);
                forecast.HorizontalAlignment = HorizontalAlignment.Right;
                forecast.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(forecast, 1);
                footer.Children.Add(forecast);
            }
            root.Children.Add(footer);
        }

        return root;
    }

    private UIElement BuildChip(PaceTier tier)
    {
        var brush = UiTheme.ForTier(tier);
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Tint(brush, 0.20),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center
        };
        border.Child = Text(tier.Word(), 9, FontWeights.SemiBold, brush);
        return border;
    }

    private bool _fillBars => Settings.GetBool("fillBarsAsUsed", false);

    private UIElement BuildPaceBar(RateWindow window, PaceTier tier, WindowKind kind)
    {
        var canvas = new Canvas { Width = InnerWidth, Height = 12, Margin = new Thickness(0, 6, 0, 0), ClipToBounds = false };
        const double barY = 3, barH = 6;
        double W = InnerWidth;

        double Clamp(double v) => Math.Min(100, Math.Max(0, v));
        var fillEdge = Clamp(_fillBars ? window.UsedPercent : window.RemainingPercent);
        double? caret = window.ElapsedFraction is { } ef ? Clamp((_fillBars ? ef : 1 - ef) * 100) : null;
        var overshoot = tier == PaceTier.RunsOut && (window.ProjectedUsageAtReset ?? 0) >= 100;

        // Track
        canvas.Children.Add(Bar(0, barY, W, barH, TrackFill));

        if (caret is { } c)
        {
            var lower = Math.Min(fillEdge, c);
            var upper = Math.Max(fillEdge, c);
            var band = BandColor(tier, window, kind);

            var baseWidth = W * (band == null ? fillEdge : lower) / 100;
            var baseBrush = overshoot && !_fillBars ? Tint(UiTheme.Red, 0.35) : UsedFill;
            canvas.Children.Add(Bar(0, barY, baseWidth, barH, baseBrush));

            if (band is not null && upper > lower)
            {
                var bandRect = Bar(W * lower / 100, barY, Math.Max(2, W * (upper - lower) / 100), barH, band);
                canvas.Children.Add(bandRect);
            }

            if (overshoot && !_fillBars)
            {
                // leftover doomed region already tinted via baseBrush; nothing more to add.
            }
            else if (overshoot)
            {
                canvas.Children.Add(Bar(W * upper / 100, barY, W * (100 - upper) / 100, barH, Tint(UiTheme.Red, 0.35)));
            }

            // Even-pace caret
            var caretX = Math.Max(0, Math.Min(W - 2, W * c / 100 - 1));
            canvas.Children.Add(Bar(caretX, 0, 2, 12, Freeze(0xF2, 0xF2, 0xF7, 0xCC)));
        }
        else
        {
            canvas.Children.Add(Bar(0, barY, W * fillEdge / 100, barH, UsedFill));
        }

        return canvas;
    }

    private Brush? BandColor(PaceTier tier, RateWindow window, WindowKind kind)
    {
        switch (tier)
        {
            case PaceTier.Early: return null;
            case PaceTier.Idle:
            case PaceTier.Hot:
            case PaceTier.RunsOut:
                return UiTheme.ForTier(tier);
            case PaceTier.OnPace:
                if (kind == WindowKind.Session)
                    return SessionLateHint(window) ? Tint(UiTheme.Blue, 0.55) : null;
                return UiTheme.ForTier(tier);
            default: return null;
        }
    }

    private static bool SessionLateHint(RateWindow window) =>
        window.TimeUntilReset is { } resetIn && window.ProjectedLeftAtReset is { } left
        && resetIn <= 3600 && left >= 50;

    private (string, Brush) ForecastText(RateWindow window, PaceTier tier, WindowKind kind)
    {
        if (kind == WindowKind.Session && SessionLateHint(window)
            && window.ProjectedLeftAtReset is { } left0 && window.TimeUntilReset is { } resetIn0)
            return ($"~{(int)Math.Round(left0)}% expires in {Format.Duration(resetIn0)} — go big", UiTheme.Blue);

        switch (tier)
        {
            case PaceTier.Early:
                return ("No forecast yet", SecondaryText);
            case PaceTier.Idle:
                return ($"~{(int)Math.Round(window.ProjectedLeftAtReset ?? 0)}% will go unused", UiTheme.Blue);
            case PaceTier.OnPace:
            case PaceTier.Hot:
                var left = Math.Max(0, Math.Round(window.ProjectedLeftAtReset ?? 0));
                return ($"~{(int)left}% left at reset", tier == PaceTier.Hot ? UiTheme.Orange : SecondaryText);
            case PaceTier.RunsOut:
                return (RunsOutCaption(window), UiTheme.Red);
            default:
                return ("", SecondaryText);
        }
    }

    private static string RunsOutCaption(RateWindow window)
    {
        if (window.RemainingPercent < 5)
            return $"Exhausted — resets in {Format.Duration(window.TimeUntilReset ?? 0)}";
        if (window.TimeToExhaustion is { } runOut)
        {
            if (window.TimeUntilReset is { } resetIn && resetIn > runOut)
                return $"Runs out in {Format.Duration(runOut)} — {Format.Duration(resetIn - runOut)} early";
            return $"Runs out in {Format.Duration(runOut)}";
        }
        return "Will exhaust before reset";
    }

    // MARK: - Footer

    private UIElement BuildFooter()
    {
        var panel = new StackPanel { Margin = new Thickness(14, 8, 14, 8) };

        if (BuildUpdateSection() is { } update)
        {
            panel.Children.Add(update);
            panel.Children.Add(Divider(new Thickness(0, 6, 0, 6)));
        }

        // Bottom action row
        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var version = new StackPanel { Orientation = Orientation.Horizontal };
        version.Children.Add(Text($"v{AutoUpdater.CurrentVersion}", 10, FontWeights.Normal, SecondaryText));
        if (_store.LastUpdated is { } lu)
            version.Children.Add(Text($"  {Relative(lu)} ago", 10, FontWeights.Normal,
                _store.IsStale ? UiTheme.Orange : SecondaryText));
        Grid.SetColumn(version, 0);
        actions.Children.Add(version);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(LinkButton("Statistics", () => _showStatistics()));
        buttons.Children.Add(SeparatorDot());
        buttons.Children.Add(LinkButton("Settings", () => _showPreferences()));
        buttons.Children.Add(SeparatorDot());
        buttons.Children.Add(LinkButton("Refresh", async () =>
        {
            await _store.RefreshAsync(force: true);
            // Check only — installing stays explicit (Update button / Auto Update toggle).
            await _updater.CheckForUpdatesAsync(autoInstall: false);
        }, enabled: !_store.IsLoading));
        buttons.Children.Add(SeparatorDot());
        buttons.Children.Add(LinkButton("Quit", () => Application.Current.Shutdown(), brush: SecondaryText));
        Grid.SetColumn(buttons, 1);
        actions.Children.Add(buttons);

        panel.Children.Add(actions);
        return panel;
    }

    private UIElement? BuildUpdateSection()
    {
        if (_updater.IsUpdating && _updater.Progress is { } progress)
            return Text(progress, 11, FontWeights.Normal, SecondaryText);
        if (_updater.UpdateAvailable && _updater.LatestVersion is { } version)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = Text($"v{version} available", 11, FontWeights.Medium, Primary);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);
            var btn = LinkButton("Update", async () => await _updater.PerformUpdateAsync());
            Grid.SetColumn(btn, 1);
            grid.Children.Add(btn);
            return grid;
        }
        if (_updater.Error is { } err)
            return Text(err, 10, FontWeights.Normal, UiTheme.Orange, wrap: false);
        return null;
    }

    // MARK: - Error / loading

    private UIElement BuildError(string message)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 12, 16, 12) };
        panel.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = UiTheme.Orange, Margin = new Thickness(0, 3, 8, 0) });
        panel.Children.Add(Text(message, 11, FontWeights.Normal, SecondaryText, wrap: true, maxWidth: 290));
        return panel;
    }

    private UIElement BuildLoading() =>
        Text("Fetching data…", 12, FontWeights.Normal, SecondaryText, margin: new Thickness(16, 16, 16, 16));

    // MARK: - Element helpers

    private UIElement LinkButton(string label, Action onClick, bool enabled = true, Brush? brush = null)
    {
        var btn = new Button
        {
            Content = new TextBlock { Text = label, FontSize = 11 },
            Foreground = brush ?? UiTheme.Blue,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = new Thickness(4, 0, 4, 0),
            IsEnabled = enabled
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    // Block body (no return value) so this binds to the Action overload — an expression-bodied
    // `() => _ = onClick()` would re-resolve to this Func<Task> overload and recurse infinitely.
    private UIElement LinkButton(string label, Func<Task> onClick, bool enabled = true) =>
        LinkButton(label, (Action)(() => { _ = onClick(); }), enabled);

    private UIElement SeparatorDot() =>
        new TextBlock { Text = "·", FontSize = 11, Foreground = SecondaryText, Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock Text(string text, double size, FontWeight weight, Brush brush,
        bool mono = false, bool wrap = false, double maxWidth = 0, Thickness margin = default)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = margin
        };
        if (mono) tb.FontFamily = new FontFamily("Consolas");
        if (wrap) tb.TextWrapping = TextWrapping.Wrap;
        if (maxWidth > 0) tb.MaxWidth = maxWidth;
        return tb;
    }

    private static Rectangle Bar(double x, double y, double w, double h, Brush fill)
    {
        var r = new Rectangle { Width = Math.Max(0, w), Height = h, Fill = fill, RadiusX = 2, RadiusY = 2 };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        return r;
    }

    private static UIElement Divider() => Divider(new Thickness(0));
    private static UIElement Divider(Thickness margin) =>
        new Border { Height = 1, Background = DividerBrush, Margin = margin };
    private static UIElement ThinDivider() =>
        new Border { Height = 1, Background = DividerBrush, Margin = new Thickness(16, 0, 16, 0) };

    private static string Relative(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span.TotalSeconds < 60) return $"{Math.Max(1, (int)span.TotalSeconds)}s";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
        return $"{(int)span.TotalDays}d";
    }

    private static Brush Tint(Brush brush, double opacity)
    {
        if (brush is SolidColorBrush scb)
        {
            var c = scb.Color;
            var b = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), c.R, c.G, c.B));
            b.Freeze();
            return b;
        }
        return brush;
    }

    private static Brush Freeze(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
