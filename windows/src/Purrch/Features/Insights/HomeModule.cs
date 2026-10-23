using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Features.Calendar;
using Purrch.Features.Media;
using Purrch.Features.Weather;
using Purrch.Interface;

namespace Purrch.Features.Insights;

/// Greeting and the time with battery, weather and next-event chips, then System, Launch and Now Playing.
public sealed class HomeModule : Grid
{
    private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);

    private readonly SystemMonitor _monitor = SystemMonitor.Shared;
    private readonly CalendarService _calendar = CalendarService.Shared;
    private readonly WeatherService _weather = WeatherService.Shared;
    private readonly NowPlayingService _player = NowPlayingService.Shared;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly TextBlock _greeting = Ui.Text("", 12, ThemeResources.SecondaryText, FontWeights.Medium);
    private readonly RollingText _time = new(44, FontWeights.Bold);
    private readonly TextBlock _amPm = Ui.Text("", 14, ThemeResources.SecondaryText, FontWeights.SemiBold);
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly RingGauge _cpu = new("CPU");
    private readonly RingGauge _ram = new("RAM");
    private readonly RingGauge _disk = new("Disk");
    private readonly WrapPanel _apps = new();
    private readonly ContentControl _playing = new();

    public HomeModule()
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(96) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        RowDefinitions.Add(new RowDefinition());

        Children.Add(Hero());

        var row = new Grid();
        for (var i = 0; i < 5; i++)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 1 ? new GridLength(10) : new GridLength(1, GridUnitType.Star) });
        Place(row, SystemCard(), 0);
        Place(row, LaunchCard(), 2);
        Place(row, _playing, 4);
        SetRow(row, 2);
        Children.Add(row);

        _clock.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            _monitor.Updated += RefreshSystem;
            _calendar.Changed += RefreshChips;
            _weather.Changed += RefreshChips;
            _player.Changed += RefreshPlaying;
            LauncherStore.Shared.Changed += RefreshApps;
            _monitor.Start();
            _calendar.Start();
            _weather.Start();
            _clock.Start();
            Tick();
            RefreshSystem();
            RefreshApps();
            RefreshPlaying();
        };
        Unloaded += (_, _) =>
        {
            _monitor.Updated -= RefreshSystem;
            _calendar.Changed -= RefreshChips;
            _weather.Changed -= RefreshChips;
            _player.Changed -= RefreshPlaying;
            LauncherStore.Shared.Changed -= RefreshApps;
            _monitor.Stop();
            _calendar.Stop();
            _weather.Stop();
            _clock.Stop();
        };
    }

    private static void Place(Grid grid, UIElement element, int column)
    {
        SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static void Go(ModuleId module) => NotchState.Shared.Select(module);

    // MARK: - Hero

    private UIElement Hero()
    {
        // Home's own tint (its colour in Playful), not whichever module was on screen before.
        var accent = AppSettings.Shared.Theme.AccentFor(ModuleId.Home);
        var surface = new Border { Style = (Style)Application.Current.FindResource("Purrch.Card") };
        var wash = new Border
        {
            CornerRadius = new CornerRadius(20),
            IsHitTestVisible = false,
            Background = new LinearGradientBrush(accent.WithOpacity(0.22), accent.WithOpacity(0.02), 0),
        };

        var clock = new StackPanel { Orientation = Orientation.Horizontal };
        clock.Children.Add(_time);
        _amPm.Margin = new Thickness(4, 0, 0, 7);
        _amPm.VerticalAlignment = VerticalAlignment.Bottom;
        clock.Children.Add(_amPm);
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_greeting);
        left.Children.Add(clock);

        var layout = new DockPanel { Margin = new Thickness(18, 0, 18, 0) };
        DockPanel.SetDock(_chips, Dock.Right);
        layout.Children.Add(_chips);
        layout.Children.Add(left);

        var hero = new Grid();
        hero.Children.Add(surface);
        hero.Children.Add(wash);
        hero.Children.Add(layout);
        return hero;
    }

    private void Tick()
    {
        var now = DateTime.Now;
        _greeting.Text = now.Hour switch
        {
            >= 5 and < 12 => "Good morning",
            >= 12 and < 17 => "Good afternoon",
            >= 17 and < 22 => "Good evening",
            _ => "Good night",
        };
        // Follows the Windows clock: "9:41" with "AM" beside it, or "21:41" on a 24-hour clock.
        var twelveHour = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('h');
        _time.Set(now.ToString(twelveHour ? "h:mm" : "HH:mm", CultureInfo.CurrentCulture));
        _amPm.Text = twelveHour ? now.ToString("tt", CultureInfo.CurrentCulture) : "";
        RefreshChips();
    }

    private void RefreshChips()
    {
        _chips.Children.Clear();
        _chips.Children.Add(BatteryChip());
        _chips.Children.Add(WeatherChip());
        _chips.Children.Add(EventChip());
    }

    private UIElement BatteryChip()
    {
        var b = _monitor.Battery;
        var colour = b.IsCharging ? Green : b.Present && b.Level < 0.2 ? Red : (Color?)null;
        var icon = Ui.Glyph(b.Present ? b.Glyph : "\uE83E", 13);
        if (colour is { } c) icon.Foreground = c.Brush();
        return Chip(icon,
            b.Present ? $"{Math.Round(b.Level * 100)}%" : "AC",
            b.IsCharging ? "Charging" : b.OnAC ? "Plugged in" : "Battery",
            () => Go(ModuleId.System));
    }

    private UIElement WeatherChip()
    {
        var w = _weather.Snapshot;
        var icon = new global::Emoji.Wpf.TextBlock { Text = WeatherCode.Emoji(w?.Code ?? 2, w?.IsDay ?? true), FontSize = 13 };
        return Chip(icon,
            w != null ? WeatherService.Temp(w.Temperature) : "--",
            w is { Place.Length: > 0 } ? w.Place : "Weather",
            () => Go(ModuleId.Weather));
    }

    private UIElement EventChip()
    {
        // Windows has no calendar store Purrch can read, so "Connect" leads to Calendar to sign in or add an iCal link.
        if (!_calendar.HasSources)
            return Chip(Ui.Glyph("\uE787", 13, ThemeResources.Accent), "Connect", "Calendar", () => Go(ModuleId.Calendar));
        if (_calendar.NextEvent is { } e)
        {
            var icon = Ui.Glyph("\uE787", 13);
            icon.Foreground = (_calendar.ColourOf(e.SourceId) ?? Colors.Gray).Brush();
            return Chip(icon, Countdown(e.Start, DateTime.Now), e.Title.Length > 0 ? e.Title : "Event", () => Go(ModuleId.Calendar), wide: true);
        }
        var free = Ui.Glyph("\uE73E", 13);
        free.Foreground = Green.Brush();
        return Chip(free, "Free", "No events", () => Go(ModuleId.Calendar));
    }

    private static string Countdown(DateTime start, DateTime now)
    {
        if (start <= now) return "Now";
        var minutes = (int)(start - now).TotalMinutes;
        if (minutes < 60) return $"in {Math.Max(1, minutes)}m";
        if (minutes < 24 * 60) return $"in {minutes / 60}h {minutes % 60}m";
        return $"in {minutes / (24 * 60)}d";
    }

    /// A small stat tile: icon, value and caption. It brightens and grows a little under the pointer.
    private static UIElement Chip(UIElement icon, string value, string caption, Action onClick, bool wide = false)
    {
        var stack = new StackPanel();
        if (icon is FrameworkElement f) f.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(icon);
        var big = Ui.Text(value, 15, weight: FontWeights.Bold).Tabular();
        big.TextWrapping = TextWrapping.NoWrap;
        big.TextTrimming = TextTrimming.CharacterEllipsis;
        big.Margin = new Thickness(0, 3, 0, 0);
        stack.Children.Add(big);
        var small = Ui.Text(caption, 10.5, ThemeResources.SecondaryText);
        small.TextWrapping = TextWrapping.NoWrap;
        small.TextTrimming = TextTrimming.CharacterEllipsis;
        small.Margin = new Thickness(0, 3, 0, 0);
        stack.Children.Add(small);

        var scale = new ScaleTransform(1, 1);
        var tile = new Border
        {
            Width = wide ? 132 : 92,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 9, 12, 9),
            BorderThickness = new Thickness(0.6),
            Child = stack,
            RenderTransform = scale,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        tile.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Hairline);
        tile.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
        tile.MouseEnter += (_, _) =>
        {
            tile.SetResourceReference(Border.BackgroundProperty, ThemeResources.Fill);
            scale.Animate(ScaleTransform.ScaleXProperty, 1.03, Motion.ExpoOut(0.3));
            scale.Animate(ScaleTransform.ScaleYProperty, 1.03, Motion.ExpoOut(0.3));
        };
        tile.MouseLeave += (_, _) =>
        {
            tile.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
            scale.Animate(ScaleTransform.ScaleXProperty, 1, Motion.ExpoOut(0.3));
            scale.Animate(ScaleTransform.ScaleYProperty, 1, Motion.ExpoOut(0.3));
        };

        var button = Ui.Bare(tile, onClick);
        button.Margin = new Thickness(8, 0, 0, 0);
        return button;
    }

    // MARK: - System

    private UIElement SystemCard()
    {
        var row = new UniformGrid { Columns = 3, VerticalAlignment = VerticalAlignment.Center };
        foreach (var gauge in new[] { _cpu, _ram, _disk })
        {
            gauge.HorizontalAlignment = HorizontalAlignment.Center;
            row.Children.Add(gauge);
        }
        var card = new Card("System", "\uE9D9", row) { Cursor = Cursors.Hand };
        card.MouseLeftButtonUp += (_, _) => Go(ModuleId.System);
        return card;
    }

    private void RefreshSystem()
    {
        _cpu.Set(_monitor.Cpu);
        _ram.Set(_monitor.MemoryFraction);
        _disk.Set(_monitor.DiskFraction);
    }

    // MARK: - Launch

    private UIElement LaunchCard()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _apps,
        };
        return new Card("Launch", "\uE8A9", scroll);
    }

    private void RefreshApps()
    {
        _apps.Children.Clear();
        foreach (var path in LauncherStore.Shared.Paths) _apps.Children.Add(AppButton(path));

        var plus = new Grid { Width = 32, Height = 32 };
        plus.Children.Add(new Rectangle
        {
            RadiusX = 8,
            RadiusY = 8,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 },
        });
        ((Rectangle)plus.Children[0]).SetResourceReference(Shape.StrokeProperty, ThemeResources.Border);
        var glyph = Ui.Glyph("\uE710", 12, ThemeResources.SecondaryText);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        plus.Children.Add(glyph);
        var add = Ui.Bare(plus, ChooseApps, "Add app");
        add.Margin = new Thickness(0, 0, 6, 6);
        _apps.Children.Add(add);
    }

    private UIElement AppButton(string path)
    {
        UIElement icon = path == LauncherStore.SettingsApp
            ? Ui.Glyph("\uE713", 22, ThemeResources.Text)
            : Downloads.FileVisuals.Icon(path, 32);
        var scale = new ScaleTransform(1, 1);
        var face = new Grid { Width = 32, Height = 32, RenderTransform = scale, RenderTransformOrigin = new Point(0.5, 0.5) };
        if (icon is FrameworkElement f)
        {
            f.HorizontalAlignment = HorizontalAlignment.Center;
            f.VerticalAlignment = VerticalAlignment.Center;
        }
        face.Children.Add(icon);
        face.MouseEnter += (_, _) =>
        {
            scale.Animate(ScaleTransform.ScaleXProperty, 1.12, Motion.Pop);
            scale.Animate(ScaleTransform.ScaleYProperty, 1.12, Motion.Pop);
        };
        face.MouseLeave += (_, _) =>
        {
            scale.Animate(ScaleTransform.ScaleXProperty, 1, Motion.Pop);
            scale.Animate(ScaleTransform.ScaleYProperty, 1, Motion.Pop);
        };

        var button = Ui.Bare(face, () => LauncherStore.Shared.Launch(path), LauncherStore.Name(path));
        button.Margin = new Thickness(0, 0, 6, 6);
        button.ContextMenu = Menu(path);
        return button;
    }

    private static ContextMenu Menu(string path)
    {
        var menu = new ContextMenu();
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Open", () => LauncherStore.Shared.Launch(path));
        if (path != LauncherStore.SettingsApp) Add("Show in File Explorer", () => FileMeta.Reveal(path));
        menu.Items.Add(new Separator());
        Add("Remove from Launch", () => LauncherStore.Shared.Remove(path));
        return menu;
    }

    private void ChooseApps()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add apps to Launch",
            Multiselect = true,
            // Start menu shortcuts are the easiest way to pick an installed app.
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Filter = "Apps and shortcuts|*.exe;*.lnk;*.url|All files|*.*",
            DereferenceLinks = false,
        };
        // Treated like a right-click menu by the pill window, so the panel stays open while the dialog is up.
        RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, this));
        try
        {
            var owner = Window.GetWindow(this);
            if ((owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true) LauncherStore.Shared.Add(dialog.FileNames);
        }
        finally
        {
            RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent, this));
        }
    }

    // MARK: - Now Playing

    private void RefreshPlaying()
    {
        var open = Ui.Bare(Ui.Glyph("\uE8A7", 11, ThemeResources.SecondaryText), () => Go(ModuleId.NowPlaying), "Open Now Playing");
        if (!_player.HasTrack)
        {
            _playing.Content = new Card("Now Playing", "\uE8D6",
                Ui.EmptyState("\uE8D6", "Nothing playing", "Spotify, a browser or any media app"), open);
            return;
        }

        var art = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(8), ClipToBounds = true };
        if (_player.Artwork is { } artwork)
            art.Background = new ImageBrush(artwork) { Stretch = Stretch.UniformToFill };
        else
        {
            art.SetResourceReference(Border.BackgroundProperty, ThemeResources.Fill);
            var note = Ui.Glyph("\uE8D6", 16, ThemeResources.SecondaryText);
            note.HorizontalAlignment = HorizontalAlignment.Center;
            art.Child = note;
        }

        var title = Ui.Text(_player.Title, 13, weight: FontWeights.SemiBold);
        var artist = Ui.Text(_player.Artist, 11, ThemeResources.SecondaryText);
        foreach (var t in new[] { title, artist })
        {
            t.TextWrapping = TextWrapping.NoWrap;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        var names = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(title);
        names.Children.Add(artist);
        var track = new DockPanel();
        DockPanel.SetDock(art, Dock.Left);
        track.Children.Add(art);
        track.Children.Add(names);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        controls.Children.Add(Ui.IconButton("\uE892", 26, _player.Previous, tooltip: "Previous"));
        var play = Ui.IconButton(_player.IsPlaying ? "\uE769" : "\uE768", 30, _player.TogglePlayPause, prominent: true,
            tooltip: _player.IsPlaying ? "Pause" : "Play");
        play.Margin = new Thickness(10, 0, 10, 0);
        controls.Children.Add(play);
        controls.Children.Add(Ui.IconButton("\uE893", 26, _player.Next, tooltip: "Next"));

        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(track);
        body.Children.Add(controls);
        _playing.Content = new Card("Now Playing", "\uE8D6", body, open);
    }
}

