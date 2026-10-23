using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using Purrch.Core;
using Purrch.DesignSystem;

namespace Purrch.Features.Shelf;

/// Files parked in the pill: drop them in, drag them back out whenever you need them.
public sealed class ShelfModule : DockPanel
{
    private readonly ShelfStore _store = ShelfStore.Shared;
    private readonly TextBlock _count = Ui.Text("", 12, ThemeResources.SecondaryText, FontWeights.Medium);
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };
    private readonly Grid _middle = new();
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 4, 2, 4) };
    private readonly ScrollViewer _scroll;
    private readonly Border _targetOverlay;
    private readonly DropZone _dropZone = new();
    // Sizes are worked out once per file; folders can take a moment.
    private readonly Dictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _shown = new();
    private bool _targeted;

    public ShelfModule()
    {
        // A transparent background makes empty space a drop target too.
        Background = Brushes.Transparent;
        AllowDrop = true;

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_actions, Dock.Right);
        header.Children.Add(_actions);
        _count.TextWrapping = TextWrapping.NoWrap;
        header.Children.Add(_count);
        SetDock(header, Dock.Top);
        Children.Add(header);

        var footer = Ui.Text("Files stay where they are. Drag them back out whenever you need them.", 11, ThemeResources.SecondaryText);
        footer.Opacity = 0.8;
        footer.HorizontalAlignment = HorizontalAlignment.Center;
        footer.Margin = new Thickness(0, 8, 0, 0);
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _row,
        };
        // The wheel scrolls the row sideways.
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        };
        _targetOverlay = TargetOverlay();
        Children.Add(_middle);

        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;

        foreach (var entry in _store.Entries) _shown.Add(entry.Id);
        Rebuild();
        Loaded += (_, _) =>
        {
            _store.Changed += Rebuild;
            _store.Prune();
            Rebuild();
        };
        Unloaded += (_, _) => _store.Changed -= Rebuild;
    }

    private void Rebuild()
    {
        var entries = _store.Entries;
        _count.Text = entries.Count == 0 ? "Drop files to park them here"
            : entries.Count == 1 ? "1 item" : $"{entries.Count} items";

        _actions.Children.Clear();
        _actions.Children.Add(Ui.Pill("Add", "\uE710", ChooseFiles));
        if (entries.Count > 0)
        {
            if (entries.Count > 1)
            {
                var share = Ui.IconButton("\uE72D", 28, () => FileMeta.Share(entries.Select(e => e.Path).ToList(), this), tooltip: "Share all");
                share.Margin = new Thickness(8, 0, 0, 0);
                _actions.Children.Add(share);
            }
            var clear = Ui.Pill("Clear", "\uE74D", _store.Clear);
            clear.Margin = new Thickness(8, 0, 0, 0);
            _actions.Children.Add(clear);
        }

        _middle.Children.Clear();
        if (entries.Count == 0)
        {
            _dropZone.SetTargeted(_targeted);
            _middle.Children.Add(_dropZone);
            return;
        }

        _row.Children.Clear();
        foreach (var entry in entries)
        {
            var tile = new ShelfTile(entry, SizeOf(entry));
            tile.Margin = new Thickness(0, 0, 12, 0);
            if (_shown.Add(entry.Id)) tile.PopIn();
            _row.Children.Add(tile);
        }
        _middle.Children.Add(_scroll);
        _targetOverlay.Visibility = _targeted ? Visibility.Visible : Visibility.Collapsed;
        _middle.Children.Add(_targetOverlay);
    }

    /// Known size, or null while it is being worked out in the background.
    private long? SizeOf(ShelfEntry entry)
    {
        // -1 marks a size still being worked out.
        if (_sizes.TryGetValue(entry.Path, out var size)) return size >= 0 ? size : null;
        var path = entry.Path;
        Task.Run(() => FileMeta.Size(path)).ContinueWith(t =>
        {
            _sizes[path] = t.Result;
            if (IsLoaded) Rebuild();
        }, TaskScheduler.FromCurrentSynchronizationContext());
        _sizes[path] = -1;
        return null;
    }

    private void ChooseFiles()
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Add to Shelf" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _store.Add(dialog.FileNames);
    }

    // MARK: - Dropping in

    private void SetTargeted(bool targeted)
    {
        if (_targeted == targeted) return;
        _targeted = targeted;
        _dropZone.SetTargeted(targeted);
        _targetOverlay.Visibility = targeted ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        SetTargeted(hasFiles);
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // Leave also fires when moving between children; only clear once the pointer is really outside.
        var p = e.GetPosition(this);
        if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight) SetTargeted(false);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        SetTargeted(false);
        _store.Add(FileMeta.DroppedPaths(e.Data));
        e.Handled = true;
    }

    /// Dashed accent outline over the tiles while files hover above them.
    private static Border TargetOverlay()
    {
        var grid = new Grid { IsHitTestVisible = false };
        var fill = new Rectangle { RadiusX = 16, RadiusY = 16, Opacity = 0.08 };
        fill.SetResourceReference(Shape.FillProperty, ThemeResources.Accent);
        grid.Children.Add(fill);
        var outline = new Rectangle { RadiusX = 16, RadiusY = 16, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3.5, 2.5 } };
        outline.SetResourceReference(Shape.StrokeProperty, ThemeResources.Accent);
        grid.Children.Add(outline);
        return new Border { Child = grid, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    }
}

