using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Purrch.Core;
using Purrch.DesignSystem;

namespace Purrch.Features.Clipboard;

/// Searchable clipboard history. Click a row to copy it again, drag it into another app,
/// right-click for more.
public sealed class ClipboardModule : DockPanel
{
    private readonly ClipboardService _service = ClipboardService.Shared;
    private readonly StackPanel _list = new();
    private readonly ContentControl _body = new();
    private readonly ScrollViewer _scroll;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private string _query = "";
    /// The item whose "Copied" mark is on screen, so the mark fades in once and fades out when it goes.
    private Guid? _shownCopiedId;
    private bool _clearing;

    public ClipboardModule()
    {
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var clear = Ui.IconButton("\uE74D", 34, ClearUnpinned, tooltip: "Clear unpinned items");
        clear.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(clear, Dock.Right);
        top.Children.Add(clear);
        top.Children.Add(Ui.SearchField("Search clipboard", q => { _query = q; Rebuild(); }, out _));
        SetDock(top, Dock.Top);
        Children.Add(top);

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _list };
        Children.Add(_body);

        Rebuild();
        // Keep "5m ago" fresh, and only listen while on screen.
        _clock.Tick += (_, _) => Rebuild();
        Loaded += (_, _) => { _service.Changed += Rebuild; _clock.Start(); Rebuild(); };
        Unloaded += (_, _) => { _service.Changed -= Rebuild; _clock.Stop(); };
    }

    private IEnumerable<ClipItem> Filtered()
    {
        var q = _query.Trim();
        var all = _service.Ordered;
        if (q.Length == 0) return all;
        bool Has(string? s) => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;
        return all.Where(i => Has(i.Title) || Has(i.Text) || Has(i.SourceApp) || Has(i.TagLabel)
                              || (i.Paths?.Any(Has) ?? false));
    }

    /// Fades the unpinned rows out, then removes them.
    private void ClearUnpinned()
    {
        if (_clearing) return;
        var leaving = _body.Content == _scroll
            ? _list.Children.OfType<ClipRow>().Where(r => !r.Item.Pinned).ToList()
            : new List<ClipRow>();
        if (leaving.Count == 0)
        {
            _service.ClearUnpinned();
            return;
        }
        _clearing = true;
        foreach (var row in leaving)
        {
            row.IsHitTestVisible = false;
            row.Animate(OpacityProperty, 0, Motion.EaseOut(0.2), completed: row == leaving[^1] ? () =>
            {
                _clearing = false;
                _service.ClearUnpinned();
            } : null);
        }
    }

    private void Rebuild()
    {
        var shown = _shownCopiedId;
        _shownCopiedId = _service.LastCopiedId;
        if (_service.Items.Count == 0)
        {
            _body.Content = Ui.EmptyState("\uE77F", "Nothing copied yet", "Text, links, files and images you copy will appear here.");
            return;
        }
        var items = Filtered().ToList();
        if (items.Count == 0)
        {
            _body.Content = Ui.EmptyState("\uE721", "No matches", "Try a different search.");
            return;
        }
        _list.Children.Clear();
        foreach (var item in items) _list.Children.Add(new ClipRow(item, _service.LastCopiedId == item.Id, shown == item.Id));
        _body.Content = _scroll;
    }
}

/// One clipboard item: icon or thumbnail, title, tag and details, and a pin on hover.
internal sealed class ClipRow : Border
{
    private const double DragThreshold = 6;
    private readonly ClipItem _item;
    private readonly Button _pin;
    private readonly CancellationTokenSource _thumbnail = new();
    private Point? _pressPoint;

