using System.Windows;
using System.Windows.Controls;
using ClaudeBar.Models;
using ClaudeBar.Services;

namespace ClaudeBar.Views;

/// <summary>
/// The focus-stealing "a limit is almost gone" dialog — the Windows counterpart of the macOS
/// LimitAlertView. Content is rebuilt imperatively so the same open window can be refreshed in place
/// with the current set of qualifying entries (matching the macOS update-in-place behavior).
/// </summary>
public partial class LimitAlertWindow : Window
{
    private readonly Action _onDismiss;
    private readonly Action _onSnooze;

    public LimitAlertWindow(IReadOnlyList<LimitAlertEntry> entries, Action onDismiss, Action onSnooze)
    {
        InitializeComponent();
        _onDismiss = onDismiss;
        _onSnooze = onSnooze;
        SetEntries(entries);
    }

    /// <summary>Refresh the open dialog with the current qualifying set without recreating the window.</summary>
    public void SetEntries(IReadOnlyList<LimitAlertEntry> entries)
    {
        // Matches the codebase-wide "exhausted" cutoff so the dialog's red/orange split never
        // disagrees with what the menu bar shows for the same number.
        var isCritical = entries.Any(e => e.Window.RemainingPercent < LimitAlert.WeeklyThreshold);

        ContentHost.Children.Clear();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock
        {
            Text = "⚠", // warning triangle
            FontSize = 20,
            Foreground = isCritical ? UiTheme.Red : UiTheme.Orange,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(new TextBlock
        {
            Text = entries.Count == 1 ? $"{entries[0].Title} almost gone" : "Limits almost gone",
            FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = UiTheme.PrimaryText,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 290
        });
        ContentHost.Children.Add(header);

        foreach (var entry in entries)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(new TextBlock { Text = entry.Title, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = UiTheme.PrimaryText });
            row.Children.Add(new TextBlock { Text = DetailLine(entry.Window), FontSize = 11, Foreground = UiTheme.SecondaryText, TextWrapping = TextWrapping.Wrap });
            ContentHost.Children.Add(row);
        }

        var buttons = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var snooze = new Button { Content = "Remind in 15 min", Padding = new Thickness(10, 4, 10, 4) };
        snooze.Click += (_, _) => _onSnooze();
        Grid.SetColumn(snooze, 0);
        buttons.Children.Add(snooze);

        var dismiss = new Button { Content = "Dismiss", Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
        dismiss.Click += (_, _) => _onDismiss();
        Grid.SetColumn(dismiss, 2);
        buttons.Children.Add(dismiss);

        ContentHost.Children.Add(buttons);
    }

    private static string DetailLine(RateWindow window)
    {
        var parts = new List<string> { $"{(int)Math.Round(window.RemainingPercent)}% left" };
        if (window.TimeUntilReset is { } resetIn) parts.Add($"resets in {Format.Duration(resetIn)}");
        if (window.TimeToExhaustion is { } exhaustion) parts.Add($"at this rate: ~{Format.Duration(exhaustion)} left");
        return string.Join(" · ", parts);
    }
}
