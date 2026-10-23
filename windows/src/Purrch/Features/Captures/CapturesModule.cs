using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch.Features.Captures;

/// Screenshots and screen recordings in a grid, newest first: filter, preview, copy, drag out,
/// select several, and delete with Undo.
public sealed class CapturesModule : Grid
{
    private const double TileMin = 104, TileMax = 132, Spacing = 8;
    private static readonly Color Red = Color.FromRgb(0xFF, 0x3B, 0x30);

    private readonly CapturesModel _model = new();
    private readonly StackPanel _segments = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _toolbarActions = new() { Orientation = Orientation.Horizontal };
    private readonly ContentControl _body = new();
    private readonly WrapPanel _grid = new();
    private readonly ScrollViewer _scroll;
    private readonly Grid _previewHost = new();
    private readonly ContentControl _bottomBar = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(0, 0, 0, 4),
    };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    // Tiles are kept per file version, so a refresh doesn't redraw every thumbnail.
    private readonly Dictionary<CaptureFile, CaptureTile> _tiles = new();
    private readonly HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private CaptureFilter _filter = CaptureFilter.All;
    private bool _selecting;
    private CaptureFile? _previewing;
    private string? _toast;
    private string _bottomKind = "";
    private double _tileWidth = 116;

    public CapturesModule()
    {
        var layout = new DockPanel();
        var toolbar = Toolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(toolbar);
        _grid.Margin = new Thickness(0, 4, 0, 4);
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _grid };
        _scroll.SizeChanged += (_, _) => LayoutTiles();
        _scroll.ScrollChanged += (_, _) => LoadVisibleThumbnails();
        layout.Children.Add(_body);
        Children.Add(layout);

        Children.Add(_previewHost);
        Children.Add(_bottomBar);

        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast = null; RefreshBottomBar(); };
        _clock.Tick += (_, _) => { foreach (var tile in _tiles.Values) tile.RefreshAge(); };
        _model.Changed += Rebuild;
        Rebuild();
        Loaded += (_, _) => { _model.Start(); _clock.Start(); };
        Unloaded += (_, _) => { _model.Stop(); _clock.Stop(); _toastTimer.Stop(); };
    }

    private List<CaptureFile> Visible() => _model.Files.Where(f => _filter.Matches(f)).ToList();

    private void Rebuild()
    {
        var visible = Visible();
        _selection.IntersectWith(visible.Select(f => f.Path));
        if (visible.Count == 0 && _selecting) _selecting = false;
        RefreshToolbar(visible);

        // Drop tiles for files that are gone or have changed.
        foreach (var stale in _tiles.Keys.Where(k => !_model.Files.Contains(k)).ToList())
        {
            _tiles[stale].Release();
            _tiles.Remove(stale);
        }

        if (visible.Count == 0)
        {
            _body.Content = EmptyState();
        }
        else
        {
            _grid.Children.Clear();
            foreach (var file in visible) _grid.Children.Add(Tile(file));
            _body.Content = _scroll;
            LayoutTiles();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, LoadVisibleThumbnails);
        }

        if (_previewing is { } shown && !visible.Contains(shown))
            _previewing = visible.FirstOrDefault(f => f.Path == shown.Path);
        RefreshPreview();
        RefreshBottomBar();
    }

    private CaptureTile Tile(CaptureFile file)
    {
        if (!_tiles.TryGetValue(file, out var tile))
        {
            tile = new CaptureTile(file,
                onOpen: () =>
                {
                    if (_selecting) Toggle(file);
                    else Preview(file);
                },
                onCopy: () => Copy(new[] { file }),
                onReveal: () => FileMeta.Reveal(file.Path),
                onTrash: () => _model.Trash(new[] { file }));
            _tiles[file] = tile;
        }
        tile.SetWidth(_tileWidth);
        tile.SetSelecting(_selecting, _selection.Contains(file.Path));
        return tile;
    }

    /// Adaptive columns, 104 to 132 wide, like the Mac app's grid.
    private void LayoutTiles()
    {
        var width = _scroll.ActualWidth;
        if (width <= 0) return;
        var columns = Math.Max(1, (int)((width + Spacing) / (TileMin + Spacing)));
        _tileWidth = Math.Floor(Math.Min(TileMax, (width - (columns - 1) * Spacing) / columns));
        // Each tile carries the gap on its right; the last column's gap hangs past the edge.
        _grid.Width = columns * (_tileWidth + Spacing);
        _grid.HorizontalAlignment = HorizontalAlignment.Left;
        foreach (var tile in _grid.Children.OfType<CaptureTile>()) tile.SetWidth(_tileWidth);
    }

    /// Thumbnails are made only for tiles on screen (and a screenful either side), like a lazy grid.
    private void LoadVisibleThumbnails()
    {
        if (!_scroll.IsLoaded) return;
        var reach = _scroll.ViewportHeight;
        foreach (var tile in _grid.Children.OfType<CaptureTile>())
        {
            if (!tile.IsDescendantOf(_scroll)) continue;
            var top = tile.TransformToAncestor(_scroll).Transform(new Point()).Y;
            if (top < _scroll.ViewportHeight + reach && top + tile.ActualHeight > -reach) tile.LoadThumbnail();
        }
    }

    // MARK: - Toolbar

    private UIElement Toolbar()
    {
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(_toolbarActions, Dock.Right);
        bar.Children.Add(_toolbarActions);

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var capsule = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(3), Child = _segments };
        capsule.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
        left.Children.Add(capsule);
        var order = Ui.Text("Newest first", 11, ThemeResources.SecondaryText);
        order.Margin = new Thickness(8, 0, 0, 0);
        left.Children.Add(order);
        bar.Children.Add(left);
        return bar;
    }

    private void RefreshToolbar(IReadOnlyCollection<CaptureFile> visible)
    {
        _segments.Children.Clear();
        foreach (var filter in CaptureFilterInfo.All)
        {
            var count = _model.Files.Count(f => filter.Matches(f));
            _segments.Children.Add(Segment(filter, count, filter == _filter));
        }

        _toolbarActions.Children.Clear();
        if (visible.Count > 0)
        {
            var select = Ui.Pill(_selecting ? "Done" : "Select", _selecting ? "\uE73E" : "\uE762", () =>
            {
                _selecting = !_selecting;
                if (!_selecting) _selection.Clear();
                Rebuild();
            });
            select.Margin = new Thickness(0, 0, 8, 0);
            _toolbarActions.Children.Add(select);
        }
        var folder = Ui.IconButton("\uE8B7", 28, () => FileMeta.OpenFolder(CapturesModel.FolderFor(_filter)), tooltip: "Open folder");
        folder.Margin = new Thickness(0, 0, 8, 0);
        _toolbarActions.Children.Add(folder);
        _toolbarActions.Children.Add(Ui.Pill("Screenshot", "\uE722", _model.TakeScreenshot, prominent: true));
    }

    private UIElement Segment(CaptureFilter filter, int count, bool selected)
    {
        var foreground = selected ? ThemeResources.Background : ThemeResources.Text;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var title = Ui.Text(filter.Title(), 12, foreground, FontWeights.SemiBold);
        if (!selected) title.Opacity = 0.8;
        row.Children.Add(title);
        var badge = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(5, 0, 5, 1),
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Text(count.ToString(), 10, foreground, FontWeights.Bold).Tabular(),
        };
        if (selected && Application.Current.Resources[ThemeResources.Background] is SolidColorBrush background)
            badge.Background = background.Color.WithOpacity(0.25).Brush();
        else
            badge.SetResourceReference(Border.BackgroundProperty, ThemeResources.Fill);
        row.Children.Add(badge);

        var pill = new Border { CornerRadius = new CornerRadius(13), Padding = new Thickness(10, 5, 10, 5), Child = row };
        if (selected) pill.SetResourceReference(Border.BackgroundProperty, ThemeResources.Accent);
        else pill.Background = Brushes.Transparent;
        return Ui.Bare(pill, () =>
        {
            if (_filter == filter) return;
            _filter = filter;
            _scroll.ScrollToTop();
            Rebuild();
        });
    }

    private static UIElement EmptyStateContent(bool recordings, bool loaded)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var badge = new Grid { Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Center };
        var circle = new Ellipse { Opacity = 0.12 };
        circle.SetResourceReference(Shape.FillProperty, ThemeResources.Accent);
        badge.Children.Add(circle);
        var icon = Ui.Glyph(recordings ? "\uE714" : "\uE722", 28, ThemeResources.Accent);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        badge.Children.Add(icon);
        stack.Children.Add(badge);
        var title = Ui.Text(loaded ? "No screenshots yet" : "Looking for captures…", 14, weight: FontWeights.SemiBold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(title);
        var hint = Ui.Text("Press Win+PrtScn or use the Screenshot button — new captures land here.", 12, ThemeResources.SecondaryText);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.TextAlignment = TextAlignment.Center;
        hint.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(hint);
        return stack;
    }

    private UIElement EmptyState() => EmptyStateContent(_filter == CaptureFilter.Recordings, _model.Loaded);

    // MARK: - Selection, copy and toast

    private void Toggle(CaptureFile file)
    {
        if (!_selection.Remove(file.Path)) _selection.Add(file.Path);
        if (_tiles.TryGetValue(file, out var tile)) tile.SetSelecting(true, _selection.Contains(file.Path));
        RefreshBottomBar();
    }

    private List<CaptureFile> Selected() => Visible().Where(f => _selection.Contains(f.Path)).ToList();

    private void Copy(IReadOnlyCollection<CaptureFile> files)
    {
        if (!_model.Copy(files)) return;
        Show(files.Count == 1 ? "Copied" : $"Copied {files.Count}");
    }

    private void Show(string message)
    {
        _toast = message;
        _toastTimer.Stop();
        _toastTimer.Start();
        RefreshBottomBar();
    }

    // MARK: - Bottom bar (selection, undo, toast)

    private void RefreshBottomBar()
    {
        var kind = _selecting ? "select" : _model.LastTrashed.Count > 0 ? "undo" : _toast != null ? "toast" : "";
        _bottomBar.Content = kind switch
        {
            "select" => SelectionBar(),
            "undo" => UndoBar(),
            "toast" => ToastBar(_toast!),
            _ => null,
        };
        _grid.Margin = new Thickness(0, 4, 0, kind.Length > 0 ? 48 : 4);
        if (kind == _bottomKind) return;
        _bottomKind = kind;
        if (kind.Length == 0) return;
        // Slides up from the bottom edge.
        var shift = new TranslateTransform(0, 30);
        _bottomBar.RenderTransform = shift;
        _bottomBar.Opacity = 0;
        shift.Animate(TranslateTransform.YProperty, 0, Motion.ExpoOut(0.35));
        _bottomBar.Animate(OpacityProperty, 1, Motion.ExpoOut(0.35));
    }

    private UIElement SelectionBar()
    {
        var visible = Visible();
        var count = _selection.Count;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Text(count == 0 ? "Click captures to select" : $"{count} selected", 12, weight: FontWeights.SemiBold));
        var all = Ui.Bare(Ui.Text(count == visible.Count ? "Deselect all" : "Select all", 12, ThemeResources.Accent), () =>
        {
            if (_selection.Count == visible.Count) _selection.Clear();
            else foreach (var file in visible) _selection.Add(file.Path);
            Rebuild();
        });
        all.Margin = new Thickness(10, 0, 24, 0);
        row.Children.Add(all);
        var copy = Ui.Pill("Copy", "\uE8C8", () => Copy(Selected()));
        copy.IsEnabled = count > 0;
        copy.Opacity = count > 0 ? 1 : 0.4;
        row.Children.Add(copy);
        var delete = DeleteButton(count > 0, () =>
        {
            var targets = Selected();
            _selection.Clear();
            _selecting = false;
            _model.Trash(targets);
        });
        delete.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(delete);
        return BarCapsule(row);
    }

    private UIElement UndoBar()
    {
        var count = _model.LastTrashed.Count;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Glyph("\uE74D", 12, ThemeResources.SecondaryText));
        var text = Ui.Text(count == 1 ? "Moved to Recycle Bin" : $"Moved {count} to Recycle Bin", 12, weight: FontWeights.SemiBold);
        text.Margin = new Thickness(10, 0, 10, 0);
        row.Children.Add(text);
        row.Children.Add(Ui.Bare(Ui.Text("Undo", 12, ThemeResources.Accent, FontWeights.Bold), _model.UndoTrash));
        return BarCapsule(row);
    }

    private static UIElement ToastBar(string message)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Glyph("\uE73E", 12, ThemeResources.Accent));
        var text = Ui.Text(message, 12, weight: FontWeights.SemiBold);
        text.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(text);
        return BarCapsule(row);
    }

    private static Border BarCapsule(UIElement content)
    {
        var capsule = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14, 8, 14, 8),
            BorderThickness = new Thickness(0.8),
            Child = content,
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.4, BlurRadius = 20, ShadowDepth = 4, Direction = 270 },
        };
        capsule.SetResourceReference(Border.BackgroundProperty, ThemeResources.Surface);
        capsule.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);
        return capsule;
    }

    /// The red "Delete" capsule.
    internal static Button DeleteButton(bool enabled, Action onClick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "\uE74D",
            FontSize = 11,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.FindResource("Purrch.Glyph"),
        });
        row.Children.Add(new TextBlock
        {
            Text = "Delete",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var capsule = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 6, 12, 6),
            Background = Red.WithOpacity(enabled ? 0.9 : 0.35).Brush(),
            Child = row,
        };
        var button = Ui.Bare(capsule, onClick);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.IsEnabled = enabled;
        return button;
    }

    // MARK: - Preview

    private void Preview(CaptureFile? file)
    {
        var wasOpen = _previewing != null;
        _previewing = file;
        RefreshPreview();
        if (wasOpen || file == null || _previewHost.Children.Count == 0) return;
        var view = (FrameworkElement)_previewHost.Children[0];
        var scale = new ScaleTransform(0.96, 0.96);
        view.RenderTransformOrigin = new Point(0.5, 0.5);
        view.RenderTransform = scale;
        view.Opacity = 0;
        scale.Animate(ScaleTransform.ScaleXProperty, 1, Motion.ExpoOut(0.4));
        scale.Animate(ScaleTransform.ScaleYProperty, 1, Motion.ExpoOut(0.4));
        view.Animate(OpacityProperty, 1, Motion.ExpoOut(0.4));
    }

    private void RefreshPreview()
    {
        if (_previewing is not { } file)
        {
            _previewHost.Children.Clear();
            return;
        }
        if (_previewHost.Children.Count == 1 && _previewHost.Children[0] is CapturePreview shown && shown.File == file
            && shown.Count == Visible().Count)
            return;
        _previewHost.Children.Clear();
        var files = Visible();
        _previewHost.Children.Add(new CapturePreview(file, files,
            go: Preview,
            copy: () => Copy(new[] { file }),
            trash: () =>
            {
                var index = files.IndexOf(file);
                var next = index + 1 < files.Count ? files[index + 1] : index > 0 ? files[index - 1] : null;
                _previewing = next;
                _model.Trash(new[] { file });
            }));
    }
}