    /// <paramref name="wasCopied"/>: the row showed "Copied" before this rebuild, so the mark fades
    /// out when it is no longer copied, and doesn't fade in again while it still is.
    public ClipRow(ClipItem item, bool copied, bool wasCopied)
    {
        _item = item;
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(10, 8, 10, 8);
        Margin = new Thickness(0, 0, 0, 6);
        Cursor = Cursors.Hand;
        ToolTip = item.Kind is ClipKind.Text or ClipKind.Url ? Preview(item.Text) : null;
        SetResourceReference(BackgroundProperty, ThemeResources.Surface);

        var row = new DockPanel();

        var tint = FileMeta.ColorFor(item.TagLabel);
        var icon = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(8),
            Background = tint.WithOpacity(0.14).Brush(),
            Child = Icon(item, tint, _thumbnail.Token),
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 10, 0),
        };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);

        _pin = Ui.Bare(Ui.Glyph(item.Pinned ? "\uE840" : "\uE718", 12,
            item.Pinned ? ThemeResources.Accent : ThemeResources.SecondaryText), () => ClipboardService.Shared.TogglePin(item),
            item.Pinned ? "Unpin" : "Pin");
        _pin.Width = 24;
        _pin.Height = 24;
        _pin.Visibility = item.Pinned ? Visibility.Visible : Visibility.Hidden;
        DockPanel.SetDock(_pin, Dock.Right);
        row.Children.Add(_pin);

        if (copied || wasCopied)
        {
            var done = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 0) };
            done.Children.Add(Ui.Glyph("\uE73E", 11, ThemeResources.Accent));
            var label = Ui.Text("Copied", 11, ThemeResources.Accent, FontWeights.SemiBold);
            label.Margin = new Thickness(4, 0, 0, 0);
            done.Children.Add(label);
            DockPanel.SetDock(done, Dock.Right);
            row.Children.Add(done);
            if (copied != wasCopied)
            {
                done.Opacity = copied ? 0 : 1;
                done.Animate(OpacityProperty, copied ? 1 : 0, Motion.EaseOut(0.15));
            }
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.MiddleTrimmed(item.Title, 13, weight: FontWeights.SemiBold));

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        var tag = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(5, 0, 5, 1),
            Background = tint.WithOpacity(0.18).Brush(),
            Child = new TextBlock { Text = item.TagLabel, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = tint.Brush() },
            Margin = new Thickness(0, 0, 5, 0),
        };
        meta.Children.Add(tag);
        var details = Ui.Text(Details(item), 11, ThemeResources.SecondaryText);
        details.TextWrapping = TextWrapping.NoWrap;
        details.TextTrimming = TextTrimming.CharacterEllipsis;
        meta.Children.Add(details);
        text.Children.Add(meta);
        row.Children.Add(text);

        Child = row;
        ContextMenu = Menu(item);

        MouseEnter += (_, _) => { _pin.Visibility = Visibility.Visible; SetResourceReference(BackgroundProperty, ThemeResources.Fill); };
        MouseLeave += (_, _) =>
        {
            if (!_item.Pinned) _pin.Visibility = Visibility.Hidden;
            SetResourceReference(BackgroundProperty, ThemeResources.Surface);
        };
        MouseLeftButtonDown += (_, e) => { _pressPoint = e.GetPosition(this); };
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += (_, e) =>
        {
            // A click (not a drag, not on the pin) copies the item again.
            if (_pressPoint != null && !_pin.IsMouseOver) ClipboardService.Shared.Copy(_item);
            _pressPoint = null;
        };
        Unloaded += (_, _) => _thumbnail.Cancel();
    }

    public ClipItem Item => _item;

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - start.X) < DragThreshold && Math.Abs(p.Y - start.Y) < DragThreshold) return;
        _pressPoint = null;
        DragDrop.DoDragDrop(this, ClipboardService.DragData(_item), DragDropEffects.Copy);
    }

    private static ContextMenu Menu(ClipItem item)
    {
        var service = ClipboardService.Shared;
        var menu = new ContextMenu();
        void Add(string header, Action action)
        {
            var entry = new MenuItem { Header = header };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }
        Add("Copy", () => service.Copy(item));
        Add(item.Pinned ? "Unpin" : "Pin", () => service.TogglePin(item));
        if (item.Kind == ClipKind.Files && item.Paths is { Count: > 0 } paths)
            Add("Show in File Explorer", () => FileMeta.Reveal(paths));
        if (item.Kind == ClipKind.Url && item.Text is { } link)
            Add("Open Link", () => Process.Start(new ProcessStartInfo(link.Trim()) { UseShellExecute = true }));
        menu.Items.Add(new Separator());
        Add("Delete", () => service.Remove(item));
        return menu;
    }

    private static string Details(ClipItem item)
    {
        var parts = new List<string>();
        if (item.Size > 0) parts.Add(Format.Bytes(item.Size));
        if (item.SourceApp is { } app) parts.Add(app);
        parts.Add(Format.Relative(item.Date));
        return string.Join(" · ", parts);
    }

    private static string? Preview(string? text) =>
        text is null ? null : text.Length > 600 ? text[..600] + "…" : text;

    private static UIElement Icon(ClipItem item, Color tint, CancellationToken token)
    {
        switch (item.Kind)
        {
            case ClipKind.Image when item.ImagePath is { } path && File.Exists(path):
                return new Image { Source = ClipboardService.LoadImage(path, 68), Stretch = Stretch.UniformToFill };
            case ClipKind.Files when item.Paths is [var first, ..]:
                // File Explorer's own icon (folders included), with a plain glyph until it arrives.
                var art = new Grid();
                art.Children.Add(Glyph(Directory.Exists(first) ? "\uE8B7" : "\uE8A5", tint));
                var image = new Image { Margin = new Thickness(3), Stretch = Stretch.Uniform };
                art.Children.Add(image);
                Thumbnails.Request(first, 64, source =>
                {
                    if (source == null) return;
                    image.Source = source;
                    art.Children.RemoveAt(0);
                }, token);
                return art;
            default:
                return Glyph(item.Kind switch
                {
                    ClipKind.Url => "\uE71B",
                    ClipKind.Files => "\uE8A5",
                    ClipKind.Image => "\uEB9F",
                    _ => "\uE8E4",
                }, tint);
        }
    }

    private static TextBlock Glyph(string glyph, Color tint) => new()
    {
        Text = glyph,
        FontSize = 14,
        Foreground = tint.Brush(),
        Style = (Style)Application.Current.FindResource("Purrch.Glyph"),
    };
}
