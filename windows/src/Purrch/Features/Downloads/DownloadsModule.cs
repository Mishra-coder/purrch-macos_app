using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch.Features.Downloads;

/// Live progress for downloads in flight, files that just finished, and the 20 newest files in Downloads.
/// "Select" picks several to move to the Recycle Bin, with Undo.
public sealed class DownloadsModule : Grid
{
    private readonly DownloadsService _service = DownloadsService.Shared;
    private readonly ContentControl _deniedHost = new();
    private readonly Grid _main = new();
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _content = new();
    private readonly StackPanel _activeSection = new();
    private readonly StackPanel _activeList = new();
    private readonly StackPanel _finished = new();
    private readonly ContentControl _bar = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Dictionary<string, ActiveRow> _activeRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DownloadRow> _rows = new();
    private readonly HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _selecting;
    // What the rows showed last time, so check circles and actions can animate when it changes.
    private bool _shownSelecting;
    private readonly HashSet<string> _shownSelection = new(StringComparer.OrdinalIgnoreCase);
    private string _listKey = "";
    private string _barKey = "";
    private bool _menuOpen, _rebuildAfterMenu, _rowsLeaving;

    public DownloadsModule()
    {
        _activeSection.Children.Add(Section("Downloading"));
        _activeSection.Children.Add(_activeList);
        _content.Children.Add(_activeSection);
        _content.Children.Add(_finished);
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _content };
        _main.Children.Add(_scroll);
        _main.Children.Add(_bar);
        Children.Add(_main);
        Children.Add(_deniedHost);

        // A right-click menu belongs to its row; rebuilding the list under an open menu would orphan it.
        AddHandler(ContextMenu.OpenedEvent, new RoutedEventHandler((_, _) => _menuOpen = true));
        AddHandler(ContextMenu.ClosedEvent, new RoutedEventHandler((_, _) =>
        {
            _menuOpen = false;
            if (!_rebuildAfterMenu) return;
            _rebuildAfterMenu = false;
            Dispatcher.BeginInvoke(Refresh);
        }));

