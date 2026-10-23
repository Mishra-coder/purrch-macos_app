using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch.Features.Media;

/// Ambience: ten synthesized soundscapes to layer, each with its own volume, plus a master volume.
public sealed class AmbienceModule : Grid
{
    private readonly AmbienceEngine _engine = AmbienceEngine.Shared;
    private readonly TextBlock _status = Ui.Text("", 12, weight: FontWeights.Medium);
    private readonly MusicBars _bars = new() { Width = 16, Height = 12, Margin = new Thickness(12, 0, 0, 0) };
    private readonly ContentControl _stopAll = new() { Margin = new Thickness(12, 0, 0, 0) };
    private readonly List<SoundTile> _tiles = new();

    public AmbienceModule()
    {
        var layout = new DockPanel();

        var header = new DockPanel { Margin = new Thickness(4, 0, 4, 10) };
        var speaker = Ui.Glyph("\uE767", 13, ThemeResources.Accent);
        speaker.FontWeight = FontWeights.SemiBold;
        speaker.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(speaker, Dock.Left);
        header.Children.Add(speaker);

        var volume = new StackPanel { Orientation = Orientation.Horizontal };
        volume.Children.Add(Ui.Glyph("\uE992", 10, ThemeResources.SecondaryText));
        var master = new VolumeSlider(_engine.MasterVolume, v => _engine.MasterVolume = v, small: true)
        {
            Width = 150,
            Margin = new Thickness(8, 0, 8, 0),
        };
        volume.Children.Add(master);
        volume.Children.Add(Ui.Glyph("\uE995", 10, ThemeResources.SecondaryText));
        volume.Children.Add(_stopAll);
        DockPanel.SetDock(volume, Dock.Right);
        header.Children.Add(volume);

        var status = new StackPanel { Orientation = Orientation.Horizontal };
        _status.TextWrapping = TextWrapping.NoWrap;
        status.Children.Add(_status);
        status.Children.Add(_bars);
        header.Children.Add(status);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        var grid = new UniformGrid5();
        foreach (var sound in AmbientSoundInfo.All)
        {
            var tile = new SoundTile(sound, _engine);
            _tiles.Add(tile);
            grid.Add(tile);
        }
        DockPanel.SetDock(grid, Dock.Top);
        layout.Children.Add(grid);
        layout.Children.Add(new Border());
        Children.Add(layout);

        Refresh();
        // Only listen while on screen; the engine outlives this view.
        Loaded += (_, _) => { _engine.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => _engine.Changed -= Refresh;
    }

    private void Refresh()
    {
        var playing = _engine.IsPlaying;
        _status.Text = playing ? $"{_engine.Active.Count} playing" : "Tap sounds to layer them";
        _status.SetResourceReference(TextBlock.ForegroundProperty, playing ? ThemeResources.Text : ThemeResources.SecondaryText);
        _bars.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        var stop = Ui.Pill("Stop all", "\uE71A", _engine.StopAll, prominent: playing);
        stop.IsEnabled = playing;
        stop.Opacity = playing ? 1 : 0.5;
        _stopAll.Content = stop;
        foreach (var tile in _tiles) tile.Refresh();
    }

    /// Five equal columns with 8 px between tiles, like the Mac grid.
    private sealed class UniformGrid5 : Grid
    {
        private const int Columns = 5;

        public void Add(UIElement child)
        {
            var index = Children.Count;
            var row = index / Columns;
            if (ColumnDefinitions.Count == 0)
                for (var c = 0; c < Columns * 2 - 1; c++)
                    ColumnDefinitions.Add(new ColumnDefinition { Width = c % 2 == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(8) });
            while (RowDefinitions.Count < row * 2 + 1)
                RowDefinitions.Add(new RowDefinition { Height = RowDefinitions.Count % 2 == 0 ? GridLength.Auto : new GridLength(8) });
            SetRow(child, row * 2);
            SetColumn(child, index % Columns * 2);
            Children.Add(child);
        }
    }
}

/// One sound: a round icon that toggles it, its name, and its own volume.
internal sealed class SoundTile : Border
{
    private readonly AmbientSound _sound;
    private readonly AmbienceEngine _engine;
    private readonly Border _circle = new() { Width = 38, Height = 38, CornerRadius = new CornerRadius(19) };
    private readonly FrameworkElement _icon;
    private readonly TextBlock _title;
    private readonly VolumeSlider _slider;
    private readonly bool _glow = AppSettings.Shared.Theme.Glow;
    private bool _hovering;
    private bool? _shownOn;

    public SoundTile(AmbientSound sound, AmbienceEngine engine)
    {
        _sound = sound;
        _engine = engine;
        Height = 112;
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(10);

        _icon = SoundIcon.Make(sound);
        _circle.Child = _icon;
        _title = Ui.Text(sound.Title(), 11, weight: FontWeights.SemiBold);
        _title.TextWrapping = TextWrapping.NoWrap;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.Margin = new Thickness(0, 6, 0, 0);

        var button = new StackPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        button.Children.Add(_circle);
        button.Children.Add(_title);
        button.MouseEnter += (_, _) => { _hovering = true; Refresh(); };
        button.MouseLeave += (_, _) => { _hovering = false; Refresh(); };
        button.MouseLeftButtonUp += (_, _) => _engine.Toggle(_sound);

        _slider = new VolumeSlider(engine.Volume(sound), v => _engine.SetVolume(_sound, v), small: true)
        {
            Margin = new Thickness(0, 6, 0, 0),
        };

        var stack = new DockPanel();
        DockPanel.SetDock(_slider, Dock.Bottom);
        stack.Children.Add(_slider);
        stack.Children.Add(button);
        Child = stack;
        Refresh();
    }

