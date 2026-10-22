using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Purrch.Core;
using Purrch.DesignSystem;

namespace Purrch.Interface;

/// Six theme swatches, each drawn in its own colours; picking one recolours the panel immediately.
public sealed class ThemePicker : UniformGrid
{
    private readonly AppSettings _settings = AppSettings.Shared;

    public ThemePicker(int columns = 3)
    {
        Columns = columns;
        Build();
        // Only listen while on screen, so discarded pickers don't linger.
        Loaded += (_, _) => _settings.PropertyChanged += OnSettingsChanged;
        Unloaded += (_, _) => _settings.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ThemeId)) Build();
    }

    private void Build()
    {
        Children.Clear();
        foreach (var id in Theme.All) Children.Add(Swatch(Theme.Make(id)));
    }

    private UIElement Swatch(Theme t)
    {
        var selected = _settings.ThemeId == t.Id;
        var rings = new StackPanel { Orientation = Orientation.Horizontal };
        Color[] playful = { Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xFF, 0x37, 0x5F) };
        double[] fractions = { 0.75, 0.5, 0.9 };
        for (var i = 0; i < 3; i++)
        {
            var arc = Ui.Arc(22, fractions[i], t.Id == ThemeId.Playful ? playful[i] : t.Accent);
            arc.Margin = new Thickness(0, 0, 6, 0);
            if (t.Glow) arc.Effect = new DropShadowEffect { Color = t.Accent, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.6 };
            rings.Children.Add(arc);
        }

        var header = new DockPanel();
        if (selected)
        {
            var check = new TextBlock { Text = "\uE73E", FontSize = 13, Foreground = t.Accent.Brush(), Style = (Style)Application.Current.FindResource("Purrch.Glyph") };
            DockPanel.SetDock(check, Dock.Right);
            header.Children.Add(check);
        }
        header.Children.Add(rings);

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(new TextBlock { Text = t.Name, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = t.Text.Brush(), Margin = new Thickness(0, 8, 0, 0) });
        body.Children.Add(new TextBlock { Text = t.Tagline, FontSize = 10, Foreground = t.SecondaryText.Brush(), TextTrimming = TextTrimming.CharacterEllipsis });

        var card = new Border
        {
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(12),
            Background = t.Surface.Brush(),
            BorderBrush = (selected ? t.Accent : t.Border).Brush(),
            BorderThickness = new Thickness(selected ? 2 : 0.8),
            Child = body,
        };

        var button = new Button { Content = card, Style = (Style)Application.Current.FindResource("Purrch.PlainButton") };
        button.Click += (_, _) => _settings.ThemeId = t.Id;
        return button;
    }
}