        _clock.Tick += (_, _) =>
        {
            // Keep "5m" fresh.
            foreach (var row in _rows) row.UpdateMeta();
        };
        Loaded += (_, _) =>
        {
            _service.Changed += Refresh;
            _clock.Start();
            Refresh();
            _service.Rescan();
        };
        Unloaded += (_, _) =>
        {
            _service.Changed -= Refresh;
            _clock.Stop();
        };
        Refresh();
    }

    private List<DownloadItem> JustDone() =>
        _service.Completed.Where(c => !_service.Recent.Take(3).Any(r => Same(r.Path, c.Path))).ToList();

    /// Every finished download on screen, once each.
    private List<string> Selectable(List<DownloadItem> justDone) =>
        justDone.Concat(_service.Recent).Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void Refresh()
    {
        if (_service.AccessDenied)
        {
            _main.Visibility = Visibility.Collapsed;
            if (_deniedHost.Content == null) _deniedHost.Content = Denied();
            _deniedHost.Visibility = Visibility.Visible;
            _listKey = "";
            return;
        }
        _deniedHost.Visibility = Visibility.Collapsed;
        _deniedHost.Content = null;
        _main.Visibility = Visibility.Visible;

        SyncActive();

        var justDone = JustDone();
        var selectable = Selectable(justDone);
        // Forget files that left the folder; leave select mode once nothing is left.
        _selection.IntersectWith(selectable);
        if (selectable.Count == 0) _selecting = false;

        var key = string.Join("\n", justDone.Select(i => $"{i.Path}|{i.Size}"))
                  + "\n#\n" + string.Join("\n", _service.Recent.Select(i => $"{i.Path}|{i.Size}|{i.Date.Ticks}"))
                  + "\n#\n" + string.Join("\n", _service.Completed.Select(i => i.Path))
                  + $"\n#{_selecting}\n" + string.Join("\n", _selection.Order());
        if (key != _listKey)
        {
            if (_menuOpen) _rebuildAfterMenu = true;
            // Rows sliding out finish first; the rebuild follows them.
            else if (!_rowsLeaving)
            {
                _listKey = key;
                BuildList(justDone, selectable);
            }
        }
        UpdateBar(selectable);
    }

    // MARK: - Downloading

    private void SyncActive()
    {
        var active = _service.Active;
        foreach (var path in _activeRows.Keys.Where(p => !active.Any(a => Same(a.Path, p))).ToList())
        {
            _activeList.Children.Remove(_activeRows[path]);
            _activeRows.Remove(path);
        }
        foreach (var item in active)
        {
            if (!_activeRows.TryGetValue(item.Path, out var row))
            {
                row = new ActiveRow { Margin = new Thickness(0, 0, 0, 6) };
                _activeRows[item.Path] = row;
                _activeList.Children.Add(row);
            }
            row.Update(item);
        }
        _activeSection.Visibility = active.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // MARK: - Finished and recent

    private void BuildList(List<DownloadItem> justDone, List<string> selectable)
    {
        _finished.Children.Clear();
        _rows.Clear();
        if (justDone.Count > 0)
        {
            _finished.Children.Add(Section("Just finished"));
            foreach (var item in justDone) _finished.Children.Add(Row(item, highlight: true));
        }
        _finished.Children.Add(Section("Recent", selectable.Count > 0));
        if (_service.Recent.Count == 0)
        {
            _finished.Children.Add(new Grid
            {
                Height = 160,
                Children = { Ui.EmptyState("\uE896", "No downloads yet", "Files you download will appear here with live progress.") },
            });
        }
        else
        {
            foreach (var item in _service.Recent)
                _finished.Children.Add(Row(item, _service.Completed.Any(c => Same(c.Path, item.Path))));
        }
        _shownSelecting = _selecting;
        _shownSelection.Clear();
        _shownSelection.UnionWith(_selection);
    }

    private DownloadRow Row(DownloadItem item, bool highlight)
    {
        var selected = _selection.Contains(item.Path);
        // Entering select mode grows the circles in from nothing; ticking one pops it.
        double? checkFrom = !_selecting ? null
            : !_shownSelecting ? 0
            : _shownSelection.Contains(item.Path) != selected ? 0.6
            : null;
        var row = new DownloadRow(item, highlight, _selecting, selected, () =>
        {
            if (!_selection.Remove(item.Path)) _selection.Add(item.Path);
            Refresh();
        }, TrashAnimated, checkFrom, fadeInActions: !_selecting && _shownSelecting)
        { Margin = new Thickness(0, 0, 0, 6) };
        _rows.Add(row);
        return row;
    }

    /// The rows shrink and fade (Motion.Pop) and the list closes up; then the files go to the Recycle Bin
    /// with the Undo offer, and <paramref name="then"/> runs.
    private void TrashAnimated(IReadOnlyCollection<string> paths, Action? then = null)
    {
        var leaving = _rows.Where(r => paths.Contains(r.Path, StringComparer.OrdinalIgnoreCase) && r.IsVisible).ToList();
        void Finish()
        {
            _rowsLeaving = false;
            _service.Trash(paths);
            then?.Invoke();
            Refresh();
        }
        if (_rowsLeaving || leaving.Count == 0)
        {
            Finish();
            return;
        }
        _rowsLeaving = true;
        for (var i = 0; i < leaving.Count; i++) leaving[i].PopOut(i == leaving.Count - 1 ? Finish : null);
    }

    private UIElement Section(string title, bool canSelect = false)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 6), LastChildFill = false };
        row.Children.Add(FileUi.Caps(title));
        if (title == "Recent")
        {
            var open = FileUi.Link("Open Downloads", () => FileMeta.Open(_service.Folder));
            DockPanel.SetDock(open, Dock.Right);
            row.Children.Add(open);
            if (canSelect)
            {
                var select = FileUi.Link(_selecting ? "Done" : "Select", () =>
                {
                    _selecting = !_selecting;
                    if (!_selecting) _selection.Clear();
                    Refresh();
                }, weight: FontWeights.SemiBold);
                select.Margin = new Thickness(0, 0, 12, 0);
                DockPanel.SetDock(select, Dock.Right);
                row.Children.Add(select);
            }
        }
        return row;
    }

    // MARK: - Bottom bar (selection, undo)

    private void UpdateBar(List<string> selectable)
    {
        var pending = _service.PendingTrash.Count;
        var allSelected = selectable.Count > 0 && _selection.Count == selectable.Count;
        var kind = _selecting ? "select" : pending > 0 ? "undo" : "";
        var key = $"{kind}|{_selection.Count}|{allSelected}|{pending}";
        if (key == _barKey) return;
        var appearing = !_barKey.StartsWith(kind + "|", StringComparison.Ordinal) && kind.Length > 0;
        _barKey = key;

        _content.Margin = new Thickness(0, 0, 0, kind.Length > 0 ? 48 : 0);
        _bar.Content = kind switch
        {
            "select" => SelectionBar(selectable, allSelected),
            "undo" => UndoBar(pending),
            _ => null,
        };
        if (appearing && _bar.Content is FrameworkElement bar)
        {
            // Slides up from the bottom edge.
            var shift = new TranslateTransform(0, 30);
            bar.RenderTransform = shift;
            bar.Opacity = 0;
            shift.Animate(TranslateTransform.YProperty, 0, Motion.ExpoOut(0.35));
            bar.Animate(OpacityProperty, 1, Motion.ExpoOut(0.35));
        }
    }

    private UIElement SelectionBar(List<string> selectable, bool allSelected)
    {
        var row = new DockPanel();
        var delete = Ui.Bare(new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 6, 12, 6),
            Background = FileVisuals.Red.WithOpacity(_selection.Count == 0 ? 0.35 : 0.9).Brush(),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "\uE74D", FontSize = 11, Foreground = Brushes.White, Style = (Style)Application.Current.FindResource("Purrch.Glyph") },
                    new TextBlock { Text = "Delete", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(6, 0, 0, 0) },
                },
            },
        }, () => TrashAnimated(_selection.ToList(), () =>
        {
            _selection.Clear();
            _selecting = false;
        }));
        delete.IsEnabled = _selection.Count > 0;
        DockPanel.SetDock(delete, Dock.Right);
        row.Children.Add(delete);

        var label = Ui.Text(_selection.Count == 0 ? "Click downloads to select" : $"{_selection.Count} selected", 12, weight: FontWeights.SemiBold);
        row.Children.Add(label);
        var toggle = FileUi.Link(allSelected ? "Deselect all" : "Select all", () =>
        {
            if (allSelected) _selection.Clear();
            else _selection.UnionWith(selectable);
            Refresh();
        }, 12, FontWeights.Normal);
        toggle.Margin = new Thickness(10, 0, 0, 0);
        toggle.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(toggle);
        // Fills the rest so the delete button sits at the right edge.
        row.Children.Add(new Border());
        return Capsule(row, HorizontalAlignment.Stretch);
    }

    private UIElement UndoBar(int count)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Glyph("\uE74D", 12, ThemeResources.SecondaryText));
        var label = Ui.Text(count == 1 ? "Moved to Recycle Bin" : $"Moved {count} to Recycle Bin", 12, weight: FontWeights.SemiBold);
        label.Margin = new Thickness(10, 0, 10, 0);
        row.Children.Add(label);
        row.Children.Add(FileUi.Link("Undo", _service.UndoTrash, 12, FontWeights.Bold));
        return Capsule(row, HorizontalAlignment.Center);
    }

    private static Border Capsule(UIElement content, HorizontalAlignment alignment)
    {
        var capsule = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14, 8, 14, 8),
            BorderThickness = new Thickness(0.8),
            HorizontalAlignment = alignment,
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.4, BlurRadius = 20, ShadowDepth = 4, Direction = 270 },
            Child = content,
        };
        capsule.SetResourceReference(Border.BackgroundProperty, ThemeResources.Surface);
        capsule.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);
        return capsule;
    }

    // MARK: - Can't read the folder

    private UIElement Denied()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new Grid
        {
            Height = 150,
            Children =
            {
                Ui.EmptyState("\uE72E", "Can't read Downloads",
                    $"Purrch couldn't open {_service.Folder}.\nCheck that the folder exists and opens in File Explorer."),
            },
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 12) };
        // File Explorer says what is wrong (a missing drive, no permission) and can fix the folder's location.
        buttons.Children.Add(Ui.Pill("Open in File Explorer", "\uE838", () => FileMeta.OpenFolder(_service.Folder), prominent: true));
        var retry = Ui.Pill("Try again", "\uE72C", _service.Rescan);
        retry.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(retry);
        stack.Children.Add(buttons);
        return stack;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// A download in flight: name, percentage, a glowing bar, and bytes, speed and time left.
