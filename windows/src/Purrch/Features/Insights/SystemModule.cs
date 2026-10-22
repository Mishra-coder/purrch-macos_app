using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch.Features.Insights;

/// CPU, memory and disk, network, battery and machine details, refreshed every second.
public sealed class SystemModule : Grid
{
    private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);

    private readonly SystemMonitor _monitor = SystemMonitor.Shared;

    private readonly RingGauge _cpu = new("Load");
    private readonly Sparkline _cpuLine = new() { Margin = new Thickness(0, 6, 0, 6) };
    private readonly RingGauge _ram = new("RAM");
    private readonly RingGauge _disk = new("Disk");
    private readonly TextBlock _ramText = Small();
    private readonly TextBlock _diskText = Small();
    private readonly TextBlock _down = Ui.Text("", 14, weight: FontWeights.SemiBold).Tabular();
    private readonly TextBlock _up = Ui.Text("", 14, weight: FontWeights.SemiBold).Tabular();
    private readonly Sparkline _downLine = new();
    private readonly Sparkline _upLine = new() { Filled = false, Colour = ThemeResources.SecondaryText };
    private readonly ContentControl _batteryBody = new();
    private readonly TextBlock _batteryState = Ui.Text("", 11, ThemeResources.SecondaryText);
    private readonly TextBlock _uptime = Ui.Text("", 15, weight: FontWeights.SemiBold).Tabular();

    public SystemModule()
    {
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        RowDefinitions.Add(new RowDefinition());

        var top = new Grid();
        for (var i = 0; i < 5; i++)
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 1 ? new GridLength(10) : new GridLength(1, GridUnitType.Star) });
        Place(top, CpuCard(), 0);
        Place(top, MemoryDiskCard(), 2);
        Place(top, NetworkCard(), 4);
        Children.Add(top);

        var bottom = new Grid();
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        Place(bottom, new Card("Battery", "\uE83F", _batteryBody, _batteryState), 0);
        Place(bottom, MachineCard(), 2);
        SetRow(bottom, 2);
        Children.Add(bottom);

        Loaded += (_, _) =>
        {
            _monitor.Updated += Refresh;
            _monitor.Start();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _monitor.Updated -= Refresh;
            _monitor.Stop();
        };
    }

    private static void Place(Grid grid, UIElement element, int column)
    {
        SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static TextBlock Small()
    {
        var text = Ui.Text("", 10, ThemeResources.SecondaryText).Tabular();
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.Margin = new Thickness(0, 4, 0, 0);
        return text;
    }

    private UIElement CpuCard()
    {
        var body = new DockPanel();
        _cpu.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(_cpu, Dock.Left);
        body.Children.Add(_cpu);
        body.Children.Add(_cpuLine);
        return new Card("CPU", "\uE9D9", body, Ui.Text($"{Environment.ProcessorCount} cores", 11, ThemeResources.SecondaryText));
    }

    private UIElement MemoryDiskCard()
    {
        var ram = new StackPanel();
        ram.Children.Add(_ram);
        ram.Children.Add(_ramText);
        var disk = new StackPanel { Margin = new Thickness(18, 0, 0, 0) };
        disk.Children.Add(_disk);
        disk.Children.Add(_diskText);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(ram);
        row.Children.Add(disk);
        return new Card("Memory & Disk", "\uE964", row);
    }

    private UIElement NetworkCard()
    {
        var rates = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        rates.Children.Add(Rate("\uE74B", ThemeResources.Accent, _down));
        var up = Rate("\uE74A", ThemeResources.SecondaryText, _up);
        ((FrameworkElement)up).Margin = new Thickness(14, 0, 0, 0);
        rates.Children.Add(up);

        var lines = new Grid();
        lines.Children.Add(_downLine);
        lines.Children.Add(_upLine);
        var body = new DockPanel();
        DockPanel.SetDock(rates, Dock.Top);
        body.Children.Add(rates);
        body.Children.Add(lines);
        return new Card("Network", "\uE968", body);
    }

    private static UIElement Rate(string glyph, string colour, TextBlock value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var arrow = Ui.Glyph(glyph, 11, colour);
        arrow.Margin = new Thickness(0, 0, 4, 0);
        row.Children.Add(arrow);
        value.TextWrapping = TextWrapping.NoWrap;
        row.Children.Add(value);
        return row;
    }

    private UIElement MachineCard()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Pair("Uptime", _uptime));
        stack.Children.Add(Pair("System", Value(SystemFormat.WindowsVersion())));
        stack.Children.Add(Pair("Name", Value(Environment.MachineName)));
        return new Card("Machine", "\uE7F4", stack);
    }

    private static TextBlock Value(string text)
    {
        var value = Ui.Text(text, 13, weight: FontWeights.SemiBold);
        value.TextWrapping = TextWrapping.NoWrap;
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        return value;
    }

    private static UIElement Pair(string label, TextBlock value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var name = Ui.Text(label, 12, ThemeResources.SecondaryText);
        name.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(name);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        row.Children.Add(value);
        return row;
    }

    private void Refresh()
    {
        var m = _monitor;
        _cpu.Set(m.Cpu);
        _cpuLine.Set(m.CpuHistory, 1);
        _ram.Set(m.MemoryFraction);
        _ramText.Text = $"{SystemFormat.Gb(m.MemoryUsed)} / {SystemFormat.Gb(m.MemoryTotal)}";
        _disk.Set(m.DiskFraction);
        _diskText.Text = $"{Format.Bytes(m.DiskFree)} free on {m.DiskName}";
        _down.Text = SystemFormat.Rate(m.NetDown);
        _up.Text = SystemFormat.Rate(m.NetUp);
        var peak = Math.Max(1024, Math.Max(m.NetDownHistory.DefaultIfEmpty(0).Max(), m.NetUpHistory.DefaultIfEmpty(0).Max()));
        _downLine.Set(m.NetDownHistory, peak);
        _upLine.Set(m.NetUpHistory, peak);
        _uptime.Text = SystemFormat.Uptime(m.Uptime);
        RefreshBattery(m.Battery);
    }

    private void RefreshBattery(BatteryInfo b)
    {
        if (!b.Present)
        {
            _batteryState.Text = "";
            if (_batteryBody.Content is not FrameworkElement { Tag: "none" })
            {
                var empty = (FrameworkElement)Ui.EmptyState("\uE83E", "No battery", "This PC runs on mains power");
                empty.Tag = "none";
                _batteryBody.Content = empty;
            }
            return;
        }
        _batteryState.Text = b.IsCharging ? "Charging" : b.OnAC ? "Plugged in" : "On battery";

        var row = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        var percent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        percent.Children.Add(Ui.Text($"{Math.Round(b.Level * 100)}", 42, weight: FontWeights.Bold).Tabular());
        var sign = Ui.Text("%", 18, ThemeResources.SecondaryText, FontWeights.SemiBold);
        sign.Margin = new Thickness(2, 10, 0, 0);
        percent.Children.Add(sign);
        DockPanel.SetDock(percent, Dock.Left);
        row.Children.Add(percent);

        var bar = new Grid { Width = 120, Height = 14, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var track = new Border { CornerRadius = new CornerRadius(7) };
        track.SetResourceReference(Border.BackgroundProperty, ThemeResources.Fill);
        var fill = new Border { CornerRadius = new CornerRadius(7), Width = 120 * Math.Clamp(b.Level, 0, 1), HorizontalAlignment = HorizontalAlignment.Left };
        if (b.IsCharging) fill.Background = Green.Brush();
        else if (b.Level < 0.2) fill.Background = Red.Brush();
        else fill.SetResourceReference(Border.BackgroundProperty, ThemeResources.Accent);
        bar.Children.Add(track);
        bar.Children.Add(fill);
        DockPanel.SetDock(bar, Dock.Left);
        row.Children.Add(bar);

        var stats = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        stats.Children.Add(Stat("Health", b.Health is { } h ? $"{Math.Round(h * 100)}%" : "—"));
        stats.Children.Add(Stat("Remaining", b.MinutesRemaining is { } m ? $"{m / 60}:{m % 60:00}" : "—"));
        row.Children.Add(stats);
        _batteryBody.Content = row;
    }

    private static UIElement Stat(string label, string value)
    {
        var stack = new StackPanel { MinWidth = 70, VerticalAlignment = VerticalAlignment.Center };
        var v = Ui.Text(value, 17, weight: FontWeights.SemiBold).Tabular();
        v.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(v);
        var l = Ui.Text(label.ToUpperInvariant(), 9, ThemeResources.SecondaryText, FontWeights.SemiBold);
        l.HorizontalAlignment = HorizontalAlignment.Center;
        l.Margin = new Thickness(0, 3, 0, 0);
        stack.Children.Add(l);
        return stack;
    }
}

