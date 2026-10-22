using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