internal sealed class ActiveRow : ListRow
{
    private readonly MiddleTrimmedText _name = new("", 13);
    private readonly TextBlock _percent = Ui.Text("", 12, ThemeResources.Accent, FontWeights.SemiBold).Tabular();
    private readonly TextBlock _detail = Ui.Text("", 11, ThemeResources.SecondaryText).Tabular();
    private readonly Grid _bar = new() { Height = 5, Margin = new Thickness(0, 5, 0, 5) };
    private readonly Border _fill = new() { CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left, Width = 4 };
    private double _progress;

    public ActiveRow() : base(new DockPanel())
    {
        var layout = (DockPanel)Child;

        var icon = new Grid { Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var disc = new Ellipse();
        disc.SetResourceReference(Shape.FillProperty, ThemeResources.Accent);
        icon.Children.Add(disc);
        icon.Children.Add(Ui.Glyph("\uE896", 11, ThemeResources.Background));
        if (AppSettings.Shared.Theme.Glow && Application.Current.Resources[ThemeResources.Accent] is SolidColorBrush accent)
            icon.Effect = ExpandedView.Glow(accent.Color, 4);
        DockPanel.SetDock(icon, Dock.Left);
        layout.Children.Add(icon);

        var text = new StackPanel();
        var top = new DockPanel();
        DockPanel.SetDock(_percent, Dock.Right);
        _percent.Margin = new Thickness(8, 0, 0, 0);
        top.Children.Add(_percent);
        top.Children.Add(_name);
        text.Children.Add(top);

        var track = new Border { CornerRadius = new CornerRadius(2.5) };
        track.SetResourceReference(Border.BackgroundProperty, ThemeResources.Fill);
        _fill.SetResourceReference(Border.BackgroundProperty, ThemeResources.Accent);
        if (icon.Effect != null && Application.Current.Resources[ThemeResources.Accent] is SolidColorBrush glow)
            _fill.Effect = ExpandedView.Glow(glow.Color, 3);
        _bar.Children.Add(track);
        _bar.Children.Add(_fill);
        _bar.SizeChanged += (_, _) => _fill.Set(WidthProperty, FillWidth);
        text.Children.Add(_bar);
        _detail.TextWrapping = TextWrapping.NoWrap;
        _detail.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(_detail);
        layout.Children.Add(text);
    }

    private double FillWidth => Math.Max(4, _bar.ActualWidth * _progress);

    public void Update(ActiveDownload item)
    {
        _name.FullText = item.Name;
        ToolTip = item.Name;
        _percent.Text = item.Total != null ? $"{(int)(item.Progress * 100)}%" : "\u2026";
        _detail.Text = Detail(item);
        if (Math.Abs(item.Progress - _progress) < 0.0001) return;
        _progress = item.Progress;
        if (_bar.ActualWidth > 0) _fill.Animate(WidthProperty, FillWidth, Motion.ExpoOut(0.4));
    }

    private static string Detail(ActiveDownload item)
    {
        var parts = new List<string>
        {
            item.Total is { } total ? $"{Format.Bytes(item.Bytes)} of {Format.Bytes(total)}" : Format.Bytes(item.Bytes),
        };
        if (item.Speed > 0)
        {
            parts.Add($"{Format.Bytes((long)item.Speed)}/s");
            if (item.Total is { } t && t > item.Bytes) parts.Add($"{Format.Duration((t - item.Bytes) / item.Speed)} left");
        }
        else
        {
            parts.Add("waiting\u2026");
        }
        return string.Join(" \u00B7 ", parts);
    }
}

/// A finished file: thumbnail or icon, name, type tag, size and age, and quick actions.
/// In select mode the actions give way to a check circle and a click toggles the row.
internal sealed class DownloadRow : ListRow
{
    private readonly DownloadItem _item;
    private readonly TextBlock _meta;

