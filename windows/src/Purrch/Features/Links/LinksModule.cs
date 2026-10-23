using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Purrch.DesignSystem;

namespace Purrch.Features.Links;

/// Search the web or open a typed address, plus a grid of saved sites.
public sealed class LinksModule : DockPanel
{
    private const double TileMin = 104;
    private const double Spacing = 8;

    private readonly LinksStore _store = LinksStore.Shared;
    private readonly ContentControl _grid = new();
    private readonly UniformGrid _tiles = new() { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-Spacing / 2) };
    private readonly ScrollViewer _scroll;
    private readonly Grid _overlay = new();
    private readonly TextBox _query;
    private readonly TextBlock _queryIcon;
    private readonly TextBlock _queryHint;
    private readonly ContentControl _engine = new();
    private SiteEditor? _editor;

    public LinksModule()
    {
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var add = Ui.IconButton("\uE710", 32, () => Edit(new SavedSite(), isNew: true), prominent: true, tooltip: "Add site");
        add.Margin = new Thickness(8, 0, 0, 0);
        SetDock(add, Dock.Right);
        top.Children.Add(add);
        _engine.Margin = new Thickness(8, 0, 0, 0);
        SetDock(_engine, Dock.Right);
        top.Children.Add(_engine);
        top.Children.Add(Field("", "\uE721", out _query, out _queryIcon, out _queryHint));
        _query.TextChanged += (_, _) =>
            _queryIcon.Text = LinkParsing.Url(_query.Text) == null ? "\uE721" : "\uE774";
        _query.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Submit();
            e.Handled = true;
        };
        SetDock(top, Dock.Top);
        Children.Add(top);

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _tiles,
        };
        // As many columns of at least 104 px as fit, like the Mac app's adaptive grid.
        _scroll.SizeChanged += (_, _) => FitColumns();
        _overlay.Children.Add(_grid);
        Children.Add(_overlay);

        RefreshEngine();
        Rebuild();
        Loaded += (_, _) => _store.Changed += Rebuild;
        Unloaded += (_, _) => _store.Changed -= Rebuild;
    }

    private void FitColumns()
    {
        var width = _scroll.ActualWidth;
        if (width <= 0) return;
        _tiles.Columns = Math.Max(1, (int)((width + Spacing) / (TileMin + Spacing)));
    }

    private void Rebuild()
    {
        if (_store.Sites.Count == 0)
        {
            _grid.Content = Ui.EmptyState("\uE71B", "No saved sites", "Press + to add one");
            return;
        }
        _tiles.Children.Clear();
        foreach (var site in _store.Sites) _tiles.Children.Add(new SiteTile(site, this));
        FitColumns();
        _grid.Content = _scroll;
    }

    private void RefreshEngine()
    {
        var engine = _store.Engine;
        _queryHint.Text = $"Search {engine.Title()} or type a URL";

        var menu = new ContextMenu();
        foreach (var e in Enum.GetValues<SearchEngine>())
        {
            var choice = e;
            var item = new MenuItem { Header = e.Title(), IsCheckable = true, IsChecked = e == engine };
            item.Click += (_, _) => { _store.Engine = choice; RefreshEngine(); };
            menu.Items.Add(item);
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Text(engine.Title(), 12, weight: FontWeights.SemiBold));
        var chevron = Ui.Glyph("\uE70D", 9);
        chevron.FontWeight = FontWeights.Bold;
        chevron.Margin = new Thickness(5, 1, 0, 0);
        row.Children.Add(chevron);
        var capsule = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 8, 12, 8), Child = row };
        capsule.SetResourceReference(Border.BackgroundProperty, ThemeResources.Fill);

        Button? button = null;
        button = Ui.Bare(capsule, () =>
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        });
        button.VerticalAlignment = VerticalAlignment.Center;
        // Parenting the menu to the button lets the panel see it open, so it doesn't fold away meanwhile.
        button.ContextMenu = menu;
        _engine.Content = button;
    }

    private void Submit()
    {
        var text = _query.Text.Trim();
        if (text.Length == 0) return;
        LinkParsing.Open(LinkParsing.Url(text)?.OriginalString ?? _store.Engine.Url(text));
        _query.Clear();
    }

    internal static void Open(SavedSite site) =>
        LinkParsing.Open(LinkParsing.Url(site.Url)?.OriginalString ?? site.Url);

    internal void Remove(SavedSite site) => _store.Remove(site);

    // MARK: - Editor

    internal void Edit(SavedSite site, bool isNew)
    {
        if (_editor != null) return;
        var editor = new SiteEditor(site.Copy(), isNew, saved =>
        {
            if (saved != null) _store.Upsert(saved);
            CloseEditor();
        });
        _editor = editor;
        _overlay.Children.Add(editor);
        _grid.IsHitTestVisible = false;
        _grid.Animate(OpacityProperty, 0.25, Motion.ExpoOut(0.15));
        editor.Opacity = 0;
        var scale = new ScaleTransform(0.97, 0.97);
        editor.RenderTransformOrigin = new Point(0.5, 0.5);
        editor.RenderTransform = scale;
        editor.Animate(OpacityProperty, 1, Motion.ExpoOut(0.15));
        scale.Animate(ScaleTransform.ScaleXProperty, 1, Motion.ExpoOut(0.15));
        scale.Animate(ScaleTransform.ScaleYProperty, 1, Motion.ExpoOut(0.15));
    }

    private void CloseEditor()
    {
        if (_editor is not { } editor) return;
        _editor = null;
        editor.IsHitTestVisible = false;
        _grid.IsHitTestVisible = true;
        _grid.Animate(OpacityProperty, 1, Motion.ExpoOut(0.15));
        editor.Animate(OpacityProperty, 0, Motion.ExpoOut(0.15), completed: () => _overlay.Children.Remove(editor));
        if (editor.RenderTransform is ScaleTransform scale)
        {
            scale.Animate(ScaleTransform.ScaleXProperty, 0.97, Motion.ExpoOut(0.15));
            scale.Animate(ScaleTransform.ScaleYProperty, 0.97, Motion.ExpoOut(0.15));
        }
    }

    /// Rounded text field with an icon and a placeholder, as in the Mac app's search fields.
    /// Unlike <see cref="Ui.SearchField"/>, the icon and the placeholder can change later.
    internal static Border Field(string placeholder, string glyph, out TextBox box, out TextBlock icon, out TextBlock hint)
    {
        var input = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
        };
        input.SetResourceReference(Control.ForegroundProperty, ThemeResources.Text);
        input.SetResourceReference(TextBoxBase.CaretBrushProperty, ThemeResources.Text);
        input.SetResourceReference(TextBoxBase.SelectionBrushProperty, ThemeResources.Accent);

        var placeholderText = Ui.Text(placeholder, 13, ThemeResources.SecondaryText);
        placeholderText.IsHitTestVisible = false;
        placeholderText.TextWrapping = TextWrapping.NoWrap;
        placeholderText.TextTrimming = TextTrimming.CharacterEllipsis;
        input.TextChanged += (_, _) =>
            placeholderText.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var field = new Grid();
        field.Children.Add(placeholderText);
        field.Children.Add(input);

        var row = new DockPanel();
        var glyphText = Ui.Glyph(glyph, 12, ThemeResources.SecondaryText);
        glyphText.Width = 14;
        glyphText.TextAlignment = TextAlignment.Center;
        glyphText.Margin = new Thickness(0, 0, 8, 0);
        SetDock(glyphText, Dock.Left);
        row.Children.Add(glyphText);
        row.Children.Add(field);

        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 0, 10, 0),
            Height = 34,
            BorderThickness = new Thickness(0.8),
            Child = row,
            Cursor = Cursors.IBeam,
        };
        border.SetResourceReference(Border.BackgroundProperty, ThemeResources.Surface);
        border.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Hairline);
        border.MouseLeftButtonDown += (_, _) => input.Focus();
        box = input;
        icon = glyphText;
        hint = placeholderText;
        return border;
    }
}

