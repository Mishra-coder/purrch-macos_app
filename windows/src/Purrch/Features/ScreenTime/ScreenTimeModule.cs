using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Features.Downloads;
using Purrch.Interface;

namespace Purrch.Features.ScreenTime;

/// Today's total and the week as bars on the left, today's apps on the right. Updates every 5 seconds while open.
public sealed class ScreenTimeModule : ContentControl
{
    private readonly ScreenTimeService _service = ScreenTimeService.Shared;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(5) };
    private ScrollViewer? _scroll;

    public ScreenTimeModule()
    {
        _refresh.Tick += (_, _) => Render();
        Render();
        Loaded += (_, _) =>
        {
            Render();
            _refresh.Start();
        };
        Unloaded += (_, _) => _refresh.Stop();
    }

    private void Render()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(OverviewCard());
        var apps = TopAppsCard();
        Grid.SetColumn(apps, 2);
        grid.Children.Add(apps);
        Content = grid;
    }

    private UIElement OverviewCard()
    {
        var week = _service.Week;
        var average = week.Sum(d => d.Seconds) / Math.Max(1, week.Count(d => d.Seconds > 0));

        var body = new DockPanel();
        var total = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var value = Ui.Text(DurationText.Short(_service.TodayTotal), 34, weight: FontWeights.Bold).Tabular();
        value.TextWrapping = TextWrapping.NoWrap;
        total.Children.Add(value);
        total.Children.Add(Ui.Text("today", 12, ThemeResources.SecondaryText));
        DockPanel.SetDock(total, Dock.Top);
        body.Children.Add(total);
        body.Children.Add(new WeekChart(week));

        return new Card("Screen Time", "\uE823", body, Ui.Text($"Avg {DurationText.Short(average)}", 11, ThemeResources.SecondaryText));
    }

    private UIElement TopAppsCard()
    {
        var apps = _service.TodayApps;
        var accessory = Ui.Text(apps.Count == 1 ? "1 app" : $"{apps.Count} apps", 11, ThemeResources.SecondaryText);
        if (apps.Count == 0)
            return new Card("Top apps today", "\uE71D",
                Ui.EmptyState("\uE823", "Nothing yet", "App usage appears here as you work"), accessory);

        var top = Math.Max(1, apps[0].Seconds);
        var list = new StackPanel { Margin = new Thickness(0, 0, 2, 0) };
        foreach (var (key, seconds) in apps.Take(20))
            list.Children.Add(AppRow(key, seconds, top));
        var scroll = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        // The list is rebuilt every few seconds; keep it where it was scrolled to.
        scroll.ScrollToVerticalOffset(_scroll?.VerticalOffset ?? 0);
        _scroll = scroll;
        return new Card("Top apps today", "\uE71D", scroll, accessory);
    }

    private UIElement AppRow(string key, double seconds, double top)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var icon = _service.ExePath(key) is { } path
            ? FileVisuals.Icon(path, 22)
            : Ui.Glyph("\uE7C3", 15, ThemeResources.SecondaryText);
        var iconBox = new Border { Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0), Child = icon };
        DockPanel.SetDock(iconBox, Dock.Left);
        row.Children.Add(iconBox);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var line = new DockPanel();
        var time = Ui.Text(DurationText.Short(seconds), 12, ThemeResources.SecondaryText, FontWeights.SemiBold).Tabular();
        time.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(time, Dock.Right);
        line.Children.Add(time);
        var name = Ui.Text(_service.Name(key), 12, weight: FontWeights.Medium);
        name.TextWrapping = TextWrapping.NoWrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        line.Children.Add(name);
        stack.Children.Add(line);

        var bar = new Grid { Height = 4, Margin = new Thickness(0, 3, 0, 0) };
        var track = new Border { CornerRadius = new CornerRadius(2) };
        track.SetResourceReference(Border.BackgroundProperty, ThemeResources.Fill);
        bar.Children.Add(track);
        var fill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
        fill.SetResourceReference(Border.BackgroundProperty, ThemeResources.Accent);
        bar.Children.Add(fill);
        bar.SizeChanged += (_, e) => fill.Width = Math.Max(4, e.NewSize.Width * seconds / top);
        stack.Children.Add(bar);
        row.Children.Add(stack);
        return row;
    }
}

