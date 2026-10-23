using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch.Features.Units;

/// The category and the two units last used, kept in %APPDATA%\Purrch\units.json.
public sealed class UnitsPrefs
{
    public UnitCategory Category { get; set; } = UnitCategory.Length;
    public string From { get; set; } = "Kilometers";
    public string To { get; set; } = "Miles";

    public static UnitsPrefs Shared { get; } = Store.Load<UnitsPrefs>("units");

    public void Save() => Store.Save("units", this);
}

/// Instant conversions: type "5 km to mi" or pick units, and see the value in every unit of the category.
public sealed class UnitsModule : DockPanel
{
    private static readonly Geometry DropGeometry = Geometry.Parse("M 6,0 C 6,0 0,7.5 0,11 A 6,6 0 0 0 12,11 C 12,7.5 6,0 6,0 Z");

    private readonly UnitsPrefs _prefs = UnitsPrefs.Shared;
    private readonly StackPanel _categoryBar = new() { Orientation = Orientation.Horizontal };
    private readonly ContentControl _fromPicker = new();
    private readonly ContentControl _toPicker = new();
    private readonly ContentControl _result = new();
    private readonly ContentControl _allUnits = new();
    private readonly TextBox _input;
    private string _text = "1";

    public UnitsModule()
    {
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _categoryBar,
            Margin = new Thickness(0, 0, 0, 8),
        };
        // The wheel scrolls the bar sideways.
        scroll.PreviewMouseWheel += (_, e) =>
        {
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        };
        SetDock(scroll, Dock.Top);
        Children.Add(scroll);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(Converter(out _input));
        Grid.SetColumn(_allUnits, 2);
        body.Children.Add(_allUnits);
        Children.Add(body);

