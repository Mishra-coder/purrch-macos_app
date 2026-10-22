using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    public ClipboardModule()
    {
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var clear = Ui.IconButton("\uE74D", 34, _service.ClearUnpinned, tooltip: "Clear unpinned items");
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

    private void Rebuild()
    {
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
        foreach (var item in items) _list.Children.Add(new ClipRow(item, _service.LastCopiedId == item.Id));
        _body.Content = _scroll;
    }
}

/// One clipboard item: icon or thumbnail, title, tag and details, and a pin on hover.
internal sealed class ClipRow : Border
{
    private const double DragThreshold = 6;
    private readonly ClipItem _item;
    private readonly Button _pin;
    private Point? _pressPoint;

    public ClipRow(ClipItem item, bool copied)
    {
        _item = item;
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(10, 8, 10, 8);
        Margin = new Thickness(0, 0, 0, 6);
        Cursor = Cursors.Hand;
        ToolTip = item.Kind is ClipKind.Text or ClipKind.Url ? Preview(item.Text) : null;
        SetResourceReference(BackgroundProperty, ThemeResources.Surface);

        var row = new DockPanel();

        var tint = TagColor(item);
        var icon = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(8),
            Background = tint.WithOpacity(0.14).Brush(),
            Child = Icon(item, tint),
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

        if (copied)
        {
            var done = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 0) };
            done.Children.Add(Ui.Glyph("\uE73E", 11, ThemeResources.Accent));
            var label = Ui.Text("Copied", 11, ThemeResources.Accent, FontWeights.SemiBold);
            label.Margin = new Thickness(4, 0, 0, 0);
            done.Children.Add(label);
            DockPanel.SetDock(done, Dock.Right);
            row.Children.Add(done);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Ui.Text(item.Title, 13, weight: FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(title);

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
    }

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
        if (item.Kind == ClipKind.Files && item.Paths is [var first, ..])
            Add("Show in File Explorer", () => Process.Start("explorer.exe", $"/select,\"{first}\""));
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

    private static Color TagColor(ClipItem item) => item.Kind switch
    {
        ClipKind.Text => Color.FromRgb(0x98, 0x98, 0x9D),
        ClipKind.Url => Color.FromRgb(0x0A, 0x84, 0xFF),
        ClipKind.Image => Color.FromRgb(0xBF, 0x5A, 0xF2),
        _ => item.TagLabel == "Folder" ? Color.FromRgb(0x64, 0xD2, 0xFF) : Color.FromRgb(0xFF, 0x9F, 0x0A),
    };

    private static UIElement Icon(ClipItem item, Color tint)
    {
        switch (item.Kind)
        {
            case ClipKind.Image when item.ImagePath is { } path && File.Exists(path):
                return new Image { Source = ClipboardService.LoadImage(path, 68), Stretch = Stretch.UniformToFill };
            case ClipKind.Files when item.Paths is [var first, ..] && FileIcon(first) is { } fileIcon:
                return new Image { Source = fileIcon, Margin = new Thickness(3), Stretch = Stretch.Uniform };
            default:
                var glyph = item.Kind switch
                {
                    ClipKind.Url => "\uE71B",
                    ClipKind.Files => "\uE8A5",
                    ClipKind.Image => "\uEB9F",
                    _ => "\uE8E4",
                };
                return new TextBlock
                {
                    Text = glyph,
                    FontSize = 14,
                    Foreground = tint.Brush(),
                    Style = (Style)Application.Current.FindResource("Purrch.Glyph"),
                };
        }
    }

    /// The icon Explorer shows for a file or folder.
    private static ImageSource? FileIcon(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
