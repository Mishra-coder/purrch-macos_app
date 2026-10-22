using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch.Features.Timers;

/// Pomodoro, countdown, stopwatch and water, side by side like the Mac module.
public sealed class TimersModule : Grid
{
    private readonly TimerService _timers = TimerService.Shared;

    // Pomodoro
    private readonly Ring _pomoRing = new() { Width = 136, Height = 136 };
    private readonly TextBlock _pomoTime = Big(34);
    private readonly TextBlock _pomoPhase = Ui.Text("", 12, ThemeResources.SecondaryText);
    private readonly TextBlock _pomoDone = Ui.Text("", 11, ThemeResources.SecondaryText);
    private readonly StackPanel _pomoButtons = Row(14);
    private readonly StackPanel _pomoSettings = Row(6);

    // Countdown
    private readonly TextBlock _countTime = Big(48);
    private readonly Waveform _wave = new() { Height = 40 };
    private readonly StackPanel _countAccessory = Row(4);
    private readonly StackPanel _presets = Row(8);
    private readonly StackPanel _countButtons = Row(8);

    // Stopwatch
    private readonly TextBlock _swTime = Big(28);
    private readonly StackPanel _swButtons = Row(6);
    private readonly StackPanel _laps = new();

    // Water
    private readonly TextBlock _waterCount = Ui.Text("", 12, weight: FontWeights.Medium);
    private readonly ContentControl _waterSwitch = new();
    private readonly StackPanel _drops = Row(6);
    private readonly TextBlock _waterHint = Ui.Text("", 10, ThemeResources.SecondaryText);

    public TimersModule()
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        ColumnDefinitions.Add(new ColumnDefinition());

