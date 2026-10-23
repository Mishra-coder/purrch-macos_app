using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Purrch.DesignSystem;

/// Small building blocks for views written in code: text, glyphs, pills, switches and cards.
/// Colours come from the theme resources, so everything recolours when the theme changes.
public static class Ui
{
    public static TextBlock Text(string text, double size = 12, string colour = ThemeResources.Text,
                                 FontWeight? weight = null)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, colour);
        return t;
    }

    public static TextBlock Glyph(string glyph, double size = 12, string colour = ThemeResources.Text)
    {
        var t = new TextBlock { Text = glyph, FontSize = size, Style = (Style)Application.Current.FindResource("Purrch.Glyph") };
        t.SetResourceReference(TextBlock.ForegroundProperty, colour);
        return t;
    }

    /// Capsule button with an icon and a title. Prominent ones are filled with the accent.
    public static Button Pill(string title, string glyph, Action onClick, bool prominent = false)
    {
        var foreground = prominent ? ThemeResources.Background : ThemeResources.Text;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Glyph(glyph, 11, foreground));
        var label = Text(title, 12, foreground, FontWeights.SemiBold);
        label.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(label);

        var capsule = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 6, 12, 6), Child = row };
        capsule.SetResourceReference(Border.BackgroundProperty, prominent ? ThemeResources.ThemeAccent : ThemeResources.Hairline);

        var button = new Button
        {
            Content = capsule,
            Style = (Style)Application.Current.FindResource("Purrch.PlainButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// Round icon button. Prominent ones are filled with the accent.
    public static Button IconButton(string glyph, double size, Action onClick, bool prominent = false, string? tooltip = null)
    {
        var circle = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Child = Glyph(glyph, size * 0.4, prominent ? ThemeResources.Background : ThemeResources.Text),
        };
        circle.SetResourceReference(Border.BackgroundProperty, prominent ? ThemeResources.Accent : ThemeResources.Fill);
        var button = new Button
        {
            Content = circle,
            Style = (Style)Application.Current.FindResource("Purrch.PlainButton"),
            ToolTip = tooltip,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// A bare glyph or label that acts as a button (steppers, plus and minus).
    public static Button Bare(UIElement content, Action onClick, string? tooltip = null)
    {
        var button = new Button
        {
            Content = content,
            Style = (Style)Application.Current.FindResource("Purrch.PlainButton"),
            ToolTip = tooltip,
            Background = Brushes.Transparent,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// Rounded search box with a magnifier (or another glyph) and a placeholder. Calls back on every change.
    public static Border SearchField(string placeholder, Action<string> changed, out TextBox box, string glyph = "\uE721")
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

        var hint = Text(placeholder, 13, ThemeResources.SecondaryText);
        hint.IsHitTestVisible = false;
        input.TextChanged += (_, _) =>
        {
            hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            changed(input.Text);
        };

        var field = new Grid();
        field.Children.Add(hint);
        field.Children.Add(input);

        var row = new DockPanel();
        var glass = Glyph(glyph, 12, ThemeResources.SecondaryText);
        glass.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(glass, Dock.Left);
        row.Children.Add(glass);
        row.Children.Add(field);

        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 0, 10, 0),
            Height = 34,
            BorderThickness = new Thickness(0.8),
            Child = row,
            Cursor = System.Windows.Input.Cursors.IBeam,
        };
        border.SetResourceReference(Border.BackgroundProperty, ThemeResources.Surface);
        border.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Hairline);
        border.MouseLeftButtonDown += (_, _) => input.Focus();
        box = input;
        return border;
    }

    /// Borderless themed text box, for titles, list items and editors.
    public static TextBox Input(string text, double size = 13, bool multiline = false, FontWeight? weight = null)
    {
        var box = new TextBox
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            AcceptsReturn = multiline,
            AcceptsTab = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled,
        };
        box.SetResourceReference(Control.ForegroundProperty, ThemeResources.Text);
        box.SetResourceReference(TextBoxBase.CaretBrushProperty, ThemeResources.Text);
        box.SetResourceReference(TextBoxBase.SelectionBrushProperty, ThemeResources.Accent);
        return box;
    }

    /// Read-only rich text that can be selected and copied, for answers and results; fill its Document with a
    /// zero-margin Paragraph. The mouse wheel goes on to the list or pane around it.
    public static RichTextBox SelectableText(double size)
    {
        var box = new RichTextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            FontSize = size,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Document = new FlowDocument { PagePadding = new Thickness(0) },
        };
        box.SetResourceReference(Control.ForegroundProperty, ThemeResources.Text);
        box.SetResourceReference(TextBoxBase.SelectionBrushProperty, ThemeResources.Accent);
        // The box's own scroller would swallow the wheel even though it never scrolls.
        box.PreviewMouseWheel += (_, e) =>
        {
            if (box.Parent is not UIElement parent) return;
            e.Handled = true;
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = box,
            });
        };
        return box;
    }

    /// One line of text that trims the middle of a long name to fit its width ("Screenshot 20…12.png"),
    /// so the start and the extension stay visible, like the Mac's truncationMode(.middle).
    public static TextBlock MiddleTrimmed(string text, double size = 12, string colour = ThemeResources.Text,
                                          FontWeight? weight = null)
    {
        var block = Text(text, size, colour, weight);
        block.TextWrapping = TextWrapping.NoWrap;
        // Stretched, the block is as wide as its slot whatever the text, so re-trimming can't loop.
        block.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged) block.Text = MiddleTrim(text, size, block.ActualWidth, block.FontWeight, block.FontFamily);
        };
        return block;
    }

    /// Cuts the middle out of a long name ("Screenshot 20…12.png") until it fits the width.
    public static string MiddleTrim(string text, double size, double width, FontWeight? weight = null, FontFamily? family = null)
    {
        var typeface = new Typeface(family ?? new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal);
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

    /// Lays a grey placeholder over a text box while it is empty.
    public static Grid WithPlaceholder(TextBox box, string placeholder, Thickness? inset = null)
    {
        var hint = Text(placeholder, box.FontSize, ThemeResources.SecondaryText);
        hint.IsHitTestVisible = false;
        hint.Opacity = 0.7;
        hint.VerticalAlignment = box.AcceptsReturn ? VerticalAlignment.Top : VerticalAlignment.Center;
        hint.Margin = inset ?? new Thickness(2, 0, 0, 0);
        hint.FontFamily = box.FontFamily;
        void Sync() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Sync();
        Sync();
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(hint);
        return grid;
    }

    /// Centred icon, title and hint for an empty list.
    public static UIElement EmptyState(string glyph, string title, string subtitle)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = Glyph(glyph, 26, ThemeResources.SecondaryText);
        icon.Opacity = 0.7;
        stack.Children.Add(icon);
        var heading = Text(title, 13, weight: FontWeights.SemiBold);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        heading.Margin = new Thickness(0, 10, 0, 2);
        stack.Children.Add(heading);
        var hint = Text(subtitle, 11.5, ThemeResources.SecondaryText);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.TextAlignment = TextAlignment.Center;
        stack.Children.Add(hint);
        return stack;
    }

    public static ToggleButton Switch(bool isOn, Action<bool> changed, bool enabled = true)
    {
        var toggle = new ToggleButton
        {
            IsChecked = isOn,
            IsEnabled = enabled,
            Style = (Style)Application.Current.FindResource("Purrch.Switch"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        toggle.Checked += (_, _) => changed(true);
        toggle.Unchecked += (_, _) => changed(false);
        return toggle;
    }

    /// A rounded surface row, as used in settings.
    public static Border Row(UIElement child, double radius = 10, Thickness? padding = null)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(radius),
            Padding = padding ?? new Thickness(10),
            Child = child,
        };
        border.SetResourceReference(Border.BackgroundProperty, ThemeResources.Surface);
        return border;
    }

    /// Partial ring, used by the theme swatches.
    public static Path Arc(double size, double fraction, Color colour, double thickness = 3)
    {
        var r = (size - thickness) / 2;
        var c = size / 2;
        var angle = Math.Clamp(fraction, 0, 0.999) * 2 * Math.PI;
        var figure = new PathFigure { StartPoint = new Point(c, c - r) };
        figure.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle)),
            new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise, true));
        return new Path
        {
            Data = new PathGeometry(new[] { figure }),
            Stroke = colour.Brush(),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = size,
            Height = size,
        };
    }
}
