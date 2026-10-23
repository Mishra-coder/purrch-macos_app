using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch.Features.Media;

/// Message: type a line (or pick a preset) and it scrolls across the closed pill as dot-matrix text.
public sealed class MessageModule : Grid
{
    private static readonly string[] Presets = { "On a call", "Focus mode", "Back in 5", "Recording \U0001F534", "Ship it \U0001F680" };

    private readonly MessageService _message = MessageService.Shared;
    private readonly TextBox _input;
    private readonly TextBlock _count = Ui.Text("", 11, ThemeResources.SecondaryText).Tabular();
    private readonly WrapPanel _chips = new();
    private readonly ToggleButton _switch;
    private readonly Border _preview;
    private readonly DotMatrixMarquee _marquee = new() { Height = 22, Margin = new Thickness(22, 0, 22, 0) };
    private readonly bool _glow = AppSettings.Shared.Theme.Glow;

    public MessageModule()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        ColumnDefinitions.Add(new ColumnDefinition());

        var field = Ui.SearchField("Type a message for the pill", text => _message.Text = text, out _input, glyph: "\uE8BD");
        _input.MaxLength = MessageService.MaxLength;
        _input.Text = _message.Text;
        _switch = Ui.Switch(_message.Enabled, on => _message.Enabled = on, _message.HasText);

        var editor = new DockPanel();
        DockPanel.SetDock(field, Dock.Top);
        editor.Children.Add(field);
        // Hair spaces stand in for the Mac label's wide letter spacing; WPF has no tracking.
        var presetsLabel = Ui.Text(string.Join("\u200A", "QUICK PRESETS".ToCharArray()), 10, ThemeResources.SecondaryText, FontWeights.SemiBold);
        presetsLabel.Margin = new Thickness(0, 12, 0, 8);
        DockPanel.SetDock(presetsLabel, Dock.Top);
        editor.Children.Add(presetsLabel);
        DockPanel.SetDock(_chips, Dock.Top);
        editor.Children.Add(_chips);
        var toggleRow = ToggleRow();
        DockPanel.SetDock(toggleRow, Dock.Bottom);
        editor.Children.Add(toggleRow);
        editor.Children.Add(new Border());
        Add(new Card("Message", "\uE8BD", editor, _count), 0);

        _preview = Preview();
        var info = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var infoGlyph = Ui.Glyph("\uE946", 11, ThemeResources.SecondaryText);
        infoGlyph.VerticalAlignment = VerticalAlignment.Top;
        infoGlyph.Margin = new Thickness(0, 2, 6, 0);
        DockPanel.SetDock(infoGlyph, Dock.Left);
        info.Children.Add(infoGlyph);
        info.Children.Add(Ui.Text("Your message scrolls across the closed pill as glowing dot-matrix text, so people around you (and you) can see it at a glance.",
            11, ThemeResources.SecondaryText));
        var previewBody = new DockPanel();
        DockPanel.SetDock(info, Dock.Bottom);
        previewBody.Children.Add(info);
        previewBody.Children.Add(_preview);
        Add(new Card("Preview", "\uE890", previewBody), 2);

        Refresh();
        // Only listen while on screen; the service outlives this view.
        Loaded += (_, _) => { _message.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => _message.Changed -= Refresh;
    }

    private void Add(UIElement element, int column)
    {
        SetColumn(element, column);
        Children.Add(element);
    }

    private UIElement ToggleRow()
    {
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(_switch, Dock.Right);
        row.Children.Add(_switch);
        var labels = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        labels.Children.Add(Ui.Text("Show in pill", 13, weight: FontWeights.SemiBold));
        var hint = Ui.Text("Takes priority over timers and music while on.", 11, ThemeResources.SecondaryText);
        hint.Margin = new Thickness(0, 2, 0, 0);
        labels.Children.Add(hint);
        row.Children.Add(labels);
        return row;
    }

    /// A black capsule like the closed pill, with the message running across it.
    private Border Preview()
    {
        var capsule = new Border
        {
            Width = 300,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Background = Brushes.Black,
            BorderThickness = new Thickness(0.8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _marquee,
        };
        capsule.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);
        return capsule;
    }

    private void Refresh()
    {
        // Typing in the box already set the text; only presets need to put theirs in the box.
        if (_input.Text != _message.Text)
        {
            _input.Text = _message.Text;
            _input.CaretIndex = _input.Text.Length;
        }
        _count.Text = $"{_message.Text.Length}/{MessageService.MaxLength}";
        _switch.IsEnabled = _message.HasText;
        if (_switch.IsChecked != _message.Enabled) _switch.IsChecked = _message.Enabled;

        _marquee.Text = _message.Text.Length == 0 ? "Type something" : _message.Text;
        _marquee.Opacity = _message.Enabled ? 1 : 0.45;
        _preview.Effect = _message.Enabled && _glow
            ? new DropShadowEffect
            {
                Color = AppSettings.Shared.Theme.AccentFor(ModuleId.Message),
                BlurRadius = 20,
                ShadowDepth = 0,
                Opacity = 0.35,
            }
            : null;
        RebuildChips();
    }

    private void RebuildChips()
    {
        _chips.Children.Clear();
        foreach (var preset in Presets) _chips.Children.Add(Chip(preset, preset == _message.Text));
    }

    private Border Chip(string title, bool selected)
    {
        var label = new global::Emoji.Wpf.TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, selected ? ThemeResources.Background : ThemeResources.Text);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(11, 5, 11, 5),
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = Cursors.Hand,
            Child = label,
        };
        chip.SetResourceReference(Border.BackgroundProperty, selected ? ThemeResources.Accent : ThemeResources.Fill);
        chip.MouseLeftButtonUp += (_, _) =>
        {
            _message.Text = title;
            _message.Enabled = true;
        };
        return chip;
    }
}