    /// <paramref name="checkFrom"/> is the scale the check circle grows from (0 entering select mode, 0.6 when
    /// ticked), or null to show it still; <paramref name="fadeInActions"/> fades the buttons back in after it.
    public DownloadRow(DownloadItem item, bool highlight, bool selecting, bool selected, Action toggle,
                       Action<IReadOnlyCollection<string>, Action?> trash, double? checkFrom = null, bool fadeInActions = false)
        : base(new DockPanel())
    {
        _item = item;
        var layout = (DockPanel)Child;
        var isImage = Converter.ImageConverter.IsImage(item.Path);
        Cursor = selecting ? System.Windows.Input.Cursors.Hand : null;
        ToolTip = item.Name;

        if (selecting)
        {
            var check = Check(selected);
            check.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(check, Dock.Left);
            layout.Children.Add(check);
            if (checkFrom is { } from)
            {
                // The Mac check's scale and opacity transition, with Motion.Pop's spring.
                var grow = new ScaleTransform(from, from);
                check.RenderTransformOrigin = new Point(0.5, 0.5);
                check.RenderTransform = grow;
                check.Opacity = from;
                grow.Animate(ScaleTransform.ScaleXProperty, 1, Motion.Pop);
                grow.Animate(ScaleTransform.ScaleYProperty, 1, Motion.Pop);
                check.Animate(OpacityProperty, 1, Motion.ExpoOut(0.3));
            }
            if (selected)
            {
                BorderThickness = new Thickness(1.5);
                SetResourceReference(BorderBrushProperty, ThemeResources.Accent);
            }
        }

        var icon = FileVisuals.Thumbnail(item.Path, 30, 6);
        icon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        layout.Children.Add(icon);

        if (!selecting)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            if (isImage) actions.Children.Add(CopyButton());
            actions.Children.Add(Action("\uE721", "Show in File Explorer", () => FileMeta.Reveal(item.Path)));
            actions.Children.Add(Action("\uE8A7", "Open", () => FileMeta.Open(item.Path)));
            actions.Children.Add(Action("\uE74D", "Move to Recycle Bin", () => trash(new[] { item.Path }, null)));
            DockPanel.SetDock(actions, Dock.Right);
            layout.Children.Add(actions);
            if (fadeInActions)
            {
                actions.Opacity = 0;
                actions.Animate(OpacityProperty, 1, Motion.ExpoOut(0.35));
            }
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new MiddleTrimmedText(item.Name, 13));
        var tags = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        var label = FileMeta.Label(item.Path);
        tags.Children.Add(FileUi.Tag(label, FileMeta.ColorFor(label)));
        if (highlight)
        {
            var done = FileUi.Tag("Done", FileVisuals.Green);
            done.Margin = new Thickness(5, 0, 0, 0);
            tags.Children.Add(done);
        }
        _meta = Ui.Text("", 11, ThemeResources.SecondaryText);
        _meta.TextWrapping = TextWrapping.NoWrap;
        _meta.Margin = new Thickness(5, 0, 0, 0);
        tags.Children.Add(_meta);
        text.Children.Add(tags);
        layout.Children.Add(text);
        UpdateMeta();