        Add(PomodoroCard(), 0);
        Add(CountdownCard(), 2);
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition());
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        right.RowDefinitions.Add(new RowDefinition());
        var stopwatch = StopwatchCard();
        var water = WaterCard();
        SetRow(water, 2);
        right.Children.Add(stopwatch);
        right.Children.Add(water);
        Add(right, 4);

        RebuildControls();
        UpdateTimes();
        // Only listen while on screen; the service outlives this view.
        Loaded += (_, _) =>
        {
            _timers.StateChanged += OnStateChanged;
            _timers.Ticked += UpdateTimes;
            RebuildControls();
            UpdateTimes();
        };
        Unloaded += (_, _) =>
        {
            _timers.StateChanged -= OnStateChanged;
            _timers.Ticked -= UpdateTimes;
        };
    }

    private void Add(UIElement element, int column)
    {
        SetColumn(element, column);
        Children.Add(element);
    }

    private void OnStateChanged()
    {
        RebuildControls();
        UpdateTimes();
    }

    // MARK: - Cards

    private UIElement PomodoroCard()
    {
        var face = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 10) };
        face.Children.Add(_pomoRing);
        var labels = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _pomoTime.HorizontalAlignment = HorizontalAlignment.Center;
        _pomoPhase.HorizontalAlignment = HorizontalAlignment.Center;
        labels.Children.Add(_pomoTime);
        labels.Children.Add(_pomoPhase);
        face.Children.Add(labels);

        _pomoButtons.HorizontalAlignment = HorizontalAlignment.Center;
        var body = new DockPanel();
        DockPanel.SetDock(face, Dock.Top);
        DockPanel.SetDock(_pomoButtons, Dock.Top);
        DockPanel.SetDock(_pomoSettings, Dock.Bottom);
        body.Children.Add(face);
        body.Children.Add(_pomoButtons);
        body.Children.Add(_pomoSettings);
        body.Children.Add(new Border());
        return new Card("Pomodoro", "\uE91F", body, _pomoDone);
    }

    private UIElement CountdownCard()
    {
        _countTime.HorizontalAlignment = HorizontalAlignment.Center;
        _presets.HorizontalAlignment = HorizontalAlignment.Center;
        _countButtons.HorizontalAlignment = HorizontalAlignment.Center;
        _wave.Margin = new Thickness(0, 10, 0, 0);

        var top = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        top.Children.Add(_countTime);
        top.Children.Add(_wave);

        var bottom = new StackPanel();
        bottom.Children.Add(_presets);
        _countButtons.Margin = new Thickness(0, 10, 0, 0);
        bottom.Children.Add(_countButtons);

        var body = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(top);
        body.Children.Add(bottom);
        body.Children.Add(new Border());
        return new Card("Timer", "\uE121", body, _countAccessory);
    }

    private UIElement StopwatchCard()
    {
        var top = new DockPanel();
        DockPanel.SetDock(_swButtons, Dock.Right);
        top.Children.Add(_swButtons);
        _swTime.TextWrapping = TextWrapping.NoWrap;
        top.Children.Add(new Viewbox { Child = _swTime, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left });

        var lapScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = _laps,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var body = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);
        body.Children.Add(lapScroll);
        return new Card("Stopwatch", "\uE916", body);
    }

    private UIElement WaterCard()
    {
        var top = new DockPanel();
        DockPanel.SetDock(_waterSwitch, Dock.Right);
        top.Children.Add(_waterSwitch);
        top.Children.Add(_waterCount);

        _drops.Margin = new Thickness(0, 7, 0, 7);
        var body = new StackPanel();
        body.Children.Add(top);
        body.Children.Add(_drops);
        body.Children.Add(_waterHint);
        var icon = DropShape.Make(filled: true, height: 11);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        return new Card("Drink Water", icon, body);
    }

    // MARK: - Updating

    /// Rebuilds buttons and lists whose shape depends on state (running or not, laps, drops).
    private void RebuildControls()
    {
        var t = _timers;

        _pomoDone.Text = $"{t.CompletedPomodoros} done";
        _pomoButtons.Children.Clear();
        _pomoButtons.Children.Add(Ui.IconButton(t.PomoRunning ? "\uE769" : "\uE768", 34, t.PomoToggle,
            prominent: !t.PomoRunning, tooltip: t.PomoRunning ? "Pause" : "Start"));
        _pomoButtons.Children.Add(Ui.IconButton("\uE72C", 34, t.PomoReset, tooltip: "Reset"));
        _pomoButtons.Children.Add(Ui.IconButton("\uE893", 34, t.PomoSkip, tooltip: "Skip"));

        _pomoSettings.Children.Clear();
        _pomoSettings.Children.Add(Stepper(() => t.FocusMinutes = t.FocusMinutes < 5 ? 5 : t.FocusMinutes + 5,
                                           () => t.FocusMinutes -= 5));
        _pomoSettings.Children.Add(Ui.Text($"{t.FocusMinutes}m focus", 11, ThemeResources.SecondaryText, FontWeights.Medium));
        var slash = Ui.Text("/", 11, ThemeResources.SecondaryText);
        slash.Opacity = 0.6;
        _pomoSettings.Children.Add(slash);
        _pomoSettings.Children.Add(Stepper(() => t.BreakMinutes += 1, () => t.BreakMinutes -= 1));
        _pomoSettings.Children.Add(Ui.Text($"{t.BreakMinutes}m break", 11, ThemeResources.SecondaryText, FontWeights.Medium));

        _countAccessory.Children.Clear();
        if (!t.CountdownActive)
        {
            _countAccessory.Children.Add(Ui.Bare(Ui.Glyph("\uE738", 10, ThemeResources.SecondaryText), () => t.AdjustCustom(-1), "One minute less"));
            var minutes = Ui.Text($"{t.CustomMinutes}m", 11, ThemeResources.SecondaryText).Tabular();
            minutes.MinWidth = 26;
            minutes.TextAlignment = TextAlignment.Center;
            _countAccessory.Children.Add(minutes);
            _countAccessory.Children.Add(Ui.Bare(Ui.Glyph("\uE710", 10, ThemeResources.SecondaryText), () => t.AdjustCustom(1), "One minute more"));
        }

        _presets.Children.Clear();
        foreach (var m in new[] { 5, 10, 25 })
        {
            var selected = t.CountdownActive && (int)(t.CountdownTotal / 60) == m;
            var label = Ui.Text($"{m}m", 12, selected ? ThemeResources.Background : ThemeResources.Text, FontWeights.SemiBold);
            var chip = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 4, 12, 5), Child = label };
            chip.SetResourceReference(Border.BackgroundProperty, selected ? ThemeResources.Accent : ThemeResources.Fill);
            var minutes = m;
            _presets.Children.Add(Ui.Bare(chip, () => t.CountdownStart(minutes)));
        }

        _countButtons.Children.Clear();
        if (t.CountdownActive)
        {
            _countButtons.Children.Add(Ui.Pill(t.CountdownRunning ? "Pause" : "Resume", t.CountdownRunning ? "\uE769" : "\uE768",
                t.CountdownToggle, prominent: true));
            _countButtons.Children.Add(Ui.Pill("Cancel", "\uE711", t.CountdownCancel));
        }
        else
        {
            _countButtons.Children.Add(Ui.Pill($"Start {t.CustomMinutes}m", "\uE768", t.CountdownToggle, prominent: true));
        }

        _swButtons.Children.Clear();
        _swButtons.Children.Add(Ui.IconButton("\uE7C1", 28, t.StopwatchLap, tooltip: "Lap"));
        _swButtons.Children.Add(Ui.IconButton("\uE72C", 28, t.StopwatchReset, tooltip: "Reset"));
        _swButtons.Children.Add(Ui.IconButton(t.StopwatchRunning ? "\uE769" : "\uE768", 28, t.StopwatchToggle,
            prominent: !t.StopwatchRunning, tooltip: t.StopwatchRunning ? "Pause" : "Start"));

        _laps.Children.Clear();
        if (t.Laps.Count == 0)
        {
            _laps.Children.Add(Ui.Text("Flag to record laps", 11, ThemeResources.SecondaryText));
        }
        for (var i = 0; i < t.Laps.Count; i++)
        {
            var colour = i == 0 ? ThemeResources.Text : ThemeResources.SecondaryText;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var time = Ui.Text(StopwatchFormat(t.Laps[i]), 11, colour, FontWeights.Medium).Tabular();
            DockPanel.SetDock(time, Dock.Right);
            row.Children.Add(time);
            row.Children.Add(Ui.Text($"Lap {t.Laps.Count - i}", 11, colour, FontWeights.Medium));
            _laps.Children.Add(row);
        }

        _waterCount.Text = $"{t.Glasses} of {t.WaterGoal} glasses today";
        var reminder = Ui.Switch(t.WaterEnabled, on => t.WaterEnabled = on);
        reminder.ToolTip = "Remind me every 30 minutes";
        _waterSwitch.Content = reminder;
        _drops.Children.Clear();
        for (var i = 0; i < t.WaterGoal; i++)
        {
            var index = i;
            _drops.Children.Add(Ui.Bare(DropShape.Make(filled: i < t.Glasses), () => t.TapDrop(index)));
        }
        _waterHint.Text = t.WaterEnabled
            ? "Tap a drop after each glass. Reminding you every 30 min."
            : "Tap a drop after each glass. Turn on for 30 min reminders.";
    }

    /// The parts that move every tick.
    private void UpdateTimes()
    {
        var t = _timers;
        _pomoTime.Text = Format.Duration(Math.Ceiling(t.PomoRemaining));
        _pomoPhase.Text = t.PomoPhase.Title();
        _pomoRing.Progress = Math.Max(0.002, 1 - t.PomoProgress);

        var remaining = t.CountdownRemaining;
        var total = t.CountdownActive ? t.CountdownTotal : t.CustomMinutes * 60;
        _countTime.Text = Format.Duration(Math.Ceiling(remaining));
        _wave.Progress = total > 0 ? 1 - remaining / total : 0;
        _wave.Animating = t.CountdownRunning;
        _wave.InvalidateVisual();

        _swTime.Text = StopwatchFormat(t.StopwatchElapsed);
    }

    // MARK: - Helpers

    private static string StopwatchFormat(double seconds)
    {
        var cs = (int)Math.Floor(seconds * 100);
        int h = cs / 360_000, m = cs / 6000 % 60, s = cs / 100 % 60, c = cs % 100;
        return h > 0 ? $"{h}:{m:00}:{s:00}.{c:00}" : $"{m}:{s:00}.{c:00}";
    }

    private static TextBlock Big(double size)
    {
        var text = Ui.Text("", size, weight: FontWeights.Bold).Tabular();
        text.TextWrapping = TextWrapping.NoWrap;
        return text;
    }

    private static StackPanel Row(double spacing) => new SpacedRow(spacing);

    /// Compact chevron up/down stepper.
    private static UIElement Stepper(Action up, Action down)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Ui.Bare(new Border { Width = 14, Height = 9, Background = Brushes.Transparent, Child = Ui.Glyph("\uE70E", 8, ThemeResources.SecondaryText) }, up));
        stack.Children.Add(Ui.Bare(new Border { Width = 14, Height = 9, Background = Brushes.Transparent, Child = Ui.Glyph("\uE70D", 8, ThemeResources.SecondaryText) }, down));
        return stack;
    }
}