/// A saved site: favicon (or its first letter), title and host. Hover shows edit and remove.
internal sealed class SiteTile : Grid
{
    private readonly Border _surface;
    private readonly Border _hoverStroke;
    private readonly StackPanel _actions = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(5),
        Visibility = Visibility.Collapsed,
    };

    public SiteTile(SavedSite site, LinksModule module)
    {
        MaxWidth = 140;
        Margin = new Thickness(4);

        var badge = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(11) };
        badge.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
        var letter = Ui.Text(site.Title.Length > 0 ? site.Title[..1].ToUpperInvariant() : "", 16, ThemeResources.Accent, FontWeights.Bold);
        letter.HorizontalAlignment = HorizontalAlignment.Center;
        badge.Child = letter;
        _ = LoadIcon(badge, site.Host);

        var title = Ui.Text(site.Title.Length == 0 ? site.Host : site.Title, 12, weight: FontWeights.SemiBold);
        var host = Ui.Text(site.Host, 10, ThemeResources.SecondaryText);
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        badge.Margin = new Thickness(0, 0, 0, 7);
        badge.HorizontalAlignment = HorizontalAlignment.Center;
        content.Children.Add(badge);
        foreach (var line in new[] { title, host })
        {
            line.TextWrapping = TextWrapping.NoWrap;
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            line.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(line);
        }

        _surface = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(6, 10, 6, 10), Child = content };
        _surface.SetResourceReference(Border.BackgroundProperty, ThemeResources.Surface);
        var tile = Ui.Bare(_surface, () => LinksModule.Open(site), site.Url);
        tile.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Children.Add(tile);

        // Two outlines: the theme border at 60%, and the accent at 70% while the pointer is over the tile.
        var stroke = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(0.8), Opacity = 0.6, IsHitTestVisible = false };
        stroke.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);
        Children.Add(stroke);
        _hoverStroke = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(0.8),
            Opacity = 0.7,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _hoverStroke.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Accent);
        Children.Add(_hoverStroke);

        _actions.Children.Add(SmallButton("\uE70F", "Edit", () => module.Edit(site, isNew: false)));
        var remove = SmallButton("\uE711", "Remove", () => module.Remove(site));
        remove.Margin = new Thickness(3, 0, 0, 0);
        _actions.Children.Add(remove);
        Children.Add(_actions);

        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => SetHover(false);
        ContextMenu = Menu(site, module);
    }

    private void SetHover(bool hovering)
    {
        _surface.SetResourceReference(Border.BackgroundProperty, hovering ? ThemeResources.Hairline : ThemeResources.Surface);
        _hoverStroke.Visibility = hovering ? Visibility.Visible : Visibility.Collapsed;
        _actions.Visibility = hovering ? Visibility.Visible : Visibility.Collapsed;
    }

    private static async Task LoadIcon(Border badge, string host)
    {
        if (await Favicons.Get(host) is not { } source) return;
        var image = new Image { Source = source, Width = 26, Height = 26, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        badge.Child = image;
    }

    private static Button SmallButton(string glyph, string tooltip, Action action)
    {
        var circle = new Grid { Width = 18, Height = 18 };
        var fill = new Border { CornerRadius = new CornerRadius(9), Opacity = 0.85 };
        fill.SetResourceReference(Border.BackgroundProperty, ThemeResources.Background);
        circle.Children.Add(fill);
        var ring = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(0.6) };
        ring.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);
        circle.Children.Add(ring);
        var icon = Ui.Glyph(glyph, 8);
        icon.FontWeight = FontWeights.Bold;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        circle.Children.Add(icon);
        return Ui.Bare(circle, action, tooltip);
    }

    private static ContextMenu Menu(SavedSite site, LinksModule module)
    {
        var menu = new ContextMenu();
        void Add(string header, Action action)
        {
            var entry = new MenuItem { Header = header };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }
        Add("Open", () => LinksModule.Open(site));
        Add("Copy Link", () =>
        {
            try { System.Windows.Clipboard.SetText(site.Url); }
            catch (System.Runtime.InteropServices.ExternalException) { }
        });
        Add("Edit…", () => module.Edit(site, isNew: false));
        menu.Items.Add(new Separator());
        Add("Remove", () => module.Remove(site));
        return menu;
    }
}

