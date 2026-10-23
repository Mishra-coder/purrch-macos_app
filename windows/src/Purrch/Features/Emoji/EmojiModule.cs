using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Purrch.Core;
using Purrch.DesignSystem;

namespace Purrch.Features.Emoji;

/// Emoji picker: search, nine categories, recently used, and one click to copy.
public sealed class EmojiModule : Grid
{
    private readonly EmojiRecents _recents = EmojiRecents.Shared;
    private readonly Dictionary<EmojiCategory, Border> _categoryButtons = new();
    private readonly StackPanel _recentRow = new() { Orientation = Orientation.Horizontal };
    private readonly UIElement _recentSection;
    private readonly WrapPanel _grid = new();
    private readonly ScrollViewer _gridScroll;
    private readonly ContentControl _body = new();
    private readonly TextBox _searchBox;
    private readonly TextBlock _status;
    private readonly Border _toast;
    private readonly TextBlock _toastText;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };
    // Cells are kept, so switching categories and typing don't rebuild every emoji drawing.
    private readonly Dictionary<string, EmojiCell> _cells = new();
    private readonly Dictionary<string, EmojiCell> _recentCells = new();
    private EmojiCategory _category = EmojiCategory.Smileys;
    private string _query = "";
    private EmojiEntry? _hovered;
    private int _resultCount;

    public EmojiModule()
    {
        var layout = new DockPanel();

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var search = Ui.SearchField("Search emoji", q => { _query = q; Rebuild(); }, out _searchBox);
        search.Width = 250;
        top.Children.Add(search);
        var categories = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var category in EmojiCategoryInfo.All) categories.Children.Add(CategoryButton(category));
        top.Children.Add(categories);
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);

        _recentSection = RecentSection();
        DockPanel.SetDock(_recentSection, Dock.Top);
        layout.Children.Add(_recentSection);

        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var hint = Ui.Text("Click to copy", 11, ThemeResources.SecondaryText);
        hint.Opacity = 0.7;
        DockPanel.SetDock(hint, Dock.Right);
        footer.Children.Add(hint);
        _status = Ui.Text("", 11, ThemeResources.SecondaryText, FontWeights.Medium);
        _status.TextWrapping = TextWrapping.NoWrap;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        footer.Children.Add(_status);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);

        _gridScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _grid };
        layout.Children.Add(_body);
        Children.Add(layout);

        (_toast, _toastText) = Toast();
        Children.Add(_toast);
        _toastTimer.Tick += (_, _) => HideToast();

        Rebuild();
        Loaded += (_, _) => _recents.Changed += RebuildRecents;
        Unloaded += (_, _) => { _recents.Changed -= RebuildRecents; _toastTimer.Stop(); };
    }

    private IEnumerable<EmojiEntry> Visible()
    {
        var q = _query.Trim().ToLowerInvariant();
        if (q.Length == 0) return EmojiCatalog.All.Where(e => e.Category == _category);
        var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return EmojiCatalog.All.Where(e => terms.All(e.Name.Contains) || e.Char == q);
    }

    private void Rebuild()
    {
        foreach (var (category, button) in _categoryButtons)
        {
            var selected = category == _category && _query.Trim().Length == 0;
            if (selected) button.SetResourceReference(Border.BackgroundProperty, ThemeResources.AccentFaint);
            else button.Background = Brushes.Transparent;
            ((TextBlock)button.Child).SetResourceReference(TextBlock.ForegroundProperty,
                selected ? ThemeResources.Accent : ThemeResources.SecondaryText);
        }

        var items = Visible().ToList();
        _resultCount = items.Count;
        if (items.Count == 0)
        {
            _body.Content = Ui.EmptyState("\uE76E", "No emoji found", "Try another word.");
        }
        else
        {
            _grid.Children.Clear();
            foreach (var entry in items) _grid.Children.Add(Cell(_cells, entry, 36));
            _gridScroll.ScrollToTop();
            _body.Content = _gridScroll;
        }
        RebuildRecents();
        UpdateStatus();
    }

    private void RebuildRecents()
    {
        _recentRow.Children.Clear();
        foreach (var ch in _recents.Items)
        {
            var entry = EmojiCatalog.ByChar.GetValueOrDefault(ch) ?? new EmojiEntry(ch, "", EmojiCategory.Symbols);
            _recentRow.Children.Add(Cell(_recentCells, entry, 28));
        }
        _recentSection.Visibility = _recents.Items.Count > 0 && _query.Trim().Length == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStatus() =>
        _status.Text = _hovered is { } h ? $"{h.Char}  {h.DisplayName}"
            : _query.Trim().Length == 0 ? _category.Title()
            : _resultCount == 1 ? "1 result" : $"{_resultCount} results";

    private EmojiCell Cell(Dictionary<string, EmojiCell> cache, EmojiEntry entry, double size)
    {
        if (!cache.TryGetValue(entry.Char, out var cell))
        {
            cell = new EmojiCell(entry, size, Copy);
            cell.MouseEnter += (_, _) => { _hovered = entry; UpdateStatus(); };
            cell.MouseLeave += (_, _) =>
            {
                if (_hovered?.Char == entry.Char) _hovered = null;
                UpdateStatus();
            };
            cache[entry.Char] = cell;
        }
        return cell;
    }

    private Border CategoryButton(EmojiCategory category)
    {
        var icon = new TextBlock
        {
            Text = category.Icon(),
            FontFamily = new FontFamily("Segoe UI Emoji"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var button = new Border
        {
            Width = 30,
            Height = 28,
            Margin = new Thickness(0, 0, 2, 0),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = category.Title(),
            Child = icon,
        };
        button.MouseLeftButtonUp += (_, _) =>
        {
            _category = category;
            _hovered = null;
            // Clearing the box calls Rebuild through the search field.
            if (_query.Length > 0) _searchBox.Clear();
            else Rebuild();
        };
        _categoryButtons[category] = button;
        return button;
    }

    private UIElement RecentSection()
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var row = new DockPanel();
        // Hair spaces stand in for the Mac label's wide letter spacing; WPF has no tracking.
        var label = Ui.Text(string.Join(" ", "RECENT".ToCharArray()), 10, ThemeResources.SecondaryText, FontWeights.SemiBold);
        label.Width = 52;
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _recentRow,
        };
        // The wheel scrolls the row sideways.
        scroll.PreviewMouseWheel += (_, e) =>
        {
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        };
        row.Children.Add(scroll);
        section.Children.Add(row);

        var divider = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 0), Opacity = 0.5 };
        divider.SetResourceReference(Border.BackgroundProperty, ThemeResources.Border);
        section.Children.Add(divider);
        return section;
    }

    private static (Border, TextBlock) Toast()
    {
        var text = new global::Emoji.Wpf.TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold };
        text.SetResourceReference(TextBlock.ForegroundProperty, ThemeResources.Background);
        var toast = new Border
        {
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 7, 14, 7),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 22),
            IsHitTestVisible = false,
            Opacity = 0,
            Visibility = Visibility.Collapsed,
            RenderTransform = new TranslateTransform(0, 24),
            Child = text,
        };
        toast.SetResourceReference(Border.BackgroundProperty, ThemeResources.Accent);
        return (toast, text);
    }

    private void Copy(EmojiEntry entry)
    {
        try { System.Windows.Clipboard.SetText(entry.Char); }
        catch (System.Runtime.InteropServices.ExternalException) { return; }
        _recents.Use(entry.Char);

        _toastText.Text = $"Copied {entry.Char} \U0001F44D";
        _toast.Effect = AppSettings.Shared.Theme.Glow
                        && Application.Current.Resources[ThemeResources.Accent] is SolidColorBrush accent
            ? Interface.ExpandedView.Glow(accent.Color, 10)
            : null;
        _toast.Visibility = Visibility.Visible;
        _toast.Animate(OpacityProperty, 1, Motion.Pop);
        _toast.RenderTransform.Animate(TranslateTransform.YProperty, 0, Motion.Pop);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        _toast.Animate(OpacityProperty, 0, Motion.ExpoOut(0.2), completed: () =>
        {
            if (_toast.Opacity == 0) _toast.Visibility = Visibility.Collapsed;
        });
        _toast.RenderTransform.Animate(TranslateTransform.YProperty, 24, Motion.ExpoOut(0.2));
    }
}