/// The empty shelf: a dashed drop area that lights up while files hover above it.
internal sealed class DropZone : Grid
{
    private readonly Rectangle _fill = new() { RadiusX = 16, RadiusY = 16 };
    private readonly Rectangle _outline = new() { RadiusX = 16, RadiusY = 16 };
    private readonly TextBlock _icon = Ui.Glyph("\uE896", 34, ThemeResources.Accent);
    private readonly TextBlock _title = Ui.Text("Drop files here", 15, weight: FontWeights.SemiBold);

    public DropZone()
    {
        Children.Add(_fill);
        Children.Add(_outline);

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _icon.HorizontalAlignment = HorizontalAlignment.Center;
        _icon.FontWeight = FontWeights.Light;
        if (AppSettings.Shared.Theme.Glow && Application.Current.Resources[ThemeResources.Accent] is SolidColorBrush accent)
            _icon.Effect = Interface.ExpandedView.Glow(accent.Color, 6);
        stack.Children.Add(_icon);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(_title);
        var hint = Ui.Text("Drag anything onto the pill to keep it handy", 12, ThemeResources.SecondaryText);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(hint);
        Children.Add(stack);
        SetTargeted(false);
    }

    public void SetTargeted(bool targeted)
    {
        _title.Text = targeted ? "Release to add" : "Drop files here";
        _fill.Opacity = targeted ? 0.08 : 1;
        _fill.SetResourceReference(Shape.FillProperty, targeted ? ThemeResources.Accent : ThemeResources.Surface);
        _outline.SetResourceReference(Shape.StrokeProperty, targeted ? ThemeResources.Accent : ThemeResources.Border);
        _outline.StrokeThickness = targeted ? 2 : 1;
        // Dashes are measured in stroke widths: 7 on, 5 off at either thickness.
        _outline.StrokeDashArray = targeted ? new DoubleCollection { 3.5, 2.5 } : new DoubleCollection { 7, 5 };
        var pop = new ScaleTransform(1, 1);
        _icon.RenderTransformOrigin = new Point(0.5, 0.5);
        _icon.RenderTransform = pop;
        if (!targeted) return;
        pop.ScaleX = pop.ScaleY = 0.85;
        pop.Animate(ScaleTransform.ScaleXProperty, 1, Motion.Pop);
        pop.Animate(ScaleTransform.ScaleYProperty, 1, Motion.Pop);
    }
}