/// The card for adding or editing a site, shown over the dimmed grid.
internal sealed class SiteEditor : Border
{
    private readonly SavedSite _site;
    private readonly Action<SavedSite?> _done;
    private readonly TextBox _title;
    private readonly TextBox _url;

    public SiteEditor(SavedSite site, bool isNew, Action<SavedSite?> done)
    {
        _site = site;
        _done = done;
        Width = 340;
        Padding = new Thickness(14);
        CornerRadius = new CornerRadius(16);
        BorderThickness = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(BackgroundProperty, ThemeResources.Background);
        SetResourceReference(BorderBrushProperty, ThemeResources.Border);
        Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.5, BlurRadius = 24, ShadowDepth = 4, Direction = 270 };

        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(isNew ? "Add site" : "Edit site", 14, weight: FontWeights.Bold));

        var titleField = LinksModule.Field("Title", "\uE8D2", out _title, out _, out _);
        titleField.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(titleField);
        var urlField = LinksModule.Field("example.com", "\uE71B", out _url, out _, out _);
        urlField.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(urlField);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Ui.Pill("Cancel", "\uE711", () => _done(null)));
        var save = Ui.Pill(isNew ? "Add" : "Save", "\uE73E", Save, prominent: true);
        save.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(save);
        stack.Children.Add(buttons);
        Child = stack;

        void Validate()
        {
            save.IsEnabled = LinkParsing.Url(_url.Text) != null;
            save.Opacity = save.IsEnabled ? 1 : 0.5;
        }
        _url.TextChanged += (_, _) => Validate();
        _url.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Save();
            e.Handled = true;
        };
        _title.Text = site.Title;
        _url.Text = site.Url;
        Validate();
        Loaded += (_, _) => _title.Focus();
    }

    private void Save()
    {
        if (LinkParsing.Url(_url.Text) is not { } url) return;
        var result = _site.Copy();
        result.Url = url.OriginalString;
        result.Title = _title.Text.Trim().Length == 0
            ? (url.Host.Length > 0 ? url.Host : "Site").Replace("www.", "")
            : _title.Text;
        _done(result);
    }
}