        if (selecting) MouseLeftButtonUp += (_, _) => toggle();
        FileUi.DragOut(this, () => FileMeta.DragData(new[] { item.Path }));
        ContextMenu = Menu(item, trash);
    }

    public string Path => _item.Path;

    public void UpdateMeta()
    {
        var parts = new List<string>();
        if (_item.Size > 0) parts.Add(Format.Bytes(_item.Size));
        parts.Add(Format.Relative(_item.Date));
        _meta.Text = string.Join(" \u00B7 ", parts);
    }

    private static Button Action(string glyph, string tooltip, Action onClick)
    {
        var button = Ui.IconButton(glyph, 26, onClick, tooltip: tooltip);
        button.Margin = new Thickness(6, 0, 0, 0);
        return button;
    }

    /// Copies the picture; the icon turns into a tick for a moment.
    private Button CopyButton()
    {
        var glyph = Ui.Glyph("\uE8C8", 26 * 0.4);
        var circle = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Child = glyph };
        circle.SetResourceReference(Border.BackgroundProperty, ThemeResources.Fill);
        var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        reset.Tick += (_, _) => { reset.Stop(); glyph.Text = "\uE8C8"; };
        var button = Ui.Bare(circle, () =>
        {
            FileMeta.CopyFiles(new[] { _item.Path });
            glyph.Text = "\uE73E";
            reset.Stop();
            reset.Start();
        }, "Copy image");
        button.Margin = new Thickness(6, 0, 0, 0);
        return button;
    }

    private static FrameworkElement Check(bool selected)
    {
        var grid = new Grid { Width = 17, Height = 17, VerticalAlignment = VerticalAlignment.Center };
        var ring = new Ellipse { StrokeThickness = 1.5 };
        if (selected)
        {
            ring.SetResourceReference(Shape.FillProperty, ThemeResources.Accent);
            ring.SetResourceReference(Shape.StrokeProperty, ThemeResources.Accent);
            grid.Children.Add(ring);
            grid.Children.Add(Ui.Glyph("\uE73E", 9, ThemeResources.Background));
        }
        else
        {
            ring.SetResourceReference(Shape.StrokeProperty, ThemeResources.SecondaryText);
            grid.Children.Add(ring);
        }
        return grid;
    }

    private static ContextMenu Menu(DownloadItem item, Action<IReadOnlyCollection<string>, Action?> trash)
    {
        var menu = new ContextMenu();
        void Add(string header, Action action)
        {
            var entry = new MenuItem { Header = header };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }
        Add("Open", () => FileMeta.Open(item.Path));
        Add("Show in File Explorer", () => FileMeta.Reveal(item.Path));
        Add("Copy", () => FileMeta.CopyFiles(new[] { item.Path }));
        menu.Items.Add(new Separator());
        Add("Move to Recycle Bin", () => trash(new[] { item.Path }, null));
        return menu;
    }
}
