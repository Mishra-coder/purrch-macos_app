using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Features.Downloads;
using Purrch.Interface;

namespace Purrch.Features.Converter;

/// Format, size and quality chosen last time (%APPDATA%\Purrch\converter.json).
public sealed class ConverterSettings
{
    public ConvertFormat Format { get; set; } = ConvertFormat.Jpeg;
    public double Quality { get; set; } = 0.8;
    /// Longest side in pixels; null keeps the original size.
    public int? MaxDimension { get; set; }
}

/// Drop images (or click to choose), pick a format, size and quality, and convert. Results are saved next to
/// the originals and listed with how much smaller they got.
public sealed class ConverterModule : Grid
{
    private static readonly int?[] Dimensions = { null, 3840, 2048, 1280, 800 };

    // Kept for the session, so switching tabs or themes doesn't drop what you were working on.
    private static readonly List<string> Inputs = new();
    private static readonly List<ConversionResult> Results = new();
    private static bool _converting;
    /// A conversion finished; every converter on screen (there may be a rebuilt one) refreshes.
    private static event Action? SessionChanged;

    private readonly ConverterSettings _settings = Store.Load<ConverterSettings>("converter");
    private readonly Rectangle _dropFrame = new() { RadiusX = 16, RadiusY = 16 };
    private readonly ContentControl _dropContent = new();
    private readonly StackPanel _formatChips = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _sizeChips = new() { Orientation = Orientation.Horizontal };
    private readonly QualitySlider _slider;
    private readonly TextBlock _qualityText = Ui.Text("", 12, weight: FontWeights.SemiBold).Tabular();
    private readonly ContentControl _convertHost = new() { Margin = new Thickness(8, 0, 0, 0) };
    private readonly ContentControl _results = new();
    private bool _targeted;

    public ConverterModule()
    {
        if (!Dimensions.Contains(_settings.MaxDimension)) _settings.MaxDimension = null;
        _settings.Quality = Math.Clamp(_settings.Quality, 0.1, 1);

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        ColumnDefinitions.Add(new ColumnDefinition());

        var drop = DropZone();
        Children.Add(drop);

        var right = new DockPanel();
        SetColumn(right, 2);
        var options = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        options.Children.Add(OptionRow("Format", _formatChips));
        options.Children.Add(OptionRow("Max size", _sizeChips, top: 8));

        _slider = new QualitySlider(_settings.Quality, q =>
        {
            _settings.Quality = q;
            UpdateQuality();
        }, () => Store.Save("converter", _settings));
        var qualityRow = new DockPanel();
        DockPanel.SetDock(_convertHost, Dock.Right);
        qualityRow.Children.Add(_convertHost);
        _qualityText.Width = 72;
        _qualityText.TextAlignment = TextAlignment.Right;
        _qualityText.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(_qualityText, Dock.Right);
        qualityRow.Children.Add(_qualityText);
        qualityRow.Children.Add(_slider);
        options.Children.Add(OptionRow("Quality", qualityRow, top: 8));
        DockPanel.SetDock(options, Dock.Top);
        right.Children.Add(options);
        right.Children.Add(_results);
        Children.Add(right);

        RefreshChips();
        UpdateQuality();
        RefreshInputs();
        RefreshResults();
        Loaded += (_, _) => SessionChanged += RefreshSession;
        Unloaded += (_, _) => SessionChanged -= RefreshSession;
    }

    private void RefreshSession()
    {
        RefreshInputs();
        RefreshConvert();
        RefreshResults();
    }

    // MARK: - Drop zone