/// One capture in the grid. Hover shows Copy, Show in File Explorer and Delete; click opens the
/// preview (or selects); drag it into another app.
internal sealed class CaptureTile : StackPanel
{
    private const double DragThreshold = 6;
    private readonly CaptureFile _file;
    private readonly Action _onOpen;
    private readonly Grid _thumb = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Border _dim;
    private readonly Border _frame;
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal };
    private readonly Grid _check = new() { Width = 17, Height = 17 };
    private readonly TextBlock _age;
    private readonly CancellationTokenSource _thumbnail = new();
    private readonly Border _picture;
    private bool _hovering, _selecting, _selected, _requested;
    private Point? _pressPoint;

    public CaptureTile(CaptureFile file, Action onOpen, Action onCopy, Action onReveal, Action onTrash)
    {
        _file = file;
        _onOpen = onOpen;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        ToolTip = System.IO.Path.GetFileName(file.Path);
        Margin = new Thickness(0, 0, 8, 10);

        var placeholder = new Border { CornerRadius = new CornerRadius(9) };
        placeholder.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
        _thumb.Children.Add(placeholder);
        var picture = new Border { CornerRadius = new CornerRadius(9) };
        _thumb.Children.Add(picture);
        _picture = picture;

        if (file.IsVideo)
        {
            var play = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = Color.FromArgb(140, 0, 0, 0).Brush(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = WhiteGlyph("\uE768", 10),
            };
            _thumb.Children.Add(play);
        }

        // Dim from the top on hover, so the buttons read over any picture.
        _dim = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = new LinearGradientBrush(Color.FromArgb(140, 0, 0, 0), Colors.Transparent, new Point(0.5, 0), new Point(0.5, 0.5)),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        _thumb.Children.Add(_dim);

        _frame = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(0.8), IsHitTestVisible = false };
        _thumb.Children.Add(_frame);

        if (file.IsVideo)
        {
            var tag = FileMeta.Tag(file.Extension, Colors.White);
            tag.HorizontalAlignment = HorizontalAlignment.Right;
            tag.VerticalAlignment = VerticalAlignment.Bottom;
            tag.Margin = new Thickness(4);
            _thumb.Children.Add(tag);
        }

        _buttons.HorizontalAlignment = HorizontalAlignment.Right;
        _buttons.VerticalAlignment = VerticalAlignment.Top;
        _buttons.Margin = new Thickness(4);
        _buttons.Visibility = Visibility.Hidden;
        _buttons.Children.Add(TileButton("\uE8C8", "Copy", Colors.White, onCopy));
        _buttons.Children.Add(TileButton("\uE8B7", "Show in File Explorer", Colors.White, onReveal));
        _buttons.Children.Add(TileButton("\uE74D", "Move to Recycle Bin", Color.FromRgb(0xFF, 0x45, 0x3A), onTrash));
        _thumb.Children.Add(_buttons);

        _check.HorizontalAlignment = HorizontalAlignment.Left;
        _check.VerticalAlignment = VerticalAlignment.Top;
        _check.Margin = new Thickness(7);
        _check.Visibility = Visibility.Collapsed;
        _check.IsHitTestVisible = false;
        _thumb.Children.Add(_check);

        _thumb.RenderTransformOrigin = new Point(0.5, 0.5);
        _thumb.RenderTransform = _scale;
        Children.Add(_thumb);

        // Small tiles: when it was taken says more than the long file name, which stays in the tooltip.
        _age = Ui.Text("", 10, ThemeResources.SecondaryText, FontWeights.Medium);
        _age.TextWrapping = TextWrapping.NoWrap;
        _age.TextTrimming = TextTrimming.CharacterEllipsis;
        _age.Margin = new Thickness(2, 4, 2, 0);
        Children.Add(_age);
        RefreshAge();
        RefreshLook(animated: false);

        MouseEnter += (_, _) => { _hovering = true; RefreshLook(animated: true); };
        MouseLeave += (_, _) => { _hovering = false; _pressPoint = null; RefreshLook(animated: true); };
        MouseLeftButtonDown += (_, e) => _pressPoint = e.GetPosition(this);
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += (_, _) =>
        {
            if (_pressPoint != null) _onOpen();
            _pressPoint = null;
        };
    }

    public void SetWidth(double width)
    {
        Width = width;
        _thumb.Height = Math.Round(width * 10 / 16);
    }

    public void SetSelecting(bool selecting, bool selected)
    {
        if (_selecting == selecting && _selected == selected) return;
        _selecting = selecting;
        _selected = selected;
        RefreshLook(animated: true);
    }

    public void RefreshAge()
    {
        var age = Format.Relative(_file.Date);
        _age.Text = age == "now" ? "just now" : $"{age} ago";
    }

    /// Asks for the thumbnail the first time the tile scrolls into view.
    public void LoadThumbnail()
    {
        if (_requested) return;
        _requested = true;
        Thumbnails.Request(_file.Path, 264, source =>
        {
            if (source == null) return;
            var brush = new ImageBrush(source) { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            _picture.Background = brush;
        }, _thumbnail.Token);
    }

    /// Stops a thumbnail that hasn't been made yet; the tile is no longer needed.
    public void Release() => _thumbnail.Cancel();

    private void RefreshLook(bool animated)
    {
        var scale = _selected ? 0.95 : _hovering ? 1.03 : 1;
        var curve = Motion.ExpoOut(0.25);
        if (animated)
        {
            _scale.Animate(ScaleTransform.ScaleXProperty, scale, curve);
            _scale.Animate(ScaleTransform.ScaleYProperty, scale, curve);
            _dim.Animate(OpacityProperty, _hovering ? 1 : 0, curve);
        }
        else
        {
            _scale.Set(ScaleTransform.ScaleXProperty, scale);
            _scale.Set(ScaleTransform.ScaleYProperty, scale);
            _dim.Set(OpacityProperty, _hovering ? 1 : 0);
        }
        // Only the tile under the pointer casts a shadow; hundreds of resting shadows make scrolling stutter.
        _thumb.Effect = _hovering
            ? new DropShadowEffect { Color = Colors.Black, Direction = 270, ShadowDepth = 6, BlurRadius = 20, Opacity = 0.45 }
            : null;

        _frame.BorderThickness = new Thickness(_selected ? 2.5 : 0.8);
        if (_selected)
        {
            _frame.Opacity = 1;
            _frame.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Accent);
        }
        else if (_hovering)
        {
            _frame.Opacity = 0.35;
            _frame.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Text);
        }
        else
        {
            _frame.Opacity = 0.5;
            _frame.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);
        }

        _buttons.Visibility = _hovering && !_selecting ? Visibility.Visible : Visibility.Hidden;
        _check.Visibility = _selecting ? Visibility.Visible : Visibility.Collapsed;
        _check.Children.Clear();
        if (!_selecting) return;
        var ring = new Ellipse { StrokeThickness = 1.6, Fill = Color.FromArgb(90, 0, 0, 0).Brush() };
        if (_selected)
        {
            ring.SetResourceReference(Shape.FillProperty, ThemeResources.Accent);
            ring.SetResourceReference(Shape.StrokeProperty, ThemeResources.Accent);
        }
        else
        {
            ring.Stroke = Brushes.White;
        }
        _check.Children.Add(ring);
        if (_selected)
        {
            var tick = Ui.Glyph("\uE73E", 9, ThemeResources.Background);
            tick.HorizontalAlignment = HorizontalAlignment.Center;
            tick.FontWeight = FontWeights.Bold;
            _check.Children.Add(tick);
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - start.X) < DragThreshold && Math.Abs(p.Y - start.Y) < DragThreshold) return;
        _pressPoint = null;
        if (!System.IO.File.Exists(_file.Path)) return;
        DragDrop.DoDragDrop(this, FileMeta.DragData(new[] { _file.Path }), DragDropEffects.Copy);
    }

    private static Button TileButton(string glyph, string tooltip, Color tint, Action onClick)
    {
        var circle = new Border
        {
            Width = 21,
            Height = 21,
            Margin = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(10.5),
            Background = Color.FromArgb(150, 20, 20, 20).Brush(),
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = tint.Brush(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.FindResource("Purrch.Glyph"),
            },
        };
        return Ui.Bare(circle, onClick, tooltip);
    }

    internal static TextBlock WhiteGlyph(string glyph, double size) => new()
    {
        Text = glyph,
        FontSize = size,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Style = (Style)Application.Current.FindResource("Purrch.Glyph"),
    };
}