/// Text whose changed characters roll in: the old one slides down and fades while the new one drops in from
/// above, like the Mac clock's numeric content transition. Unchanged characters stay still.
internal sealed class RollingText : StackPanel
{
    private const double Travel = 14;

    private readonly double _size;
    private readonly FontWeight _weight;
    private string _text = "";

    public RollingText(double size, FontWeight weight)
    {
        _size = size;
        _weight = weight;
        Orientation = Orientation.Horizontal;
    }

    public void Set(string text)
    {
        if (text == _text) return;
        // A different length ("9:59" to "10:00") just swaps; the characters no longer line up.
        if (_text.Length != text.Length || !IsLoaded)
        {
            Children.Clear();
            foreach (var c in text) Children.Add(Slot(c));
            _text = text;
            return;
        }
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == _text[i]) continue;
            var slot = (Grid)Children[i];
            foreach (var old in slot.Children.OfType<TextBlock>().ToList())
            {
                ((TranslateTransform)old.RenderTransform).Animate(TranslateTransform.YProperty, Travel, Motion.ExpoOut(0.4));
                old.Animate(OpacityProperty, 0, Motion.ExpoOut(0.3), completed: () => slot.Children.Remove(old));
            }
            var glyph = Glyph(text[i]);
            glyph.Opacity = 0;
            var shift = (TranslateTransform)glyph.RenderTransform;
            shift.Y = -Travel;
            slot.Children.Add(glyph);
            shift.Animate(TranslateTransform.YProperty, 0, Motion.ExpoOut(0.5));
            glyph.Animate(OpacityProperty, 1, Motion.ExpoOut(0.4));
        }
        _text = text;
    }

    private Grid Slot(char c)
    {
        var slot = new Grid { ClipToBounds = true };
        slot.Children.Add(Glyph(c));
        return slot;
    }

    private TextBlock Glyph(char c)
    {
        var t = Ui.Text(c.ToString(), _size, weight: _weight).Tabular();
        t.TextWrapping = TextWrapping.NoWrap;
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.RenderTransform = new TranslateTransform();
        return t;
    }
}