    private Grid DropZone()
    {
        var zone = new Grid { AllowDrop = true, Cursor = Cursors.Hand };
        _dropFrame.StrokeDashArray = new DoubleCollection { 7, 5 };
        zone.Children.Add(_dropFrame);
        _dropContent.Margin = new Thickness(10);
        zone.Children.Add(_dropContent);
        StyleFrame();

        zone.DragEnter += OnDrag;
        zone.DragOver += OnDrag;
        zone.DragLeave += (_, _) => SetTargeted(false);
        zone.Drop += (_, e) =>
        {
            SetTargeted(false);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) Add(paths);
            e.Handled = true;
        };
        var pressed = false;
        // Preview, because the thumbnail scroller swallows the plain event.
        zone.PreviewMouseLeftButtonDown += (_, _) => pressed = true;
        zone.MouseLeave += (_, _) => pressed = false;
        zone.MouseLeftButtonUp += (_, _) =>
        {
            if (pressed) Choose();
            pressed = false;
        };
        return zone;
    }

    private void OnDrag(object sender, DragEventArgs e)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        SetTargeted(files);
        e.Handled = true;
    }

    private void SetTargeted(bool targeted)
    {
        if (_targeted == targeted) return;
        _targeted = targeted;
        StyleFrame();
    }

    private void StyleFrame()
    {
        _dropFrame.StrokeThickness = _targeted ? 2 : 1;
        // Dash lengths are in multiples of the stroke width; keep them the same on screen.
        _dropFrame.StrokeDashArray = _targeted ? new DoubleCollection { 3.5, 2.5 } : new DoubleCollection { 7, 5 };
        _dropFrame.SetResourceReference(Shape.FillProperty, _targeted ? ThemeResources.AccentFaint : ThemeResources.Surface);
        _dropFrame.SetResourceReference(Shape.StrokeProperty, _targeted ? ThemeResources.Accent : ThemeResources.Border);
    }

    private void RefreshInputs()
    {
        if (Inputs.Count == 0)
        {
            var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var icon = Ui.Glyph("\uE8B9", 30, ThemeResources.Accent);
            if (AppSettings.Shared.Theme.Glow && Application.Current.Resources[ThemeResources.Accent] is SolidColorBrush accent)
                icon.Effect = ExpandedView.Glow(accent.Color, 6);
            empty.Children.Add(icon);
            var title = Ui.Text("Drop images", 14, weight: FontWeights.SemiBold);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.Margin = new Thickness(0, 8, 0, 8);
            empty.Children.Add(title);
            var hint = Ui.Text("or click to choose", 12, ThemeResources.SecondaryText);
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(hint);
            _dropContent.Content = empty;
            return;
        }

        var layout = new DockPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var clear = FileUi.Link("Clear", () =>
        {
            Inputs.Clear();
            RefreshInputs();
            RefreshConvert();
        });
        DockPanel.SetDock(clear, Dock.Right);
        header.Children.Add(clear);
        header.Children.Add(Ui.Text(Inputs.Count == 1 ? "1 image" : $"{Inputs.Count} images", 12, weight: FontWeights.SemiBold));
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        var more = Ui.Text("Click to add more", 11, ThemeResources.SecondaryText);
        more.HorizontalAlignment = HorizontalAlignment.Center;
        more.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(more, Dock.Bottom);
        layout.Children.Add(more);

        var grid = new WrapPanel();
        foreach (var path in Inputs) grid.Children.Add(InputTile(path));
        layout.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false, Content = grid });
        _dropContent.Content = layout;
    }

    private UIElement InputTile(string path)
    {
        var tile = new Grid { Width = 58, Height = 58, Margin = new Thickness(0, 0, 6, 6), ToolTip = System.IO.Path.GetFileName(path) };
        var thumb = FileVisuals.Thumbnail(path, 58, 8);
        thumb.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Hairline);
        tile.Children.Add(thumb);

        var remove = Ui.Bare(new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = Colors.Black.WithOpacity(0.6).Brush(),
            Child = new TextBlock { Text = "\uE711", FontSize = 7, Foreground = Brushes.White, Style = (Style)Application.Current.FindResource("Purrch.Glyph") },
        }, () =>
        {
            Inputs.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            RefreshInputs();
            RefreshConvert();
        }, "Remove");
        remove.HorizontalAlignment = HorizontalAlignment.Right;
        remove.VerticalAlignment = VerticalAlignment.Top;
        remove.Margin = new Thickness(0, 2, 2, 0);
        tile.Children.Add(remove);
        return tile;
    }

    // MARK: - Options

    private static UIElement OptionRow(string label, UIElement content, double top = 0)
    {
        var row = new DockPanel { Margin = new Thickness(0, top, 0, 0) };
        var caps = FileUi.Caps(label);
        caps.Width = 62;
        caps.TextWrapping = TextWrapping.NoWrap;
        DockPanel.SetDock(caps, Dock.Left);
        row.Children.Add(caps);
        row.Children.Add(content);
        return row;
    }

    private void RefreshChips()
    {
        _formatChips.Children.Clear();
        foreach (var format in ConvertFormatInfo.All)
            _formatChips.Children.Add(Chip(format.Title(), _settings.Format == format, () =>
            {
                _settings.Format = format;
                Store.Save("converter", _settings);
                RefreshChips();
                UpdateQuality();
            }));
        _sizeChips.Children.Clear();
        foreach (var dimension in Dimensions)
            _sizeChips.Children.Add(Chip(dimension?.ToString() ?? "Original", _settings.MaxDimension == dimension, () =>
            {
                _settings.MaxDimension = dimension;
                Store.Save("converter", _settings);
                RefreshChips();
            }));
    }

    private static UIElement Chip(string title, bool selected, Action onClick)
    {
        var text = Ui.Text(title, 11, selected ? ThemeResources.Background : ThemeResources.Text, FontWeights.SemiBold);
        var capsule = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 9, 4), Child = text };
        capsule.SetResourceReference(Border.BackgroundProperty, selected ? ThemeResources.Accent : ThemeResources.Fill);
        var button = Ui.Bare(capsule, onClick);
        button.Margin = new Thickness(0, 0, 6, 0);
        return button;
    }

    private void UpdateQuality()
    {
        var lossy = _settings.Format.Lossy();
        _slider.IsEnabled = lossy;
        _slider.Opacity = lossy ? 1 : 0.4;
        _qualityText.Text = _settings.Format switch
        {
            ConvertFormat.Gif => "256 colours",
            _ when !lossy => "Lossless",
            _ => $"{(int)(_settings.Quality * 100)}%",
        };
        RefreshConvert();
    }

    private void RefreshConvert()
    {
        var ready = Inputs.Count > 0;
        var button = Ui.Pill(_converting ? "Converting\u2026" : "Convert", "\uE895", StartConversion, prominent: ready);
        button.IsEnabled = ready && !_converting;
        button.Opacity = ready ? 1 : 0.5;
        _convertHost.Content = button;
    }

    // MARK: - Results

    private void RefreshResults()
    {
        if (Results.Count == 0)
        {
            var hint = Ui.Text("Converted files are saved next to the originals. Nothing leaves your PC.", 11, ThemeResources.SecondaryText);
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.TextAlignment = TextAlignment.Center;
            _results.Content = hint;
            return;
        }
        var list = new StackPanel();
        foreach (var result in Results) list.Children.Add(new ResultRow(result) { Margin = new Thickness(0, 0, 0, 6) });
        _results.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = list };
    }

    // MARK: - Actions

    private void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(p => ImageConverter.IsImage(p) && File.Exists(p)))
            if (!Inputs.Contains(path, StringComparer.OrdinalIgnoreCase)) Inputs.Add(path);
        RefreshInputs();
        RefreshConvert();
    }

    private void Choose()
    {
        var extensions = string.Join(";", ImageConverter.ImageExtensions.Select(e => "*" + e));
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose images",
            Multiselect = true,
            Filter = $"Images|{extensions}|All files|*.*",
        };
        // Treated like a right-click menu by the pill window, so the panel stays open while the dialog is up.
        RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, this));
        try
        {
            var owner = Window.GetWindow(this);
            if ((owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true) Add(dialog.FileNames);
        }
        finally
        {
            RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent, this));
        }
    }

    private async void StartConversion()
    {
        if (Inputs.Count == 0 || _converting) return;
        _converting = true;
        RefreshConvert();
        var paths = Inputs.ToList();
        var (format, quality, maxDimension) = (_settings.Format, _settings.Quality, _settings.MaxDimension);
        IReadOnlyList<ConversionResult> output;
        try
        {
            output = await Task.Run(() => ImageConverter.Convert(paths, format, quality, maxDimension));
        }
        catch (Exception e) when (ImageConverter.IsImageError(e))
        {
            Log.Write($"Converter failed: {e.Message}");
            output = new[] { new ConversionResult(paths, null, 0, 0, "Conversion failed") };
        }
        Results.InsertRange(0, output);
        Inputs.Clear();
        _converting = false;
        SessionChanged?.Invoke();
    }
}

