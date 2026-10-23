using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch.Features.Weather;

/// Current conditions, the next 12 hours and the week, from <see cref="WeatherService"/>.
public sealed class WeatherModule : ContentControl
{
    private static readonly Geometry Thermometer =
        Geometry.Parse("M 3,0 A 2,2 0 0 1 5,2 L 5,8.6 A 3,3 0 1 1 1,8.6 L 1,2 A 2,2 0 0 1 3,0 Z");
    private static readonly Geometry WindLines =
        Geometry.Parse("M 0,3 L 8,3 A 2,2 0 1 0 6,1 M 0,6.5 L 11,6.5 A 2,2 0 1 1 9,8.5 M 0,10 L 6,10");

    private readonly WeatherService _service = WeatherService.Shared;

    public WeatherModule()
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
        if (_service.Snapshot is not { } s)
        {
            Content = new Card("Weather", "\uE753", EmptyBody());
            return;
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(NowCard(s));

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition());
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.3, GridUnitType.Star) });
        right.Children.Add(HourlyCard(s));
        var daily = DailyCard(s);
        Grid.SetRow(daily, 2);
        right.Children.Add(daily);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        Content = grid;
    }

    private UIElement EmptyBody()
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (_service.IsLoading)
        {
            var spinner = Spinner(true, 16);
            spinner.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(spinner);
            var label = Ui.Text("Loading forecast…", 12, ThemeResources.SecondaryText);
            label.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(label);
            return stack;
        }
        stack.Children.Add(Ui.EmptyState("\uE81D", "No weather yet",
            _service.Message ?? "Set a city in Settings → Integrations, or allow location access."));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Ui.Pill("Try again", "\uE72C", () => _service.Refresh(force: true), prominent: true));
        if (_service.LocationDenied)
        {
            var settings = Ui.Pill("Location settings", "\uE81D", OpenLocationSettings);
            settings.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(settings);
        }
        stack.Children.Add(buttons);
        return stack;
    }

    private static void OpenLocationSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:privacy-location") { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"Couldn't open location settings: {e.Message}");
        }
    }

    // MARK: Now

    private UIElement NowCard(WeatherSnapshot s)
    {
        var refresh = Ui.Bare(Spinner(_service.IsLoading, 11), () => _service.Refresh(force: true));
        var updated = Format.Relative(s.FetchedAt);
        refresh.ToolTip = (updated == "now" ? "Updated just now" : $"Updated {updated} ago")
                          + (_service.Message is { } m ? $"\n{m}" : "");

        var body = new DockPanel();
        var details = new UniformGrid { Rows = 1 };
        details.Children.Add(Detail(Icon(Thermometer, 10), "Feels", WeatherService.Temp(s.Apparent)));
        details.Children.Add(Detail(Icon(WindLines, 9), "Wind", WeatherService.Wind(s.Wind)));
        details.Children.Add(Detail(DropShape.Make(false, 10), "Humid", $"{(int)s.Humidity}%"));
        DockPanel.SetDock(details, Dock.Bottom);
        body.Children.Add(details);

        var top = new StackPanel();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Emoji(WeatherCode.Emoji(s.Code, s.IsDay), 34);
        icon.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(icon);
        row.Children.Add(Ui.Text(WeatherService.Temp(s.Temperature), 44, weight: FontWeights.Bold).Tabular());
        top.Children.Add(row);
        var condition = Ui.Text(WeatherCode.Text(s.Code), 14, weight: FontWeights.SemiBold);
        condition.Margin = new Thickness(0, 6, 0, 0);
        top.Children.Add(condition);
        if (s.Daily.FirstOrDefault() is { } today)
        {
            var range = Ui.Text($"H {WeatherService.Temp(today.Max)}  L {WeatherService.Temp(today.Min)}", 12, ThemeResources.SecondaryText).Tabular();
            range.Margin = new Thickness(0, 6, 0, 0);
            top.Children.Add(range);
        }
        body.Children.Add(top);

        return new Card(s.Place.Length == 0 ? "Now" : s.Place, "\uE81D", body, refresh);
    }

    private static UIElement Detail(FrameworkElement icon, string label, string value)
    {
        var stack = new StackPanel();
        var caption = new StackPanel { Orientation = Orientation.Horizontal };
        icon.Margin = new Thickness(0, 0, 4, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        caption.Children.Add(icon);
        caption.Children.Add(Ui.Text(label.ToUpperInvariant(), 9, ThemeResources.SecondaryText, FontWeights.SemiBold));
        stack.Children.Add(caption);
        var text = Ui.Text(value, 12, weight: FontWeights.SemiBold).Tabular();
        text.TextWrapping = TextWrapping.NoWrap;
        text.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(new Viewbox { Child = text, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left });
        return stack;
    }

    /// Small outline icon in the secondary colour, for the details row.
    private static Path Icon(Geometry geometry, double height)
    {
        var path = new Path
        {
            Data = geometry,
            Stretch = Stretch.Uniform,
            Height = height,
            StrokeThickness = 1.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        path.SetResourceReference(Shape.StrokeProperty, ThemeResources.SecondaryText);
        return path;
    }

    // MARK: Hourly and daily

    private static UIElement HourlyCard(WeatherSnapshot s)
    {
        var columns = new UniformGrid { Rows = 1, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < s.Hourly.Count; i++)
        {
            var h = s.Hourly[i];
            var stack = Column();
            stack.Children.Add(Label(i == 0 ? "Now" : HourLabel(h.Time), i == 0));
            var icon = Emoji(WeatherCode.Emoji(h.Code, h.IsDay), 15);
            icon.Height = 20;
            icon.Margin = new Thickness(0, 4, 0, 4);
            stack.Children.Add(icon);
            stack.Children.Add(Centered(Ui.Text(WeatherService.Temp(h.Temperature), 13, weight: FontWeights.SemiBold).Tabular()));
            columns.Children.Add(stack);
        }
        return new Card("Next 12 hours", "\uE823", columns);
    }

    private static UIElement DailyCard(WeatherSnapshot s)
    {
        var lo = s.Daily.Count == 0 ? 0 : s.Daily.Min(d => d.Min);
        var hi = s.Daily.Count == 0 ? 1 : s.Daily.Max(d => d.Max);
        var columns = new UniformGrid { Rows = 1, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < s.Daily.Count; i++)
        {
            var d = s.Daily[i];
            var stack = Column();
            stack.Children.Add(Label(i == 0 ? "Today" : DayLabel(d.Date), i == 0));
            var icon = Emoji(WeatherCode.Emoji(d.Code), 14);
            icon.Height = 18;
            icon.Margin = new Thickness(0, 3, 0, 3);
            stack.Children.Add(icon);
            stack.Children.Add(Centered(Ui.Text(WeatherService.Temp(d.Max), 12, weight: FontWeights.SemiBold).Tabular()));
            var bar = RangeBar(d, lo, hi);
            bar.Margin = new Thickness(0, 3, 0, 3);
            stack.Children.Add(bar);
            stack.Children.Add(Centered(Ui.Text(WeatherService.Temp(d.Min), 11, ThemeResources.SecondaryText).Tabular()));
            columns.Children.Add(stack);
        }
        return new Card("This week", "\uE787", columns);
    }

    /// The day's low-to-high span, placed on the week's overall range.
    private static FrameworkElement RangeBar(WeatherSnapshot.Day d, double lo, double hi)
    {
        const double height = 20;
        var span = Math.Max(hi - lo, 1);
        var top = (hi - d.Max) / span * height;
        var length = Math.Max(3, (d.Max - d.Min) / span * height);
        var grid = new Grid { Width = 4, Height = height, HorizontalAlignment = HorizontalAlignment.Center };
        var track = new Border { CornerRadius = new CornerRadius(2) };
        track.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
        grid.Children.Add(track);
        var fill = new Border
        {
            CornerRadius = new CornerRadius(2),
            Height = Math.Min(length, height),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Math.Clamp(top, 0, height - Math.Min(length, height)), 0, 0),
        };
        fill.SetResourceReference(Border.BackgroundProperty, ThemeResources.Accent);
        grid.Children.Add(fill);
        return grid;
    }

    private static StackPanel Column() => new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock Label(string text, bool current)
    {
        var label = Ui.Text(text, 10, current ? ThemeResources.Accent : ThemeResources.SecondaryText, FontWeights.SemiBold);
        label.TextWrapping = TextWrapping.NoWrap;
        return Centered(label);
    }

    private static TextBlock Centered(TextBlock text)
    {
        text.HorizontalAlignment = HorizontalAlignment.Center;
        return text;
    }

    private static TextBlock Emoji(string text, double size) => new global::Emoji.Wpf.TextBlock
    {
        Text = text,
        FontSize = size,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

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

    /// "2026-10-23T15:00" -> "3PM", or "15" where the clock is 24-hour, like the Mac app's "j" template.
    private static string HourLabel(string time)
    {
        var t = time.IndexOf('T');
        if (t < 0 || !int.TryParse(time.AsSpan(t + 1, Math.Min(2, time.Length - t - 1)), out var hour)) return time;
        var culture = CultureInfo.CurrentCulture;
        var date = DateTime.Today.AddHours(hour);
        return culture.DateTimeFormat.ShortTimePattern.Contains('H')
            ? date.ToString("HH", culture)
            : date.ToString("h tt", culture).Replace(" ", "");
    }

    /// "2026-10-24" -> "Sat".
    private static string DayLabel(string date) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("ddd", CultureInfo.CurrentCulture)
            : date;
}
