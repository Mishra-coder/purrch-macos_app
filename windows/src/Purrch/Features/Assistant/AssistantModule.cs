using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch.Features.Assistant;

/// Chat with Claude, OpenAI, Gemini or a local model about anything to do with the PC.
public sealed partial class AssistantModule : DockPanel
{
    private static readonly string[] Suggestions =
        { "Why is my PC slow?", "Free up disk space", "Shortcut for emoji", "How do I show hidden files?" };
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private const string SendGlyph = "\uE724";
    private const string SendBusyGlyph = "\uE712";

    private readonly AssistantService _service = AssistantService.Shared;
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly ContentControl _header = new();
    private readonly ContentControl _body = new();
    private readonly StackPanel _transcript = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input = Ui.Input("", 13);
    private readonly Grid _inputBar;
    private Button _send = null!;

    public AssistantModule()
    {
        _header.Margin = new Thickness(0, 0, 0, 8);
        SetDock(_header, Dock.Top);
        Children.Add(_header);

        _inputBar = InputBar();
        _inputBar.Margin = new Thickness(0, 8, 0, 0);
        SetDock(_inputBar, Dock.Bottom);
        Children.Add(_inputBar);
        Children.Add(_body);

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _transcript };

        Refresh();
        Loaded += (_, _) =>
        {
            _service.Changed += Refresh;
            _settings.PropertyChanged += OnSettingsChanged;
            SecretStore.Changed += OnSecretChanged;
            Refresh();
            _input.Focus();
        };
        Unloaded += (_, _) =>
        {
            _service.Changed -= Refresh;
            _settings.PropertyChanged -= OnSettingsChanged;
            SecretStore.Changed -= OnSecretChanged;
        };
    }

    /// A key added or removed in Settings swaps the "needs an API key" screen and the chat.
    private void OnSecretChanged(string name)
    {
        if (name == _settings.AiProvider.SecretName()) Refresh();
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.AiProvider) or nameof(AppSettings.AssistantWebSearch)) Refresh();
    }

    private void Refresh()
    {
        _header.Content = Header();
        var provider = _settings.AiProvider;
        if (!provider.HasKey())
        {
            _inputBar.Visibility = Visibility.Collapsed;
            _body.Content = MissingKey(provider);
            return;
        }
        _inputBar.Visibility = Visibility.Visible;
        // While an answer is on its way the send button shows "…" and can't be pressed.
        _send.IsEnabled = !_service.IsThinking;
        _send.ToolTip = _service.IsThinking ? "Thinking…" : "Send";
        if (_send.Content is Border { Child: TextBlock glyph }) glyph.Text = _service.IsThinking ? SendBusyGlyph : SendGlyph;
        if (_service.Messages.Count == 0 && !_service.IsThinking)
        {
            _body.Content = EmptyChat();
            return;
        }

        _transcript.Children.Clear();
        foreach (var message in _service.Messages) _transcript.Children.Add(Bubble(message));
        if (_service.IsThinking) _transcript.Children.Add(TypingIndicator());
        _body.Content = _scroll;
        _scroll.ScrollToEnd();
    }

    // MARK: - Header

    private UIElement Header()
    {
        var row = new DockPanel();
        if (_service.Messages.Count > 0)
        {
            var clear = Ui.Pill("Clear chat", "\uE74D", _service.Clear);
            DockPanel.SetDock(clear, Dock.Right);
            row.Children.Add(clear);
        }

        var provider = _settings.AiProvider;
        var label = provider.Title() + (provider == AiProvider.Claude && _settings.AssistantWebSearch ? " · web" : "");
        var menu = new ContextMenu();
        foreach (var p in Enum.GetValues<AiProvider>())
        {
            var item = new MenuItem { Header = p.Title(), IsCheckable = true, IsChecked = p == provider };
            var choice = p;
            item.Click += (_, _) => _settings.AiProvider = choice;
            menu.Items.Add(item);
        }
        if (provider == AiProvider.Claude)
        {
            menu.Items.Add(new Separator());
            var web = new MenuItem { Header = "Web search", IsCheckable = true, IsChecked = _settings.AssistantWebSearch };
            web.Click += (_, _) => _settings.AssistantWebSearch = !_settings.AssistantWebSearch;
            menu.Items.Add(web);
        }
        var picker = Dropdown(label, menu);
        picker.HorizontalAlignment = HorizontalAlignment.Left;
        row.Children.Add(picker);
        return row;
    }

    /// A capsule that opens a menu when clicked.
    internal static Button Dropdown(string title, ContextMenu menu, bool dot = true)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (dot)
        {
            var mark = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0) };
            mark.SetResourceReference(Border.BackgroundProperty, ThemeResources.Accent);
            row.Children.Add(mark);
        }
        row.Children.Add(Ui.Text(title, 11, weight: FontWeights.SemiBold));
        var chevron = Ui.Glyph("\uE70D", 8);
        chevron.Margin = new Thickness(6, 1, 0, 0);
        row.Children.Add(chevron);

        var capsule = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 5, 10, 5),
            BorderThickness = new Thickness(0.7),
            Child = row,
        };
        capsule.SetResourceReference(Border.BackgroundProperty, ThemeResources.AccentFaint);
        capsule.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Accent);

        Button? button = null;
        button = Ui.Bare(capsule, () =>
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        });
        // Parenting the menu to the button lets the panel see it open, so it doesn't fold away meanwhile.
        button.ContextMenu = menu;
        return button;
    }

    // MARK: - Body

    private static UIElement MissingKey(AiProvider provider)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Ui.EmptyState("\uE8D7", $"{provider.Title()} needs an API key",
            "Add your key in Settings \u2192 AI, or switch to Local (on-device) or another provider above."));
        var open = Ui.Pill("Open Settings", "\uE713", () => SettingsView.Open(SettingsView.Section.AI), prominent: true);
        open.HorizontalAlignment = HorizontalAlignment.Center;
        open.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(open);
        return stack;
    }

    private UIElement EmptyChat()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Ui.EmptyState("\uE945", "Ask anything about your PC",
            "The assistant knows your Windows version, PC model, memory, storage and battery."));
        var chips = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var suggestion in Suggestions)
        {
            var text = suggestion;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(13),
                Padding = new Thickness(10, 5, 10, 6),
                BorderThickness = new Thickness(0.7),
                Margin = new Thickness(3),
                Child = Ui.Text(suggestion, 12, weight: FontWeights.Medium),
            };
            chip.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
            chip.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);
            chips.Children.Add(Ui.Bare(chip, () => _service.Send(text)));
        }
        stack.Children.Add(chips);
        return stack;
    }

    private Grid InputBar()
    {
        _input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            Submit();
            e.Handled = true;
        };
        var field = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 0, 10, 0),
            Height = 34,
            BorderThickness = new Thickness(0.8),
            Child = Ui.WithPlaceholder(_input, "Ask the assistant…"),
        };
        field.SetResourceReference(Border.BackgroundProperty, ThemeResources.Surface);
        field.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Hairline);

        _send = Ui.IconButton(SendGlyph, 32, Submit, prominent: true, tooltip: "Send");
        _send.Margin = new Thickness(8, 0, 0, 0);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(field);
        Grid.SetColumn(_send, 1);
        grid.Children.Add(_send);
        return grid;
    }

    private void Submit()
    {
        if (_service.IsThinking || _input.Text.Trim().Length == 0) return;
        _service.Send(_input.Text);
        _input.Text = "";
    }

    private static UIElement Bubble(ChatMessage message)
    {
        // The text is selectable, but a RichTextBox always fills its row. A hidden TextBlock with the same
        // text sizes the bubble instead, and the box takes the bubble's width (2px spare so it wraps the same).
        var measure = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Hidden,
            Margin = new Thickness(0, 0, 2, 0),
        };
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var text = Ui.SelectableText(13);
        text.Document.Blocks.Add(paragraph);
        var content = new Grid { Children = { measure, text } };
        text.SetBinding(WidthProperty, new Binding(nameof(ActualWidth)) { Source = content });
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(11, 7, 11, 7),
            Margin = new Thickness(0, 0, 0, 8),
            Child = content,
        };
        switch (message.Role)
        {
            case ChatRole.User:
                measure.Text = message.Text;
                paragraph.Inlines.Add(new Run(message.Text));
                text.SetResourceReference(Control.ForegroundProperty, ThemeResources.Background);
                // An accent selection would vanish on the accent bubble.
                text.SetResourceReference(TextBoxBase.SelectionBrushProperty, ThemeResources.Background);
                bubble.SetResourceReference(Border.BackgroundProperty, ThemeResources.Accent);
                bubble.HorizontalAlignment = HorizontalAlignment.Right;
                bubble.Margin = new Thickness(120, 0, 0, 8);
                break;
            case ChatRole.Assistant:
                AddMarkdown(measure.Inlines, message.Text);
                AddMarkdown(paragraph.Inlines, message.Text);
                bubble.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
                bubble.HorizontalAlignment = HorizontalAlignment.Left;
                bubble.Margin = new Thickness(0, 0, 80, 8);
                break;
            default:
                measure.Text = "⚠ " + message.Text;
                paragraph.Inlines.Add(new Run("⚠ " + message.Text));
                text.SetResourceReference(Control.ForegroundProperty, ThemeResources.SecondaryText);
                bubble.Background = Orange.WithOpacity(0.12).Brush();
                bubble.HorizontalAlignment = HorizontalAlignment.Left;
                bubble.Margin = new Thickness(0, 0, 80, 8);
                break;
        }
        // Right-click copies the selection, or the whole message when nothing is selected.
        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) =>
        {
            var selected = text.Selection.Text;
            try { System.Windows.Clipboard.SetText(selected.Length > 0 ? selected : message.Text); }
            catch (System.Runtime.InteropServices.ExternalException) { }
        };
        var menu = new ContextMenu { Items = { copy } };
        bubble.ContextMenu = menu;
        text.ContextMenu = menu;
        return bubble;
    }

    [GeneratedRegex(@"(\*\*[^*]+\*\*|`[^`]+`)")]
    private static partial Regex InlineMarkdown();

    /// Renders the two Markdown forms the assistant is asked to use: **bold** and `code`.
    private static void AddMarkdown(InlineCollection inlines, string text)
    {
        foreach (var part in InlineMarkdown().Split(text))
        {
            if (part.Length == 0) continue;
            if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
            {
                inlines.Add(new Bold(new Run(part[2..^2])));
            }
            else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2)
            {
                var code = new Run(part[1..^1]) { FontFamily = new FontFamily("Cascadia Mono, Consolas") };
                code.SetResourceReference(TextElement.BackgroundProperty, ThemeResources.Fill);
                inlines.Add(code);
            }
            else
            {
                inlines.Add(new Run(part));
            }
        }
    }

    private static UIElement TypingIndicator()
    {
        var dots = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < 3; i++)
        {
            var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0) };
            dot.SetResourceReference(Border.BackgroundProperty, ThemeResources.SecondaryText);
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromSeconds(0.6))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(i * 0.16),
            });
            dots.Children.Add(dot);
        }
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 10, 12, 10),
            Child = dots,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        bubble.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
        return bubble;
    }
}