/// Quality from 10% to 100%: click or drag along the track.
internal sealed class QualitySlider : Grid
{
    private readonly Border _fill = new() { CornerRadius = new CornerRadius(2), Height = 4, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _knob = new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly Action<double> _changed;
    private readonly Action _committed;
    private double _value;
    private bool _dragging;

    public QualitySlider(double value, Action<double> changed, Action committed)
    {
        _value = value;
        _changed = changed;
        _committed = committed;
        Height = 20;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        var track = new Border { CornerRadius = new CornerRadius(2), Height = 4, VerticalAlignment = VerticalAlignment.Center };
        track.SetResourceReference(Border.BackgroundProperty, ThemeResources.Fill);
        _fill.SetResourceReference(Border.BackgroundProperty, ThemeResources.Accent);
        _knob.SetResourceReference(Border.BackgroundProperty, ThemeResources.Text);
        _knob.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, Opacity = 0.4, BlurRadius = 4, ShadowDepth = 0 };
        Children.Add(track);
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
            _committed();
            e.Handled = true;
        };
        SizeChanged += (_, _) => Layout();
    }

    private void Move(MouseEventArgs e)
    {
        var usable = Math.Max(1, ActualWidth - _knob.Width);
        var fraction = Math.Clamp((e.GetPosition(this).X - _knob.Width / 2) / usable, 0, 1);
        _value = Math.Round((0.1 + fraction * 0.9) * 100) / 100;
        Layout();
        _changed(_value);
    }

    private void Layout()
    {
        var usable = Math.Max(0, ActualWidth - _knob.Width);
        var x = usable * (_value - 0.1) / 0.9;
        _fill.Width = x + _knob.Width / 2;
        _knob.Margin = new Thickness(x, 0, 0, 0);
    }
}

