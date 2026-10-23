using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch.Features.Analytics;

/// Live visitors, today's totals, visitors by hour, and top pages and sources, from <see cref="AnalyticsService"/>.
public sealed class AnalyticsModule : ContentControl
{
    private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);

    private readonly AnalyticsService _service = AnalyticsService.Shared;

    public AnalyticsModule()
    {
        Render();
        Loaded += (_, _) =>
        {
            _service.Changed += Render;
            _service.Start();
            Render();
        };
        Unloaded += (_, _) =>
        {
            _service.Changed -= Render;
            _service.Stop();
        };
    }

    private void Render()
    {
        if (!AnalyticsService.HasKey || AnalyticsService.Site.Length == 0)
        {
            Content = Missing(AnalyticsService.HasKey
                ? "Add your site domain (e.g. example.com) in Settings."
                : "Add your Plausible API key and site domain in Settings.");
            return;
        }
        var s = _service.KeyRejected ? null : _service.Snapshot;
        if (s == null)
        {
            Content = _service.Message is { } message && !_service.IsLoading ? Failed(message) : Loading();
            return;
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });

        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(74) });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        left.RowDefinitions.Add(new RowDefinition());
        left.Children.Add(StatsRow(s));
        var chart = ChartCard(s);
        Grid.SetRow(chart, 2);
        left.Children.Add(chart);
        grid.Children.Add(left);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition());
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        right.RowDefinitions.Add(new RowDefinition());
        right.Children.Add(ListCard("Top pages", "\uE8A5", s.TopPages));
        var sources = ListCard("Top sources", "\uE774", s.TopSources);
        Grid.SetRow(sources, 2);
        right.Children.Add(sources);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        Content = grid;
    }

    // MARK: States

    private static UIElement Missing(string hint)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Ui.EmptyState("\uE9D2", "Connect Plausible", hint));
        var open = Ui.Pill("Open Settings", "\uE713", () => SettingsView.Open(SettingsView.Section.Integrations), prominent: true);
        open.HorizontalAlignment = HorizontalAlignment.Center;
        open.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(open);
        return stack;
    }

    private UIElement Failed(string message)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Ui.EmptyState("\uE7BA", "Couldn't load Plausible", message));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Ui.Pill("Retry", "\uE72C", () => _service.Refresh(force: true), prominent: !_service.KeyRejected));
        var settings = Ui.Pill("Open Settings", "\uE713", () => SettingsView.Open(SettingsView.Section.Integrations), prominent: _service.KeyRejected);
        settings.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(settings);
        stack.Children.Add(buttons);
        return stack;
    }

    private static UIElement Loading()
    {
        var spinner = Spinner(true, 16);
        spinner.HorizontalAlignment = HorizontalAlignment.Center;
        spinner.VerticalAlignment = VerticalAlignment.Center;
        return spinner;
    }

    // MARK: Stats

    private static UIElement StatsRow(AnalyticsSnapshot s)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        for (var i = 0; i < 4; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
        }

        var live = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var caption = new StackPanel { Orientation = Orientation.Horizontal };
        caption.Children.Add(PulsingDot());
        var label = Ui.Text("LIVE", 10, ThemeResources.SecondaryText, FontWeights.Bold);
        label.Margin = new Thickness(6, 0, 0, 0);
        caption.Children.Add(label);
        live.Children.Add(caption);
        var count = Ui.Text(s.Live.ToString(CultureInfo.CurrentCulture), 34, ThemeResources.Accent, FontWeights.Bold).Tabular();
        count.TextWrapping = TextWrapping.NoWrap;
        if (AppSettings.Shared.Theme.Glow && Application.Current.Resources[ThemeResources.Accent] is SolidColorBrush accent)
            count.Effect = ExpandedView.Glow(accent.Color, 5);
        live.Children.Add(new Viewbox { Child = count, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left });
        var liveTile = Tile(live, new Thickness(12, 8, 12, 8));
        liveTile.ToolTip = "Visitors on the site in the last 5 minutes";
        row.Children.Add(liveTile);

        var metrics = new[]
        {
            (AnalyticsService.Compact(s.Visitors), "Visitors"),
            (AnalyticsService.Compact(s.Pageviews), "Pageviews"),
            ($"{(int)Math.Round(s.BounceRate)}%", "Bounce"),
            (Format.Duration(s.VisitDuration), "Visit"),
        };
        for (var i = 0; i < metrics.Length; i++)
        {
            var tile = Metric(metrics[i].Item1, metrics[i].Item2);
            Grid.SetColumn(tile, 2 + i * 2);
            row.Children.Add(tile);
        }
        return row;
    }

    private static Border Metric(string value, string label)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Ui.Text(label.ToUpperInvariant(), 9, ThemeResources.SecondaryText, FontWeights.SemiBold));
        var text = Ui.Text(value, 20, weight: FontWeights.SemiBold).Tabular();
        text.TextWrapping = TextWrapping.NoWrap;
        text.Margin = new Thickness(0, 3, 0, 0);
        stack.Children.Add(new Viewbox { Child = text, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left });
        return Tile(stack, new Thickness(10, 0, 10, 0));
    }

    private static Border Tile(UIElement child, Thickness padding)
    {
        var tile = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = padding,
            BorderThickness = new Thickness(AppSettings.Shared.Theme.Glow ? 1.2 : 0.8),
            Child = child,
        };
        tile.SetResourceReference(Border.BackgroundProperty, ThemeResources.Surface);
        tile.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);
        return tile;
    }

    /// Green dot with a ring that keeps spreading out from it.
    private static UIElement PulsingDot()
    {
        var grid = new Grid { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        var scale = new ScaleTransform(1, 1);
        var halo = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Green.WithOpacity(0.5).Brush(),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
        };
        grid.Children.Add(halo);
        grid.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Green.Brush() });

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var grow = new DoubleAnimation(1, 2.4, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        halo.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.8, 0, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        return grid;
    }

    // MARK: Chart

    private UIElement ChartCard(AnalyticsSnapshot s)
    {
        var accessory = new StackPanel { Orientation = Orientation.Horizontal };
        var stale = s.FetchedAt.Date != DateTime.Today;
        if (_service.Message is { } problem)
        {
            // Offline or a hiccup: the last figures stay, marked with a warning sign.
            var warning = Ui.Glyph("\uE7BA", 10, ThemeResources.SecondaryText);
            warning.Margin = new Thickness(0, 0, 5, 0);
            warning.ToolTip = problem + "\nShowing the last copy.";
            accessory.Children.Add(warning);
        }
        var time = Ui.Text(s.FetchedAt.ToString(stale ? "ddd t" : "t", CultureInfo.CurrentCulture), 11, ThemeResources.SecondaryText).Tabular();
        time.Margin = new Thickness(0, 0, 8, 0);
        accessory.Children.Add(time);
        accessory.Children.Add(Ui.Bare(Spinner(_service.IsLoading, 11), () => _service.Refresh(force: true), "Refresh"));

        var title = stale ? s.FetchedAt.ToString("dddd", CultureInfo.CurrentCulture) + " by hour" : "Today by hour";
        return new Card(title, "\uE9D2", new HourlyChart(s.Hourly, stale ? -1 : DateTime.Now.Hour), accessory);
    }

    /// Visitors per hour as bars, the current hour in full accent, with a light grid and axis labels.
    private sealed class HourlyChart : FrameworkElement
    {
        private const double AxisWidth = 26;
        private const double LabelHeight = 14;

        private readonly int[] _visitors = new int[24];
        private readonly int _currentHour;

        public HourlyChart(IEnumerable<AnalyticsSnapshot.Point> points, int currentHour)
        {
            foreach (var p in points)
                if (p.Hour is >= 0 and < 24) _visitors[p.Hour] = p.Visitors;
            _currentHour = currentHour;
            ClipToBounds = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth <= AxisWidth || ActualHeight <= LabelHeight) return;
            var accent = ((SolidColorBrush)FindResource(ThemeResources.Accent)).Color;
            var text = ((SolidColorBrush)FindResource(ThemeResources.Text)).Color;
            var secondary = (Brush)FindResource(ThemeResources.SecondaryText);
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface("Segoe UI");

            // Three or so round steps up to the busiest hour.
            var max = Math.Max(1, _visitors.Max());
            var raw = max / 3.0;
            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            var step = new[] { 1, 2, 5, 10 }.Select(f => f * magnitude).First(v => v >= raw);
            step = Math.Max(1, Math.Round(step));
            var top = Math.Ceiling(max / step) * step;

            var plotHeight = ActualHeight - LabelHeight;
            var plotWidth = ActualWidth - AxisWidth;
            var grid = new Pen(text.WithOpacity(0.08).Brush(), 1);
            for (var v = 0.0; v <= top + 0.001; v += step)
            {
                var y = Math.Round(plotHeight * (1 - v / top)) + 0.5;
                dc.DrawLine(grid, new Point(AxisWidth, y), new Point(ActualWidth, y));
                var label = Label(AnalyticsService.Compact(v), secondary, typeface, dpi);
                dc.DrawText(label, new Point(AxisWidth - 5 - label.Width, Math.Clamp(y - label.Height / 2, 0, plotHeight - label.Height)));
            }

            // The domain runs from -0.5 to 23.5, so every bar sits in the middle of its slot.
            var slot = plotWidth / 24;
            var full = accent.Brush();
            var faded = accent.WithOpacity(0.5).Brush();
            for (var hour = 0; hour < 24; hour++)
            {
                if (_visitors[hour] == 0) continue;
                var height = plotHeight * _visitors[hour] / top;
                var rect = new Rect(AxisWidth + slot * hour + slot * 0.15, plotHeight - height, slot * 0.7, height);
                dc.DrawRoundedRectangle(hour == _currentHour ? full : faded, null, rect, 2, 2);
            }

            foreach (var hour in new[] { 0, 6, 12, 18, 23 })
            {
                var label = Label(hour.ToString("00", CultureInfo.InvariantCulture), secondary, typeface, dpi);
                var x = AxisWidth + slot * (hour + 0.5) - label.Width / 2;
                dc.DrawText(label, new Point(Math.Clamp(x, AxisWidth, ActualWidth - label.Width), plotHeight + 2));
            }
        }

        private static FormattedText Label(string value, Brush brush, Typeface typeface, double dpi) =>
            new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 9, brush, dpi);
    }

    // MARK: Lists

    private static UIElement ListCard(string title, string glyph, IReadOnlyList<AnalyticsSnapshot.Row> rows)
    {
        if (rows.Count == 0)
        {
            var empty = Ui.Text("No data yet today", 11, ThemeResources.SecondaryText);
            empty.VerticalAlignment = VerticalAlignment.Top;
            return new Card(title, glyph, empty);
        }
        var peak = Math.Max(1, rows.Max(r => r.Visitors));
        var list = new StackPanel();
        foreach (var row in rows.Take(5))
        {
            var fraction = (double)row.Visitors / peak;
            var line = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction, GridUnitType.Star) });
            var bar = new Border { CornerRadius = new CornerRadius(4) };
            bar.SetResourceReference(Border.BackgroundProperty, ThemeResources.AccentFaint);
            line.Children.Add(bar);

            var content = new DockPanel { Margin = new Thickness(6, 2, 6, 2) };
            var count = Ui.Text(AnalyticsService.Compact(row.Visitors), 11, weight: FontWeights.SemiBold).Tabular();
            count.Margin = new Thickness(4, 0, 0, 0);
            DockPanel.SetDock(count, Dock.Right);
            content.Children.Add(count);
            var name = Ui.Text(row.Name, 11);
            name.TextWrapping = TextWrapping.NoWrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.ToolTip = row.Name;
            content.Children.Add(name);
            Grid.SetColumnSpan(content, 2);
            line.Children.Add(content);
            list.Children.Add(line);
        }
        return new Card(title, glyph, list);
    }

    /// The refresh arrow; it turns while a fetch is running.
    private static TextBlock Spinner(bool spinning, double size)
    {
        var glyph = Ui.Glyph("\uE72C", size, ThemeResources.SecondaryText);
        glyph.RenderTransformOrigin = new Point(0.5, 0.5);
        var rotate = new RotateTransform();
        glyph.RenderTransform = rotate;
        if (spinning)
            rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        return glyph;
    }
}