public static class DurationText
{
    /// "3h 12m", "12m", "<1m".
    public static string Short(double seconds)
    {
        var m = (int)seconds / 60;
        if (m >= 60) return $"{m / 60}h {m % 60}m";
        if (m >= 1) return $"{m}m";
        return "<1m";
    }
}

/// Seven bars, one a day, today's in the full accent. Hour lines and labels on the right, weekday letters below.
internal sealed class WeekChart : FrameworkElement
{
    private const double LabelWidth = 26;
    private const double LabelHeight = 16;

    private readonly IReadOnlyList<(DateTime Date, double Seconds)> _week;

    public WeekChart(IReadOnlyList<(DateTime Date, double Seconds)> week)
    {
        _week = week;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_week.Count == 0 || ActualWidth <= LabelWidth || ActualHeight <= LabelHeight) return;
        var theme = AppSettings.Shared.Theme;
        var accent = TryFindResource(ThemeResources.Accent) is SolidColorBrush brush ? brush.Color : theme.Accent;
        var secondary = theme.SecondaryText.Brush();
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var culture = CultureInfo.CurrentCulture;
        var typeface = new Typeface("Segoe UI");

        var plotWidth = ActualWidth - LabelWidth;
        var plotHeight = ActualHeight - LabelHeight;
        var (step, top) = Scale(_week.Max(d => d.Seconds) / 3600);

        var grid = new Pen(theme.Border.WithOpacity(0.5).Brush(), 0.6) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
        for (var h = 0.0; h <= top + 0.001; h += step)
        {
            var y = plotHeight * (1 - h / top);
            dc.DrawLine(grid, new Point(0, y), new Point(plotWidth, y));
            var label = new FormattedText($"{(int)h}h", culture, FlowDirection.LeftToRight, typeface, 10, secondary, pixelsPerDip);
            dc.DrawText(label, new Point(plotWidth + 6, Math.Clamp(y - label.Height / 2, 0, plotHeight - label.Height / 2)));
        }

        var slot = plotWidth / _week.Count;
        var barWidth = Math.Min(28, slot * 0.6);
        for (var i = 0; i < _week.Count; i++)
        {
            var (date, seconds) = _week[i];
            var centre = slot * (i + 0.5);
            var height = plotHeight * Math.Clamp(seconds / 3600 / top, 0, 1);
            if (height > 0)
            {
                var colour = date == DateTime.Today ? accent : accent.WithOpacity(0.4);
                var rect = new Rect(centre - barWidth / 2, plotHeight - height, barWidth, height);
                var radius = Math.Min(4, Math.Min(barWidth, height) / 2);
                dc.DrawRoundedRectangle(colour.Brush(), null, rect, radius, radius);
            }
            var day = culture.DateTimeFormat.GetShortestDayName(date.DayOfWeek);
            var text = new FormattedText(day.Length > 0 ? day[..1] : "", culture, FlowDirection.LeftToRight, typeface, 10, secondary, pixelsPerDip);
            dc.DrawText(text, new Point(centre - text.Width / 2, plotHeight + 3));
        }
    }

    /// Whole-hour steps with at most three lines above zero, like the Mac chart's automatic axis.
    private static (double Step, double Top) Scale(double peakHours)
    {
        var peak = Math.Max(1, Math.Ceiling(peakHours));
        var step = new[] { 1.0, 2, 3, 4, 6, 8, 12 }.FirstOrDefault(s => peak / s <= 3, 12);
        return (step, Math.Ceiling(peak / step) * step);
    }
}