/// A ring with the value in the middle and a label below.
internal sealed class RingGauge : StackPanel
{
    private readonly Ring _ring = new() { Width = 58, Height = 58, Thickness = 6 };
    private readonly TextBlock _value = Ui.Text("", 15.5, weight: FontWeights.Bold).Tabular();

    public RingGauge(string label)
    {
        _ring.SetResourceReference(Ring.TrackProperty, ThemeResources.AccentFaint);
        var face = new Grid { Width = 58, Height = 58 };
        face.Children.Add(_ring);
        _value.HorizontalAlignment = HorizontalAlignment.Center;
        face.Children.Add(_value);
        Children.Add(face);
        var caption = Ui.Text(label, 10, ThemeResources.SecondaryText, FontWeights.Medium);
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        caption.Margin = new Thickness(0, 6, 0, 0);
        Children.Add(caption);
    }

    public void Set(double fraction)
    {
        var f = Math.Clamp(fraction, 0, 1);
        _ring.Animate(Ring.ProgressProperty, Math.Max(0.001, f), Motion.ExpoOut(0.6));
        _value.Text = $"{Math.Round(f * 100)}";
    }
}

/// Minimal line chart for a short history, filled underneath.
internal sealed class Sparkline : FrameworkElement
{
    private IReadOnlyList<double> _values = Array.Empty<double>();
    private double _max = 1;