/// One parked file: thumbnail with its type, name and size. Double-click opens it, drag it out to use it.
internal sealed class ShelfTile : StackPanel
{
    private const double TileWidth = 150;
    private const double DragThreshold = 6;
    private readonly ShelfEntry _entry;
    private readonly Border _card;
    private readonly Border _frame;
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal };
    private readonly CancellationTokenSource _thumbnail = new();
    private Point? _pressPoint;

    public ShelfTile(ShelfEntry entry, long? size)
    {
        _entry = entry;
        var path = entry.Path;
        Width = TileWidth;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        ToolTip = path;

        var label = FileMeta.Label(path);
        var art = new Grid { Width = TileWidth, Height = 110 };
        _card = new Border { CornerRadius = new CornerRadius(14) };
        _card.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
        art.Children.Add(_card);

        var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(12) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var fallback = Ui.Glyph(Directory.Exists(path) ? "\uE8B7" : "\uE8A5", 34, ThemeResources.SecondaryText);
        fallback.HorizontalAlignment = HorizontalAlignment.Center;
        art.Children.Add(fallback);
        art.Children.Add(image);
        Thumbnails.Request(path, 220, source =>
        {
            image.Source = source;
            if (source != null) fallback.Visibility = Visibility.Collapsed;
        }, _thumbnail.Token);

        var tag = FileMeta.Tag(label, FileMeta.ColorFor(label));
        tag.HorizontalAlignment = HorizontalAlignment.Left;
        tag.VerticalAlignment = VerticalAlignment.Bottom;
        tag.Margin = new Thickness(8);
        art.Children.Add(tag);

        _frame = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(0.8), IsHitTestVisible = false, Opacity = 0.6 };
        _frame.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);
        art.Children.Add(_frame);

        _buttons.HorizontalAlignment = HorizontalAlignment.Right;
        _buttons.VerticalAlignment = VerticalAlignment.Top;
        _buttons.Margin = new Thickness(6);
        _buttons.Visibility = Visibility.Hidden;
        _buttons.Children.Add(MiniButton("\uE72D", "Share", () => FileMeta.Share(new[] { path }, this)));
        var remove = MiniButton("\uE711", "Remove from shelf", () => ShelfStore.Shared.Remove(entry));
        remove.Margin = new Thickness(4, 0, 0, 0);
        _buttons.Children.Add(remove);
        art.Children.Add(_buttons);
        Children.Add(art);

        var name = Ui.Text(MiddleTrim(System.IO.Path.GetFileName(path), 12, TileWidth), 12, weight: FontWeights.SemiBold);
        name.TextWrapping = TextWrapping.NoWrap;
        name.Margin = new Thickness(0, 5, 0, 0);
        Children.Add(name);

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        meta.Children.Add(Ui.Glyph("\uE8A5", 10, ThemeResources.SecondaryText));
        var sizeText = Ui.Text(size is { } bytes ? Format.Bytes(bytes) : "…", 11, ThemeResources.SecondaryText);
        sizeText.Margin = new Thickness(4, 0, 0, 0);
        meta.Children.Add(sizeText);
        Children.Add(meta);

        ContextMenu = Menu(entry);
        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => { SetHover(false); _pressPoint = null; };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                _pressPoint = null;
                FileMeta.Open(path);
                return;
            }
            _pressPoint = e.GetPosition(this);
        };
        MouseLeftButtonUp += (_, _) => _pressPoint = null;
        MouseMove += OnMouseMove;
        Unloaded += (_, _) => _thumbnail.Cancel();
    }

    /// New tiles grow in from slightly smaller.
    public void PopIn()
    {
        var scale = new ScaleTransform(0.9, 0.9);
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = scale;
        Opacity = 0;
        scale.Animate(ScaleTransform.ScaleXProperty, 1, Motion.Pop);
        scale.Animate(ScaleTransform.ScaleYProperty, 1, Motion.Pop);
        this.Animate(OpacityProperty, 1, Motion.ExpoOut(0.3));
    }

    private void SetHover(bool hovering)
    {
        _buttons.Visibility = hovering ? Visibility.Visible : Visibility.Hidden;
        if (hovering && Application.Current.Resources[ThemeResources.Text] is SolidColorBrush text)
            _card.Background = text.Color.WithOpacity(0.14).Brush();
        else
            _card.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
        _frame.SetResourceReference(Border.BorderBrushProperty, hovering ? ThemeResources.Accent : ThemeResources.Border);
        _frame.Opacity = hovering ? 0.7 : 0.6;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - start.X) < DragThreshold && Math.Abs(p.Y - start.Y) < DragThreshold) return;
        _pressPoint = null;
        if (!File.Exists(_entry.Path) && !Directory.Exists(_entry.Path))
        {
            ShelfStore.Shared.Prune();
            return;
        }
        DragDrop.DoDragDrop(this, FileMeta.DragData(new[] { _entry.Path }), DragDropEffects.Copy);
    }

    private ContextMenu Menu(ShelfEntry entry)
    {
        var menu = new ContextMenu();
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Open", () => FileMeta.Open(entry.Path));
        Add("Show in File Explorer", () => FileMeta.Reveal(entry.Path));
        Add("Copy", () => FileMeta.CopyFiles(new[] { entry.Path }));
        Add("Share", () => FileMeta.Share(new[] { entry.Path }, this));
        menu.Items.Add(new Separator());
        Add("Remove from Shelf", () => ShelfStore.Shared.Remove(entry));
        return menu;
    }

    private static Button MiniButton(string glyph, string tooltip, Action onClick)
    {
        var circle = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(0.6),
            Child = Ui.Glyph(glyph, 8, ThemeResources.Text),
        };
        ((TextBlock)circle.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)circle.Child).FontWeight = FontWeights.Bold;
        if (Application.Current.Resources[ThemeResources.Background] is SolidColorBrush background)
            circle.Background = background.Color.WithOpacity(0.85).Brush();
        circle.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);
        return Ui.Bare(circle, onClick, tooltip);
    }

    /// Cuts the middle out of a long name ("Screenshot 20…12.png"), so the extension stays visible.
    private static string MiddleTrim(string text, double size, double width)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double Measure(string s) => new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, size, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
        if (Measure(text) <= width) return text;
        var keep = text.Length - 1;
        while (keep > 2)
        {
            var head = (keep + 1) / 2;
            var candidate = text[..head] + "…" + text[^(keep - head)..];
            if (Measure(candidate) <= width) return candidate;
            keep--;
        }
        return "…";
    }
}
