using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeBar.Services;
using ClaudeBar.ViewModels;

namespace ClaudeBar.Views;

/// <summary>
/// The Settings window — the home for preferences that are set once and rarely touched, moved out of
/// the tray popover so that surface stays a glanceable status readout. Every control writes straight
/// to <see cref="Settings"/> (or the store) and takes effect immediately, so there is no Apply/Cancel.
/// </summary>
public partial class PreferencesWindow : Window
{
    private static readonly Brush Primary = Freeze(0xF2, 0xF2, 0xF7);
    private static readonly Brush Secondary = Freeze(0x9A, 0x9A, 0xA0);

    private readonly UsageStore _store;
    private readonly AutoUpdater _updater;

    public PreferencesWindow(UsageStore store, AutoUpdater updater)
    {
        InitializeComponent();
        _store = store;
        _updater = updater;
        Build();
    }

    private void Build()
    {
        ContentHost.Children.Clear();

        ContentHost.Children.Add(SectionHeader("General"));
        ContentHost.Children.Add(Toggle("Launch at Login", _store.LaunchAtLogin,
            v => _store.SetLaunchAtLogin(v)));
        ContentHost.Children.Add(Toggle("Auto Update", Settings.GetBool(AutoUpdater.AutoUpdateKey, true),
            v =>
            {
                Settings.SetBool(AutoUpdater.AutoUpdateKey, v);
                if (v) _updater.StartPeriodicCheck(); else _updater.StopPeriodicCheck();
            }));

        ContentHost.Children.Add(SectionHeader("Notifications"));
        ContentHost.Children.Add(Toggle("Notify on limit alerts", Settings.GetBool(NotificationManager.EnabledKey, true),
            v => Settings.SetBool(NotificationManager.EnabledKey, v)));
        ContentHost.Children.Add(Dropdown("Notify from",
            new[] { ("Medium", (int)Urgency.Medium), ("High", (int)Urgency.High), ("Critical", (int)Urgency.Critical) },
            (int)NotificationManager.Threshold,
            v => Settings.SetInt(NotificationManager.ThresholdKey, v),
            "The lowest urgency that shows a toast."));

        ContentHost.Children.Add(SectionHeader("Usage polling"));
        ContentHost.Children.Add(Dropdown("Check every",
            new[] { ("1 min", 60), ("2 min", 120), ("5 min", 300), ("10 min", 600) },
            Settings.GetInt(UsageStore.PollIntervalKey, 300),
            v => { Settings.SetInt(UsageStore.PollIntervalKey, v); _store.ApplyPollInterval(); },
            "How often ClaudeBar re-fetches usage."));

        ContentHost.Children.Add(SectionHeader("Appearance"));
        ContentHost.Children.Add(Toggle("Fill bars as limit is used", Settings.GetBool("fillBarsAsUsed", false),
            v => Settings.SetBool("fillBarsAsUsed", v),
            "Off: bars drain as quota is spent. On: bars fill up. Applies next time the popover opens."));
    }

    // MARK: - Row helpers

    private UIElement SectionHeader(string title) =>
        new TextBlock
        {
            Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Secondary,
            Margin = new Thickness(0, 14, 0, 6)
        };

    private UIElement Toggle(string label, bool value, Action<bool> onChange, string? caption = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        var cb = new CheckBox
        {
            Content = new TextBlock { Text = label, FontSize = 12, Foreground = Primary },
            IsChecked = value,
            Foreground = Primary
        };
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        panel.Children.Add(cb);
        if (caption is not null)
            panel.Children.Add(new TextBlock
            {
                Text = caption, FontSize = 10, Foreground = Secondary,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 1, 0, 0)
            });
        return panel;
    }

    private UIElement Dropdown(string label, (string text, int value)[] options, int current,
        Action<int> onChange, string? caption = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lbl = new TextBlock { Text = label, FontSize = 12, Foreground = Primary, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var combo = new ComboBox { MinWidth = 110, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        // Snap to the nearest option instead of exact-matching: a stored value outside the offered
        // buckets (a hand-edited or legacy settings.json) would otherwise select nothing and render the
        // combo blank, even though runtime behavior stays safe via its own clamp.
        var selected = NearestIndex(current, options);
        for (var i = 0; i < options.Length; i++)
            combo.Items.Add(new ComboBoxItem { Content = options[i].text, Tag = options[i].value, IsSelected = i == selected });
        // Attach after the initial selection so the first render doesn't fire a spurious write.
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: int v }) onChange(v);
        };
        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);
        panel.Children.Add(grid);

        if (caption is not null)
            panel.Children.Add(new TextBlock
            {
                Text = caption, FontSize = 10, Foreground = Secondary,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0)
            });
        return panel;
    }

    /// <summary>
    /// The index of the option whose value is closest to <paramref name="current"/> (the first on a tie).
    /// Guarantees a dropdown always has a selection even when the stored value matches no offered option,
    /// so the combo never renders blank. Assumes a non-empty <paramref name="options"/>.
    /// </summary>
    internal static int NearestIndex(int current, (string text, int value)[] options)
    {
        var best = 0;
        var bestDistance = Math.Abs((long)options[0].value - current);
        for (var i = 1; i < options.Length; i++)
        {
            var distance = Math.Abs((long)options[i].value - current);
            if (distance < bestDistance) { best = i; bestDistance = distance; }
        }
        return best;
    }

    private static SolidColorBrush Freeze(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
