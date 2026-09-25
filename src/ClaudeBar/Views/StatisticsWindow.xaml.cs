using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using ClaudeBar.Models;
using ClaudeBar.ViewModels;

namespace ClaudeBar.Views;

/// <summary>
/// The Statistics window — a port of the SwiftUI StatisticsContent. Two tabs: Usage (current
/// quota cards + a hand-drawn quota-burn line chart) and Activity (an hourly bar chart and a
/// weekday×hour heatmap). Charts are drawn on a <see cref="Canvas"/> since WPF has no built-in
/// charting, matching the original's custom rendering.
/// </summary>
public partial class StatisticsWindow : Window
{
    private enum Tab { Usage, Activity }
    private enum Range { SevenDays, FourteenDays, ThirtyDays }

    private static readonly Brush Primary = Freeze(0xF2, 0xF2, 0xF7);
    private static readonly Brush Secondary = Freeze(0x9A, 0x9A, 0xA0);
    private static readonly Brush CardBg = Freeze(0xFF, 0xFF, 0xFF, 0x12);
    private static readonly Brush CardBorder = Freeze(0xFF, 0xFF, 0xFF, 0x1F);
    private static readonly Brush GridLine = Freeze(0xFF, 0xFF, 0xFF, 0x1A);

    private static readonly string[] WeekdayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
    private static readonly int[] WeekdayOrder = { 1, 2, 3, 4, 5, 6, 0 };

    private readonly UsageStore _store;
    private Tab _tab = Tab.Usage;
    private Range _range = Range.FourteenDays;