    public bool Filled { get; init; } = true;
    public string Colour { get; init; } = ThemeResources.Accent;

    public void Set(IReadOnlyList<double> values, double max)
    {
        _values = values.ToArray();
        _max = Math.Max(max, 0.000_001);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_values.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var colour = ((SolidColorBrush)FindResource(Colour)).Color;
        // Always span 60 samples, so a fresh chart grows in from the right.
        var count = Math.Max(_values.Count, 60);
        var step = ActualWidth / (count - 1);
        var points = _values.Select((v, i) => new Point((i + count - _values.Count) * step,
            ActualHeight * (1 - Math.Clamp(v / _max, 0, 1)))).ToList();

        if (Filled)
        {
            var area = new StreamGeometry();
            using (var c = area.Open())
            {
                c.BeginFigure(new Point(points[0].X, ActualHeight), true, true);
                c.PolyLineTo(points, true, true);
                c.LineTo(new Point(points[^1].X, ActualHeight), true, true);
            }
            area.Freeze();
            dc.DrawGeometry(new LinearGradientBrush(colour.WithOpacity(0.35), colour.WithOpacity(0.02), 90), null, area);
        }

        var line = new StreamGeometry();
        using (var c = line.Open())
        {
            c.BeginFigure(points[0], false, false);
            c.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        line.Freeze();
        dc.DrawGeometry(null, new Pen(colour.Brush(), 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
    }
}
