using System.Windows;
using System.Windows.Media;
using Purrch.Core;

namespace Purrch.DesignSystem;

public enum ThemeId { Minimal, Playful, Molten, TechHud, Polished, BloodMoon }

/// Colours for the whole panel. Views read them as DynamicResource brushes (see <see cref="ThemeResources"/>).
public sealed record Theme(
    ThemeId Id,
    string Name,
    string Tagline,
    Color Background,
    Color Surface,
    Color Border,
    Color Accent,
    Color Text,
    Color SecondaryText,
    bool Glow)
{
    /// Accent for one module. The Playful theme gives each module its own colour.
    public Color AccentFor(ModuleId module) => Id == ThemeId.Playful ? module.PlayfulColor() : Accent;

    public static IReadOnlyList<ThemeId> All { get; } = Enum.GetValues<ThemeId>();

    public static Theme Make(ThemeId id) => id switch
    {
        ThemeId.Playful => new(id, "Playful", "A system colour per module",
            Colors.Black, White(0.1), White(0.2), Color.FromRgb(0x0A, 0x84, 0xFF),
            Colors.White, White(0.58), false),
        ThemeId.Molten => new(id, "Molten", "Glowing orange on black",
            Rgb(0.05, 0.025, 0.0), Rgb(0.12, 0.06, 0.02), Rgb(0.95, 0.5, 0.15, 0.75),
            Rgb(1.0, 0.55, 0.18), Colors.White, Rgb(0.75, 0.62, 0.52), true),
        ThemeId.TechHud => new(id, "Tech HUD", "Cyan on deep teal",
            Rgb(0.0, 0.06, 0.07), Rgb(0.0, 0.12, 0.13), Rgb(0.2, 0.9, 0.95, 0.55),
            Rgb(0.25, 0.95, 1.0), Rgb(0.85, 1.0, 1.0), Rgb(0.45, 0.7, 0.72), true),
        ThemeId.Polished => new(id, "Polished", "Dark green with gold",
            Rgb(0.02, 0.07, 0.05), Rgb(0.05, 0.14, 0.1), Rgb(0.85, 0.7, 0.35, 0.5),
            Rgb(0.92, 0.76, 0.4), Rgb(0.97, 0.94, 0.85), Rgb(0.6, 0.66, 0.58), false),
        ThemeId.BloodMoon => new(id, "Blood Moon", "Oxblood borders, warm highlights",
            Rgb(0.06, 0.01, 0.015), Rgb(0.13, 0.03, 0.04), Rgb(0.55, 0.08, 0.12, 0.85),
            Rgb(0.95, 0.35, 0.3), Rgb(1.0, 0.93, 0.9), Rgb(0.7, 0.5, 0.48), true),
        _ => new(ThemeId.Minimal, "Minimal", "Black and white, nothing else",
            Colors.Black, White(0.09), White(0.2), Colors.White,
            Colors.White, White(0.55), false),
    };

    private static Color White(double v) => Rgb(v, v, v);

    private static Color Rgb(double r, double g, double b, double a = 1) =>
        Color.FromArgb(Byte(a), Byte(r), Byte(g), Byte(b));

    private static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
}

public static class ColorExtensions
{
    public static Color WithOpacity(this Color c, double opacity) =>
        Color.FromArgb((byte)Math.Round(c.A * Math.Clamp(opacity, 0, 1)), c.R, c.G, c.B);

    public static SolidColorBrush Brush(this Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}

/// Publishes the current theme as application resources, so XAML can use
/// {DynamicResource Purrch.Text} and recolour instantly when the theme changes.
public static class ThemeResources
{
    public const string Background = "Purrch.Background";
    public const string Surface = "Purrch.Surface";
    public const string Border = "Purrch.Border";
    public const string Text = "Purrch.Text";
    public const string SecondaryText = "Purrch.SecondaryText";
    /// Accent of what is on screen: the selected module's tint in Playful, otherwise the theme accent.
    public const string Accent = "Purrch.Accent";
    /// The same accent at low opacity, for selection fills and badges.
    public const string AccentSoft = "Purrch.AccentSoft";
    /// The theme's own accent, never tinted per module.
    public const string ThemeAccent = "Purrch.ThemeAccent";
    /// Faint text-coloured fill for controls that sit on a surface.
    public const string Hairline = "Purrch.Hairline";
    /// Text colour at 10%: round buttons, ring tracks, unlit bars.
    public const string Fill = "Purrch.Fill";
    /// Accent at 16%, for the icon badge on cards.
    public const string AccentFaint = "Purrch.AccentFaint";

    public static void Apply(Theme theme, Color accent)
    {
        var r = Application.Current.Resources;
        r[Background] = theme.Background.Brush();
        r[Surface] = theme.Surface.Brush();
        r[Border] = theme.Border.Brush();
        r[Text] = theme.Text.Brush();
        r[SecondaryText] = theme.SecondaryText.Brush();
        r[Accent] = accent.Brush();
        r[AccentSoft] = accent.WithOpacity(theme.Glow ? 0.9 : 0.2).Brush();
        r[ThemeAccent] = theme.Accent.Brush();
        r[Hairline] = theme.Text.WithOpacity(0.08).Brush();
        r[Fill] = theme.Text.WithOpacity(0.1).Brush();
        r[AccentFaint] = accent.WithOpacity(0.16).Brush();
    }
}