/// One converted file (or a failure): thumbnail, name, before and after sizes, and how much it saved.
internal sealed class ResultRow : ListRow
{
    private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);

    public ResultRow(ConversionResult result) : base(new DockPanel())
    {
        var layout = (DockPanel)Child;

        UIElement icon = result.Output is { } output
            ? FileVisuals.Thumbnail(output, 30, 6)
            : new TextBlock { Text = "\uE7BA", FontSize = 15, Foreground = Orange.Brush(), Style = (Style)Application.Current.FindResource("Purrch.Glyph"), Width = 30 };
        ((FrameworkElement)icon).Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        layout.Children.Add(icon);

        if (result.Output is { } file)
        {
            var reveal = Ui.Pill("Show in folder", "\uE721", () => FileMeta.Reveal(file));
            reveal.VerticalAlignment = VerticalAlignment.Center;
            reveal.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(reveal, Dock.Right);
            layout.Children.Add(reveal);
            FileUi.DragOut(this, () => File.Exists(file) ? FileMeta.DragData(new[] { file }) : null);
            ToolTip = file;
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Ui.Text(System.IO.Path.GetFileName(result.Output ?? result.Sources.FirstOrDefault() ?? ""), 12, weight: FontWeights.SemiBold);
        name.TextWrapping = TextWrapping.NoWrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(name);
        if (result.Error is { } error)
        {
            var message = new TextBlock { Text = error, FontSize = 11, Foreground = Orange.Brush(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            text.Children.Add(message);
        }
        else
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            var sizes = Ui.Text($"{Format.Bytes(result.Before)} \u2192 {Format.Bytes(result.After)}", 11, ThemeResources.SecondaryText);
            sizes.Margin = new Thickness(0, 0, 5, 0);
            line.Children.Add(sizes);
            var pct = (int)Math.Round(result.Saved * 100);
            line.Children.Add(FileUi.Tag(pct >= 0 ? $"-{pct}%" : $"+{-pct}%", pct >= 0 ? Green : Orange));
            text.Children.Add(line);
        }
        layout.Children.Add(text);
    }
}