/// Horizontal stack with a gap between children, like an HStack with spacing.
internal sealed class SpacedRow : StackPanel
{
    private readonly double _spacing;

    public SpacedRow(double spacing)
    {
        _spacing = spacing;
        Orientation = Orientation.Horizontal;
    }

    protected override void OnVisualChildrenChanged(DependencyObject? added, DependencyObject? removed)
    {
        base.OnVisualChildrenChanged(added, removed);
        for (var i = 0; i < Children.Count; i++)
            if (Children[i] is FrameworkElement fe) fe.Margin = new Thickness(i == 0 ? 0 : _spacing, 0, 0, 0);
    }
}

/// Symmetric bar waveform for the countdown; bars left of the playhead are lit.
public sealed class Waveform : FrameworkElement
{
    private const int Count = 44;

    public double Progress { get; set; }
    public bool Animating { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        var lit = (Brush)FindResource(ThemeResources.Accent);
        var dim = (Brush)FindResource(ThemeResources.Fill);
        const double gap = 1.6;
        var barW = Math.Max(1, (ActualWidth - gap * (Count - 1)) / Count);
        var litCount = (int)Math.Floor(Count * Math.Clamp(Progress, 0, 1));
        var time = Environment.TickCount64 / 1000.0;
        for (var i = 0; i < Count; i++)
        {
            // Fixed "speech-like" envelope plus a gentle shimmer while running.
            var amp = 0.25 + 0.75 * Math.Abs(Math.Sin(i * 0.45) * Math.Cos(i * 0.17 + 1.3));
            if (Animating) amp *= 0.85 + 0.15 * Math.Sin(time * 5 + i * 0.8);
            var h = Math.Max(3, ActualHeight * amp);
            var rect = new Rect(i * (barW + gap), (ActualHeight - h) / 2, barW, h);
            dc.DrawRoundedRectangle(i < litCount ? lit : dim, null, rect, barW / 2, barW / 2);
        }
    }
}