/// Large in-panel viewer with previous / next, copy, open and delete.
internal sealed class CapturePreview : Grid
{
    private readonly IReadOnlyList<CaptureFile> _files;

    public CaptureFile File { get; }
    public int Count => _files.Count;

    public CapturePreview(CaptureFile file, IReadOnlyList<CaptureFile> files, Action<CaptureFile?> go, Action copy, Action trash)
    {
        File = file;
        _files = files;
        var index = Math.Max(0, files.ToList().IndexOf(file));

        // Clicking the backdrop (anywhere outside the picture and the buttons) closes the preview.
        var backdrop = new Border { CornerRadius = new CornerRadius(16) };
        if (Application.Current.Resources[ThemeResources.Background] is SolidColorBrush background)
            backdrop.Background = background.Color.WithOpacity(0.96).Brush();
        backdrop.MouseLeftButtonUp += (_, e) => { go(null); e.Handled = true; };
        Children.Add(backdrop);

        var row = new Grid { Margin = new Thickness(12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var previous = NavButton("\uE76B", "Previous", index > 0, () => go(files[index - 1]));
        previous.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(previous);

        var stage = Stage(file);
        SetColumn(stage, 1);
        row.Children.Add(stage);

        var details = Details(file, index, files.Count, copy, trash, () => go(null));
        details.Margin = new Thickness(12, 0, 0, 0);
        SetColumn(details, 2);
        row.Children.Add(details);

        var next = NavButton("\uE76C", "Next", index + 1 < files.Count, () => go(files[index + 1]));
        next.Margin = new Thickness(12, 0, 0, 0);
        SetColumn(next, 3);
        row.Children.Add(next);
        Children.Add(row);
    }

    /// The picture itself, loaded full size in the background; a video shows its thumbnail and a play button.
    private static UIElement Stage(CaptureFile file)
    {
        var stage = new Grid { ClipToBounds = true, Background = Color.FromArgb(77, 0, 0, 0).Brush() };
        var image = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        stage.Children.Add(image);

        void ShowThumbnail() => Thumbnails.Request(file.Path, 900, source => { if (source != null && image.Source == null) image.Source = source; });
        if (file.IsVideo)
        {
            ShowThumbnail();
            var play = new Border
            {
                Width = 52,
                Height = 52,
                CornerRadius = new CornerRadius(26),
                Background = Brushes.White,
                Child = new TextBlock
                {
                    Text = "\uE768",
                    FontSize = 20,
                    Foreground = Brushes.Black,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Style = (Style)Application.Current.FindResource("Purrch.Glyph"),
                },
            };
            var button = Ui.Bare(play, () => FileMeta.Open(file.Path), "Play");
            button.HorizontalAlignment = HorizontalAlignment.Center;
            button.VerticalAlignment = VerticalAlignment.Center;
            stage.Children.Add(button);
        }
        else
        {
            // Full picture off the UI thread; the thumbnail stands in if it can't be read (yet).
            var path = file.Path;
            Task.Run(() => FileMeta.LoadImage(path, 1600)).ContinueWith(t =>
            {
                if (t.Result is BitmapSource full)
                {
                    image.Source = full;
                    image.Opacity = 0;
                    image.Animate(OpacityProperty, 1, Motion.ExpoOut(0.3));
                }
                else
                {
                    ShowThumbnail();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        var frame = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(0.8), Child = stage };
        frame.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);
        // Round the picture's corners to match the frame.
        stage.SizeChanged += (_, _) => stage.Clip = new RectangleGeometry(new Rect(stage.RenderSize), 11.5, 11.5);
        return frame;
    }

    private static FrameworkElement Details(CaptureFile file, int index, int count, Action copy, Action trash, Action close)
    {
        var panel = new DockPanel();
        var top = new StackPanel();
        var closeButton = Ui.IconButton("\uE711", 26, close, tooltip: "Close");
        closeButton.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(closeButton);
        var name = Ui.Text(file.Name, 13, weight: FontWeights.Bold);
        name.MaxHeight = 54;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.Margin = new Thickness(0, 10, 0, 0);
        top.Children.Add(name);
        var when = Ui.Text($"{file.Date:t} · {Format.Bytes(file.Size)}", 11, ThemeResources.SecondaryText);
        when.Margin = new Thickness(0, 10, 0, 0);
        top.Children.Add(when);
        var position = Ui.Text($"{index + 1} of {count}", 11, ThemeResources.SecondaryText);
        position.Margin = new Thickness(0, 10, 0, 0);
        top.Children.Add(position);
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);

        var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        void Add(Button button)
        {
            button.Margin = new Thickness(0, actions.Children.Count == 0 ? 0 : 8, 0, 0);
            actions.Children.Add(button);
        }
        Add(Ui.Pill("Copy", "\uE8C8", copy));
        Add(Ui.Pill("Open", "\uE8A7", () => FileMeta.Open(file.Path)));
        Add(Ui.Pill("Explorer", "\uE8B7", () => FileMeta.Reveal(file.Path)));
        Add(CapturesModule.DeleteButton(true, trash));
        panel.Children.Add(actions);
        return panel;
    }

    private static Button NavButton(string glyph, string tooltip, bool enabled, Action onClick)
    {
        var button = Ui.IconButton(glyph, 30, onClick, tooltip: tooltip);
        button.VerticalAlignment = VerticalAlignment.Center;
        button.IsEnabled = enabled;
        button.Opacity = enabled ? 1 : 0.25;
        return button;
    }
}