        // Setting the text runs the first refresh through the field's change handler.
        _input.Text = _text;
    }

    private UnitCategory Category => _prefs.Category;

    private UnitDef FromUnit
    {
        get
        {
            var units = Category.Units();
            return units.FirstOrDefault(u => u.Name == _prefs.From) ?? units[0];
        }
    }

    private UnitDef ToUnit
    {
        get
        {
            var units = Category.Units();
            return units.FirstOrDefault(u => u.Name == _prefs.To) ?? units[Math.Min(1, units.Count - 1)];
        }
    }

    private double? Value => UnitCatalog.Parse(_text)?.Value;

    private double ConvertTo(double value, UnitDef unit) => UnitCatalog.Convert(value, FromUnit, unit);

    private void Refresh()
    {
        RefreshCategories();
        _fromPicker.Content = UnitPicker(FromUnit, name => _prefs.From = name);
        _toPicker.Content = UnitPicker(ToUnit, name => _prefs.To = name);
        RefreshResult();
        RefreshAllUnits();
    }

    // MARK: - Category bar

    private void RefreshCategories()
    {
        _categoryBar.Children.Clear();
        foreach (var item in UnitCategoryInfo.All)
        {
            var active = item == Category;
            var foreground = active ? ThemeResources.Background : ThemeResources.Text;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Icon(item, 10, foreground);
            icon.Margin = new Thickness(0, 0, 4, 0);
            row.Children.Add(icon);
            row.Children.Add(Ui.Text(item.Title(), 11, foreground, FontWeights.SemiBold));
            if (!active) row.Opacity = 0.85;

            var pill = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 0, 5, 0),
                Child = row,
            };
            pill.SetResourceReference(Border.BackgroundProperty, active ? ThemeResources.Accent : ThemeResources.Hairline);
            var choice = item;
            _categoryBar.Children.Add(Ui.Bare(pill, () => Select(choice)));
        }
    }

    /// The category's glyph, or a drawn drop for Volume, which Segoe has no glyph for.
    private static FrameworkElement Icon(UnitCategory category, double size, string colour)
    {
        if (category != UnitCategory.Volume) return Ui.Glyph(category.Glyph(), size, colour);
        var path = new Path { Data = DropGeometry, Stretch = Stretch.Uniform, Height = size, VerticalAlignment = VerticalAlignment.Center };
        path.SetResourceReference(Shape.FillProperty, colour);
        return path;
    }

    private void Select(UnitCategory category)
    {
        if (category == Category) return;
        _prefs.Category = category;
        (_prefs.From, _prefs.To) = category.DefaultPair();
        _prefs.Save();
        Refresh();
    }

    // MARK: - Converter

    private Border Converter(out TextBox input)
    {
        var field = Links.LinksModule.Field("5 km to mi", "\uE8EF", out input, out _, out _);
        var box = input;
        box.TextChanged += (_, _) =>
        {
            _text = box.Text;
            ApplyParsed(_text);
            Refresh();
        };
        DockPanel.SetDock(field, Dock.Top);

        var pickers = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        pickers.ColumnDefinitions.Add(new ColumnDefinition());
        pickers.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pickers.ColumnDefinitions.Add(new ColumnDefinition());
        pickers.Children.Add(_fromPicker);
        var swap = Ui.IconButton("\uE8AB", 26, () =>
        {
            var old = _prefs.From;
            _prefs.From = ToUnit.Name;
            _prefs.To = old;
            _prefs.Save();
            Refresh();
        }, tooltip: "Swap");
        swap.Margin = new Thickness(6, 0, 6, 0);
        Grid.SetColumn(swap, 1);
        pickers.Children.Add(swap);
        Grid.SetColumn(_toPicker, 2);
        pickers.Children.Add(_toPicker);
        DockPanel.SetDock(pickers, Dock.Top);

        DockPanel.SetDock(_result, Dock.Bottom);
        var stack = new DockPanel();
        stack.Children.Add(field);
        stack.Children.Add(pickers);
        stack.Children.Add(_result);
        stack.Children.Add(new Border()); // the space between the pickers and the result

        var frame = new Border
        {
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12),
            BorderThickness = new Thickness(AppSettings.Shared.Theme.Glow ? 1.2 : 0.8),
            Child = stack,
        };
        frame.SetResourceReference(Border.BackgroundProperty, ThemeResources.Surface);
        frame.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);
        return frame;
    }

    /// A capsule showing "Kilometers (km)" that opens a menu of the category's units.
    private Button UnitPicker(UnitDef selected, Action<string> choose)
    {
        var menu = new ContextMenu { MaxHeight = 360 };
        foreach (var def in Category.Units())
        {
            var name = def.Name;
            var item = new MenuItem { Header = $"{def.Name} ({def.Symbol})", IsCheckable = true, IsChecked = def == selected };
            item.Click += (_, _) =>
            {
                choose(name);
                _prefs.Save();
                Refresh();
            };
            menu.Items.Add(item);
        }

        var row = new DockPanel();
        var chevron = Ui.Glyph("\uE70D", 8, ThemeResources.SecondaryText);
        chevron.Margin = new Thickness(6, 1, 0, 0);
        DockPanel.SetDock(chevron, Dock.Right);
        row.Children.Add(chevron);
        var label = Ui.Text($"{selected.Name} ({selected.Symbol})", 12, weight: FontWeights.Medium);
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(label);

        var capsule = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 5, 8, 5), Child = row };
        capsule.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);

        Button? button = null;
        button = Ui.Bare(capsule, () =>
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        });
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        // Parenting the menu to the button lets the panel see it open, so it doesn't fold away meanwhile.
        button.ContextMenu = menu;
        return button;
    }

    private void RefreshResult()
    {
        var stack = new StackPanel();
        if (Value is { } value)
        {
            var from = Ui.Text($"{UnitCatalog.Format(value)} {FromUnit.Symbol} =", 12, ThemeResources.SecondaryText, FontWeights.Medium);
            from.TextWrapping = TextWrapping.NoWrap;
            from.TextTrimming = TextTrimming.CharacterEllipsis;
            from.Margin = new Thickness(0, 0, 0, 2);
            stack.Children.Add(from);

            var text = UnitCatalog.Format(ConvertTo(value, ToUnit));
            // Selectable like the Mac's .textSelection(.enabled), and still copied whole by a plain click.
            var number = Ui.Input(text, 38, weight: FontWeights.Bold);
            number.IsReadOnly = true;
            number.IsReadOnlyCaretVisible = false;
            number.SetResourceReference(Control.ForegroundProperty, ThemeResources.Accent);
            System.Windows.Documents.Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
            if (AppSettings.Shared.Theme.Glow && TryFindResource(ThemeResources.Accent) is SolidColorBrush accent)
                number.Effect = ExpandedView.Glow(accent.Color, 4);
            var symbol = Ui.Text(ToUnit.Symbol, 16, ThemeResources.SecondaryText, FontWeights.SemiBold);
            symbol.TextWrapping = TextWrapping.NoWrap;
            symbol.Margin = new Thickness(6, 0, 0, 0);
            symbol.VerticalAlignment = VerticalAlignment.Bottom;
            symbol.Padding = new Thickness(0, 0, 0, 7);
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(number);
            line.Children.Add(symbol);
            // Long numbers shrink to fit rather than being cut off, down to about 40%.
            var fit = new Viewbox
            {
                Child = line,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            stack.Children.Add(CopyOnClick(fit, text, number));
        }
        else
        {
            stack.Children.Add(Ui.Text("Type a number, or something like “100 f in c”.", 12, ThemeResources.SecondaryText));
        }
        _result.Content = stack;
    }

    // MARK: - All units

    private void RefreshAllUnits()
    {
        UIElement content;
        if (Value is { } value)
        {
            var list = new StackPanel();
            foreach (var def in Category.Units())
            {
                var text = UnitCatalog.Format(ConvertTo(value, def));
                var row = new DockPanel();
                var symbol = Ui.Text(def.Symbol, 11, ThemeResources.SecondaryText);
                symbol.Width = 44;
                symbol.Margin = new Thickness(8, 0, 0, 0);
                symbol.TextWrapping = TextWrapping.NoWrap;
                DockPanel.SetDock(symbol, Dock.Right);
                row.Children.Add(symbol);
                var number = Ui.Text(text, 13, def == ToUnit ? ThemeResources.Accent : ThemeResources.Text, FontWeights.SemiBold).Tabular();
                number.TextWrapping = TextWrapping.NoWrap;
                DockPanel.SetDock(number, Dock.Right);
                row.Children.Add(number);
                var name = Ui.Text(def.Name, 12, ThemeResources.SecondaryText);
                name.TextWrapping = TextWrapping.NoWrap;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                row.Children.Add(name);

                var cell = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 3, 6, 3),
                    Margin = new Thickness(0, 0, 0, 2),
                    Background = Brushes.Transparent,
                    Child = row,
                };
                if (def == FromUnit) cell.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
                list.Children.Add(CopyOnClick(cell, text));
            }
            content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = list };
        }
        else
        {
            content = EnterValue();
        }
        _allUnits.Content = new Card($"All {Category.Title().ToLowerInvariant()} units",
            Icon(Category, 10.5, ThemeResources.Accent), content);
    }

    private UIElement EnterValue()
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = Icon(Category, 26, ThemeResources.SecondaryText);
        icon.Opacity = 0.7;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(icon);
        var title = Ui.Text("Enter a value", 13, weight: FontWeights.SemiBold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(title);
        return stack;
    }

    /// <paramref name="selectable"/>: text inside that can be selected; dragging across it selects
    /// instead of copying everything.
    private static FrameworkElement CopyOnClick(FrameworkElement element, string text, TextBox? selectable = null)
    {
        element.Cursor = Cursors.Hand;
        element.ToolTip = "Click to copy";
        // Preview: a text box keeps the plain button-up event to itself.
        element.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (selectable is { SelectionLength: > 0 }) return;
            try { System.Windows.Clipboard.SetText(text); }
            catch (System.Runtime.InteropServices.ExternalException) { }
        };
        return element;
    }

    // MARK: - Typed queries

    /// "100 f in c" switches to Temperature and picks Fahrenheit and Celsius.
    private void ApplyParsed(string text)
    {
        if (UnitCatalog.Parse(text) is not { } query || (query.From ?? query.To) is not { } from) return;
        if (from.Category != Category) _prefs.Category = from.Category;
        if (query.From is { } f) _prefs.From = f.Unit.Name;
        var units = Category.Units();
        if (query.To is { } t)
            _prefs.To = t.Unit.Name;
        else if (_prefs.To == _prefs.From || units.All(u => u.Name != _prefs.To))
            _prefs.To = units.FirstOrDefault(u => u.Name != _prefs.From)?.Name ?? _prefs.To;
        _prefs.Save();
    }
}