/// One emoji in the grid or the recent row: grows and tints on hover, click copies, drag drops the text.
internal sealed class EmojiCell : Border
{
    private const double DragThreshold = 6;
    private readonly ScaleTransform _scale = new(1, 1);
    private Point? _pressPoint;

    public EmojiCell(EmojiEntry entry, double size, Action<EmojiEntry> copy)
    {
        Width = size;
        Height = size;
        Margin = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _scale;
        Child = new global::Emoji.Wpf.TextBlock
        {
            Text = entry.Char,
            FontSize = size * 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        MouseEnter += (_, _) =>
        {
            Background = Hover();
            _scale.Animate(ScaleTransform.ScaleXProperty, 1.12, Motion.ExpoOut(0.12));
            _scale.Animate(ScaleTransform.ScaleYProperty, 1.12, Motion.ExpoOut(0.12));
        };
        MouseLeave += (_, _) =>
        {
            Background = Brushes.Transparent;
            _scale.Animate(ScaleTransform.ScaleXProperty, 1, Motion.ExpoOut(0.12));
            _scale.Animate(ScaleTransform.ScaleYProperty, 1, Motion.ExpoOut(0.12));
            _pressPoint = null;
        };
        MouseLeftButtonDown += (_, e) => _pressPoint = e.GetPosition(this);
        MouseMove += (_, e) =>
        {
            if (_pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - start.X) < DragThreshold && Math.Abs(p.Y - start.Y) < DragThreshold) return;
            _pressPoint = null;
            DragDrop.DoDragDrop(this, entry.Char, DragDropEffects.Copy);
        };
        MouseLeftButtonUp += (_, _) =>
        {
            if (_pressPoint != null) copy(entry);
            _pressPoint = null;
        };
    }

    /// Accent at 20%, from whatever accent is on screen now.
    private static Brush Hover() =>
        Application.Current.Resources[ThemeResources.Accent] is SolidColorBrush accent
            ? accent.Color.WithOpacity(0.2).Brush()
            : Brushes.Transparent;
}