    public StatisticsWindow(UsageStore store)
    {
        InitializeComponent();
        _store = store;
        _store.PropertyChanged += OnChanged;
        _store.History.PropertyChanged += OnChanged;
        SizeChanged += (_, _) => Rebuild();
        Closed += (_, _) =>
        {
            _store.PropertyChanged -= OnChanged;
            _store.History.PropertyChanged -= OnChanged;
        };
        UsageTabBtn.IsChecked = true;
        Rebuild();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.Invoke(Rebuild);

    private void OnUsageTab(object sender, RoutedEventArgs e) { _tab = Tab.Usage; SyncTabs(); Rebuild(); }
    private void OnActivityTab(object sender, RoutedEventArgs e) { _tab = Tab.Activity; SyncTabs(); Rebuild(); }

    private void SyncTabs()
    {
        UsageTabBtn.IsChecked = _tab == Tab.Usage;
        ActivityTabBtn.IsChecked = _tab == Tab.Activity;
    }

    private double ContentWidth => Math.Max(400, ActualWidth - 48 /* margins */ - 20 /* scrollbar */);

    private void Rebuild()
    {
        ContentHost.Children.Clear();
        SubtitleText.Text = _store.LastUpdated is { } lu
            ? $"Live usage updated {Relative(lu)} ago"
            : "Waiting for the first usage update";

        if (_tab == Tab.Usage) BuildUsageTab();
        else BuildActivityTab();
    }

    // MARK: - Usage tab

    private void BuildUsageTab()
    {
        // Current usage cards
        if (_store.Snapshot is { } snap)
        {
            var cards = new UniformGrid { Rows = 1, Margin = new Thickness(0, 0, 0, 22) };
            cards.Children.Add(UsageCard("Session", "5 hours", snap.Session, UiTheme.Blue));
            if (snap.Weekly is { } weekly) cards.Children.Add(UsageCard("Weekly", "7 days", weekly, UiTheme.Green));
            if (snap.ScopedWeekly is { } scoped)
                cards.Children.Add(UsageCard(snap.ScopedModelName ?? "Model", "7 days", scoped, UiTheme.Purple));
            ContentHost.Children.Add(cards);
        }
        else
        {
            ContentHost.Children.Add(EmptyState("Waiting for current quota data."));
        }

        // Quota burn header + range selector
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = SectionHeader("Quota burn");
        Grid.SetColumn(title, 0);
        header.Children.Add(title);
        var ranges = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        ranges.Children.Add(RangeButton("7d", Range.SevenDays));
        ranges.Children.Add(RangeButton("14d", Range.FourteenDays));
        ranges.Children.Add(RangeButton("30d", Range.ThirtyDays));
        Grid.SetColumn(ranges, 1);
        header.Children.Add(ranges);
        ContentHost.Children.Add(header);

        var series = BuildBurnSeries();
        if (series.Values.Sum(v => v.Count) < 2 || series.Count == 0)
        {
            ContentHost.Children.Add(EmptyState("Collecting history. The graph fills in while ClaudeBar runs."));
        }
        else
        {
            ContentHost.Children.Add(BuildBurnChart(series));
            ContentHost.Children.Add(new TextBlock
            {
                Text = "Lines show percent used. Drops are quota-window resets; breaks are spans when ClaudeBar wasn't running; dots are the latest recorded values.",
                FontSize = 11, Foreground = Secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
            });
        }
    }

    private UIElement UsageCard(string title, string subtitle, RateWindow window, Brush color)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 0),
            Padding = new Thickness(16),
            MinHeight = 112
        };
        var panel = new StackPanel();

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Primary });
        titles.Children.Add(new TextBlock { Text = subtitle, FontSize = 10, Foreground = Secondary });
        Grid.SetColumn(titles, 0);
        top.Children.Add(titles);
        var pct = new TextBlock { Text = $"{(int)Math.Round(window.UsedPercent)}%", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = color };
        Grid.SetColumn(pct, 1);
        top.Children.Add(pct);
        panel.Children.Add(top);

        var track = new Grid { Height = 6, Margin = new Thickness(0, 12, 0, 12) };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = GridLine });
        var used = Math.Min(100, Math.Max(0, window.UsedPercent));
        track.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(3), Background = color,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = double.NaN,
            Margin = new Thickness(0)
        });
        // Width set after layout via a Viewbox-free proportional grid:
        var fillHost = new Grid();
        fillHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(used, GridUnitType.Star) });
        fillHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 100 - used), GridUnitType.Star) });
        var fill = new Border { CornerRadius = new CornerRadius(3), Background = color };
        Grid.SetColumn(fill, 0);
        fillHost.Children.Add(fill);
        track.Children.Clear();
        track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = GridLine });
        track.Children.Add(fillHost);
        panel.Children.Add(track);

        var bottom = new Grid();
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var usedLabel = new TextBlock { Text = "used", FontSize = 11, Foreground = Secondary };
        Grid.SetColumn(usedLabel, 0);
        bottom.Children.Add(usedLabel);
        var leftLabel = new TextBlock { Text = $"{(int)Math.Round(window.RemainingPercent)}% left", FontSize = 11, Foreground = Secondary };
        Grid.SetColumn(leftLabel, 1);
        bottom.Children.Add(leftLabel);
        panel.Children.Add(bottom);

        border.Child = panel;
        return border;
    }

    // Burn chart data

    /// <summary>
    /// Break the burn line when consecutive samples sit farther apart than this. ClaudeBar records
    /// roughly every five minutes while running, so a larger gap means it simply wasn't running
    /// (machine asleep or off) and there is no data to connect. Drawing a straight line across the
    /// gap would imply steady usage that never happened — the single biggest source of a misleading
    /// chart when the app has been off for a while.
    /// </summary>
    private static readonly TimeSpan GapThreshold = TimeSpan.FromMinutes(20);

    private static readonly Brush[] SeriesPalette =
    {
        Freeze(0x0A, 0x84, 0xFF), // blue - session
        Freeze(0x34, 0xC7, 0x59), // green - weekly
        Freeze(0xAF, 0x52, 0xDE), // purple - scoped
        Freeze(0xFF, 0x9F, 0x0A), // orange
        Freeze(0xFF, 0x6B, 0x6B), // red-ish
    };

    private Dictionary<string, List<(DateTime date, double pct)>> BuildBurnSeries()
    {
        var cutoff = DateTime.Now - RangeInterval(_range);
        var result = new Dictionary<string, List<(DateTime date, double pct)>>();

        void Add(string key, DateTime d, double p)
        {
            if (!result.TryGetValue(key, out var list)) { list = new(); result[key] = list; }
            list.Add((d, p));
        }

        foreach (var s in _store.History.Samples.Where(s => s.Date >= cutoff))
        {
            Add("Session (5h)", s.Date, s.Session);
            if (s.Weekly is { } w) Add("Weekly", s.Date, w);
            if (s.Opus is { } o) Add("Opus (7d)", s.Date, o);
            if (s.Sonnet is { } so) Add("Sonnet (7d)", s.Date, so);
            if (s.Scoped is { } sc) Add(s.ScopedModel is { } m ? $"{m} (7d)" : "Model (7d)", s.Date, sc);
        }
        foreach (var list in result.Values) list.Sort((a, b) => a.date.CompareTo(b.date));
        return result;
    }

    private UIElement BuildBurnChart(Dictionary<string, List<(DateTime date, double pct)>> series)
    {
        var width = ContentWidth;
        const double height = 290;
        const double padL = 38, padR = 12, padT = 24, padB = 26;
        var plotW = Math.Max(50, width - padL - padR);
        var plotH = height - padT - padB;

        var allDates = series.Values.SelectMany(v => v.Select(p => p.date)).ToArray();
        var lo = allDates.Min();
        var hi = allDates.Max();
        var span = (hi - lo).TotalSeconds;
        if (span <= 0) span = 1;

        double X(DateTime d) => padL + plotW * ((d - lo).TotalSeconds / span);
        double Y(double pct) => padT + plotH * (1 - Math.Min(100, Math.Max(0, pct)) / 100.0);

        var container = new StackPanel();

        // Legend
        var legend = new WrapPanel { Margin = new Thickness(padL, 0, 0, 6) };
        var idx = 0;
        foreach (var key in series.Keys)
        {
            var brush = SeriesPalette[idx % SeriesPalette.Length];
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
            item.Children.Add(new Rectangle { Width = 10, Height = 10, Fill = brush, RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = key, FontSize = 11, Foreground = Secondary });
            legend.Children.Add(item);
            idx++;
        }
        container.Children.Add(legend);

        var canvas = new Canvas { Width = width, Height = height };

        // Y gridlines + labels
        foreach (var pct in new[] { 0, 25, 50, 75, 100 })
        {
            var y = Y(pct);
            canvas.Children.Add(Line(padL, y, padL + plotW, y, GridLine, 1));
            var label = new TextBlock { Text = $"{pct}%", FontSize = 10, Foreground = Secondary };
            Canvas.SetLeft(label, 6);
            Canvas.SetTop(label, y - 7);
            canvas.Children.Add(label);
        }

        // X ticks
        var (tickCount, showTime) = XAxisStyle(hi - lo);
        for (var i = 0; i <= tickCount; i++)
        {
            var t = lo.AddSeconds(span * i / tickCount);
            var x = X(t);
            canvas.Children.Add(Line(x, padT, x, padT + plotH, GridLine, 0.5));
            var text = showTime ? $"{t:MMM d}\n{t:HH:mm}" : $"{t:MMM d}";
            var label = new TextBlock { Text = text, FontSize = 10, Foreground = Secondary, TextAlignment = TextAlignment.Center };
            Canvas.SetLeft(label, x - 18);
            Canvas.SetTop(label, padT + plotH + 3);
            canvas.Children.Add(label);
        }

        // Series polylines + latest dot
        idx = 0;
        foreach (var (key, pts) in series)
        {
            var brush = SeriesPalette[idx % SeriesPalette.Length];
            // Draw one polyline per contiguous run, breaking wherever the app wasn't running long
            // enough to record. A lone sample stranded between two gaps gets a small dot so it is
            // still visible (a one-point polyline would draw nothing).
            var segStart = 0;
            for (var i = 1; i <= pts.Count; i++)
            {
                var boundary = i == pts.Count || (pts[i].date - pts[i - 1].date) > GapThreshold;
                if (!boundary) continue;
                var segLen = i - segStart;
                if (segLen >= 2)
                {
                    var poly = new Polyline { Stroke = brush, StrokeThickness = 1.75, StrokeLineJoin = PenLineJoin.Round };
                    for (var j = segStart; j < i; j++) poly.Points.Add(new Point(X(pts[j].date), Y(pts[j].pct)));
                    canvas.Children.Add(poly);
                }
                else if (segLen == 1)
                {
                    var (d, p) = pts[segStart];
                    var mark = new Ellipse { Width = 3, Height = 3, Fill = brush };
                    Canvas.SetLeft(mark, X(d) - 1.5);
                    Canvas.SetTop(mark, Y(p) - 1.5);
                    canvas.Children.Add(mark);
                }
                segStart = i;
            }
            var last = pts[^1];
            var dot = new Ellipse { Width = 7, Height = 7, Fill = brush };
            Canvas.SetLeft(dot, X(last.date) - 3.5);
            Canvas.SetTop(dot, Y(last.pct) - 3.5);
            canvas.Children.Add(dot);
            idx++;
        }

        container.Children.Add(canvas);
        return container;
    }

    private static (int count, bool showTime) XAxisStyle(TimeSpan span)
    {
        var s = span.TotalSeconds;
        if (s < 36 * 3600) return (4, true);
        if (s < 3 * 86400) return (4, true);
        if (s < 9 * 86400) return (6, false);
        return (5, false);
    }

    // MARK: - Activity tab

    private void BuildActivityTab()
    {
        var activity = _store.Activity;

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        head.Children.Add(SectionHeader("When you use Claude Code"));
        head.Children.Add(new TextBlock
        {
            Text = ActivitySummary(activity), FontSize = 12, Foreground = Secondary,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
        });
        ContentHost.Children.Add(head);

        if (activity.TotalPrompts == 0)
        {
            ContentHost.Children.Add(EmptyState("No Claude Code activity found under ~/.claude yet."));
            return;
        }

        ContentHost.Children.Add(ChartCard("Prompts by hour",
            "Blue marks your active hours; grey marks rarely used hours.", BuildHourlyChart(activity)));
        ContentHost.Children.Add(ChartCard("Weekly rhythm",
            "Darker cells mean more prompts in that weekday and hour.", BuildHeatmap(activity)));
    }

    private string ActivitySummary(ActivityProfile activity)
    {
        if (activity.TotalPrompts == 0) return "No activity recorded yet.";
        var parts = new List<string> { $"{activity.TotalPrompts} prompts across {activity.DistinctDays} active day(s)." };
        if (activity.HasEnoughData)
        {
            if (activity.DeadRangeDescription is { } dead)
                parts.Add($"You are rarely active {dead}; quota forecasts discount resets in this window.");
            else
                parts.Add("Your activity is spread fairly evenly through the day.");
        }
        else
        {
            parts.Add("About 5 days and 40 prompts are needed to detect off-hours reliably.");
        }
        return string.Join(" ", parts);
    }

    private UIElement BuildHourlyChart(ActivityProfile activity)
    {
        var container = new StackPanel();
        var bars = new UniformGrid { Rows = 1, Height = 150 };
        for (var hour = 0; hour < 24; hour++)
        {
            var count = activity.HourCounts[hour];
            var h = count > 0 ? Math.Max(4, Math.Min(1.0, activity.Intensity(hour)) * 150) : 0;
            var cell = new Grid();
            var rect = new Rectangle
            {
                Height = h,
                Fill = activity.IsActive(hour) ? UiTheme.Blue : Freeze(0x9A, 0x9A, 0xA0, 0x52),
                RadiusX = 3, RadiusY = 3,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = $"{hour}:00 — {count} prompt(s)"
            };
            cell.Children.Add(rect);
            bars.Children.Add(cell);
        }
        container.Children.Add(bars);

        var labels = new UniformGrid { Rows = 1, Margin = new Thickness(0, 4, 0, 0) };
        for (var hour = 0; hour < 24; hour++)
            labels.Children.Add(new TextBlock
            {
                Text = hour % 3 == 0 ? hour.ToString() : "",
                FontSize = 9, Foreground = Secondary, TextAlignment = TextAlignment.Center
            });
        container.Children.Add(labels);
        return container;
    }

    private UIElement BuildHeatmap(ActivityProfile activity)
    {
        var maxCell = Math.Max(1, activity.WeekdayHourCounts.SelectMany(r => r).DefaultIfEmpty(0).Max());
        var container = new StackPanel();

        for (var row = 0; row < WeekdayOrder.Length; row++)
        {
            var dayIndex = WeekdayOrder[row];
            var line = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock { Text = WeekdayNames[row], FontSize = 10, FontWeight = FontWeights.Medium, Foreground = Secondary, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 0);
            line.Children.Add(name);

            var cells = new UniformGrid { Rows = 1, Height = 22 };
            for (var hour = 0; hour < 24; hour++)
            {
                var count = activity.WeekdayHourCounts[dayIndex][hour];
                cells.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(3),
                    Background = CellColor(count, maxCell),
                    Margin = new Thickness(2, 0, 2, 0),
                    ToolTip = $"{WeekdayNames[row]} {hour}:00 — {count} prompt(s)"
                });
            }
            Grid.SetColumn(cells, 1);
            line.Children.Add(cells);
            container.Children.Add(line);
        }

        // Hour axis
        var axis = new Grid();
        axis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        axis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labels = new UniformGrid { Rows = 1 };
        for (var hour = 0; hour < 24; hour++)
            labels.Children.Add(new TextBlock { Text = hour % 3 == 0 ? hour.ToString() : "", FontSize = 9, Foreground = Secondary, TextAlignment = TextAlignment.Center });
        Grid.SetColumn(labels, 1);
        axis.Children.Add(labels);
        container.Children.Add(axis);
        return container;
    }

    private static Brush CellColor(int count, int max)
    {
        if (count <= 0) return Freeze(0x9A, 0x9A, 0xA0, 0x14);
        var intensity = (double)count / max;
        var alpha = (byte)((0.18 + 0.82 * intensity) * 255);
        return Freeze(0x0A, 0x84, 0xFF, alpha);
    }

    // MARK: - Shared helpers

    private UIElement ChartCard(string title, string caption, UIElement content)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(12), Background = Freeze(0xFF, 0xFF, 0xFF, 0x10),
            Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 24)
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Primary, Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(content);
        panel.Children.Add(new TextBlock { Text = caption, FontSize = 11, Foreground = Secondary, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap });
        border.Child = panel;
        return border;
    }

    private UIElement SectionHeader(string title) =>
        new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Primary };

    private UIElement EmptyState(string message) =>
        new Border
        {
            CornerRadius = new CornerRadius(10), Background = Freeze(0xFF, 0xFF, 0xFF, 0x10),
            Padding = new Thickness(20), Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock { Text = message, FontSize = 12, Foreground = Secondary, TextWrapping = TextWrapping.Wrap }
        };

    private UIElement RangeButton(string label, Range range)
    {
        var btn = new ToggleButton
        {
            Content = label, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 0, 0, 0),
            IsChecked = _range == range
        };
        btn.Click += (_, _) => { _range = range; Rebuild(); };
        return btn;
    }

    private static TimeSpan RangeInterval(Range r) => r switch
    {
        Range.SevenDays => TimeSpan.FromDays(7),
        Range.FourteenDays => TimeSpan.FromDays(14),
        Range.ThirtyDays => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(14)
    };

    private static Line Line(double x1, double y1, double x2, double y2, Brush stroke, double thickness) =>
        new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke, StrokeThickness = thickness };

    private static string Relative(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span.TotalSeconds < 60) return $"{Math.Max(1, (int)span.TotalSeconds)}s";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
        return $"{(int)span.TotalDays}d";
    }

    private static SolidColorBrush Freeze(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