    public void Refresh()
    {
        var on = _engine.IsOn(_sound);
        _circle.SetResourceReference(BackgroundProperty, on ? ThemeResources.Accent : _hovering ? ThemeResources.Fill : ThemeResources.Hairline);
        _title.SetResourceReference(TextBlock.ForegroundProperty, on ? ThemeResources.Text : ThemeResources.SecondaryText);
        _slider.Opacity = on ? 1 : 0.45;
        SetResourceReference(BackgroundProperty, on ? ThemeResources.AccentFaint : ThemeResources.Surface);
        SetResourceReference(BorderBrushProperty, on ? ThemeResources.Accent : ThemeResources.Border);
        BorderThickness = new Thickness(on ? 1.2 : 0.8);
        if (_shownOn == on) return;
        _shownOn = on;

        SoundIcon.Recolour(_icon, on ? ThemeResources.Background : ThemeResources.Text);
        _circle.Effect = on && _glow
            ? new DropShadowEffect { Color = AppSettings.Shared.Theme.AccentFor(ModuleId.Ambience), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.6 }
            : null;
        // A slow pulse while the sound plays.
        _icon.BeginAnimation(OpacityProperty, on
            ? new DoubleAnimation(1, 0.45, TimeSpan.FromSeconds(0.9))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            }
            : null);
    }
}

/// Icons for the sounds. Segoe's icon fonts have no weather glyphs, so nature sounds use the
/// emoji font's outline glyphs (tinted like any text), and the three noises are drawn as waveforms.
internal static class SoundIcon
{
    public static FrameworkElement Make(AmbientSound sound)
    {
        var emoji = sound switch
        {
            AmbientSound.Rain => "\U0001F327",
            AmbientSound.Storm => "\U000026C8",
            AmbientSound.Wind => "\U0001F4A8",
            AmbientSound.Ocean => "\U0001F30A",
            AmbientSound.Fire => "\U0001F525",
            AmbientSound.Stream => "\U0001F4A7",
            AmbientSound.Lofi => "\U0000266B",
            _ => null,
        };
        if (emoji != null)
        {
            return new TextBlock
            {
                Text = emoji,
                FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"),
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        var data = sound switch
        {
            // Bars of different heights (waveform).
            AmbientSound.White => "M 1,6 L 1,10 M 4,3 L 4,13 M 7,0.5 L 7,15.5 M 10,4 L 10,12 M 13,1.5 L 13,14.5 M 16,5.5 L 16,10.5",
            // A smooth wave (waveform.path).
            AmbientSound.Pink => "M 0,8 C 2,1 4,1 6,8 S 10,15 12,8 S 16,1 18,8",
            // A heartbeat line (waveform.path.ecg).
            _ => "M 0,9 L 5,9 L 7,3 L 10,15 L 12,9 L 18,9",
        };
        return new Path
        {
            Data = Geometry.Parse(data),
            Width = 18,
            Height = 16,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static void Recolour(FrameworkElement icon, string colour)
    {
        if (icon is TextBlock text) text.SetResourceReference(TextBlock.ForegroundProperty, colour);
        else if (icon is Path path) path.SetResourceReference(Shape.StrokeProperty, colour);
    }
}

/// A thin themed volume slider: click or drag anywhere along it.
internal sealed class VolumeSlider : Grid
{
    private readonly Border _track = new() { CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _fill = new() { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _knob = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly Action<double> _changed;
    private double _value;
    private bool _dragging;

    public VolumeSlider(double value, Action<double> changed, bool small = false)
    {
        _value = Math.Clamp(value, 0, 1);
        _changed = changed;
        Height = 16;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        _track.Height = _fill.Height = small ? 3 : 4;
        _knob.Width = _knob.Height = small ? 10 : 12;
        _knob.CornerRadius = new CornerRadius(_knob.Width / 2);
        _knob.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.4, BlurRadius = 4, ShadowDepth = 0 };
        _track.SetResourceReference(Border.BackgroundProperty, ThemeResources.Fill);
        _fill.SetResourceReference(Border.BackgroundProperty, ThemeResources.Accent);
        _knob.SetResourceReference(Border.BackgroundProperty, ThemeResources.Text);
        Children.Add(_track);
        Children.Add(_fill);
        Children.Add(_knob);

        MouseLeftButtonDown += (_, e) =>
        {
            CaptureMouse();
            _dragging = true;
            Move(e);
            e.Handled = true;
        };
        MouseMove += (_, e) => { if (_dragging) Move(e); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
        };
        LostMouseCapture += (_, _) => _dragging = false;
        // The wheel nudges the volume, in steps of 5%.
        MouseWheel += (_, e) =>
        {
            Set(_value + Math.Sign(e.Delta) * 0.05);
            e.Handled = true;
        };
        SizeChanged += (_, _) => Layout();
    }

    private void Move(MouseEventArgs e)
    {
        var inset = _knob.Width / 2;
        Set((e.GetPosition(this).X - inset) / Math.Max(1, ActualWidth - inset * 2));
    }

    private void Set(double value)
    {
        value = Math.Clamp(value, 0, 1);
        if (value == _value) return;
        _value = value;
        Layout();
        _changed(value);
    }

    private void Layout()
    {
        var inset = _knob.Width / 2;
        var x = inset + _value * Math.Max(0, ActualWidth - inset * 2);
        _fill.Width = x;
        _knob.Margin = new Thickness(x - inset, 0, 0, 0);
    }
}
