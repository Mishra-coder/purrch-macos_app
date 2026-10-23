using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Purrch.Core;
using Purrch.DesignSystem;

namespace Purrch.Interface;

/// Settings: a section list on the left, the section's rows on the right.
public sealed class SettingsView : Grid
{
    public enum Section { Appearance, Behaviour, Modules, AI, Integrations, About }

    /// Remembered across openings, like the Mac app's @State while the panel lives.
    private static Section _section = Section.Appearance;

    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly StackPanel _nav = new();
    private readonly StackPanel _content = new();

    public SettingsView()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        ColumnDefinitions.Add(new ColumnDefinition());

        var navColumn = new DockPanel();
        var quit = Ui.Pill("Quit Purrch", "\uE7E8", () => Application.Current.Shutdown());
        DockPanel.SetDock(quit, Dock.Bottom);
        navColumn.Children.Add(quit);
        navColumn.Children.Add(_nav);
        Children.Add(navColumn);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = _content,
            Padding = new Thickness(0, 0, 6, 0),
        };
        SetColumn(scroll, 2);
        Children.Add(scroll);

        Show(_section);
        // Reset order changes the list under the toggles; listen only while on screen.
        Loaded += (_, _) => _settings.PropertyChanged += OnSettingsChanged;
        Unloaded += (_, _) => _settings.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ModuleOrder) && _section == Section.Modules) Show(Section.Modules);
    }

    private static string Glyph(Section s) => s switch
    {
        Section.Appearance => "\uE790",
        Section.Behaviour => "\uE962",
        Section.Modules => "\uF0E2",
        Section.AI => "\uE945",
        Section.Integrations => "\uE8D7",
        _ => "\uE946",
    };

    private void Show(Section section)
    {
        _section = section;
        BuildNav();
        _content.Children.Clear();
        switch (section)
        {
            case Section.Appearance:
                Add(Heading("Theme"));
                Add(new ThemePicker());
                break;
            case Section.Behaviour:
                Add(Toggle("Open on hover", "Off means click the pill to open it.",
                    _settings.OpenOnHover, v => _settings.OpenOnHover = v));
                Add(Toggle("Hide in full screen", "Get out of the way of full-screen apps, games and videos.",
                    _settings.HideInFullscreen, v => _settings.HideInFullscreen = v));
                Add(Toggle("Live activities", "Music, timers and downloads in the closed pill.",
                    _settings.ShowLiveActivities, v => _settings.ShowLiveActivities = v));
                Add(Toggle("Launch at startup", "Start Purrch automatically when you sign in to Windows.",
                    _settings.LaunchAtStartup, v => _settings.LaunchAtStartup = v));
                break;
            case Section.Modules:
                BuildModules();
                break;
            case Section.AI:
                BuildAi();
                break;
            case Section.Integrations:
                BuildIntegrations();
                break;
            case Section.About:
                BuildAbout();
                break;
        }
    }

    private void BuildNav()
    {
        _nav.Children.Clear();
        foreach (var s in Enum.GetValues<Section>())
        {
            var active = s == _section;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Ui.Glyph(Glyph(s), 12, active ? ThemeResources.Text : ThemeResources.SecondaryText));
            var label = Ui.Text(s.ToString(), 12, active ? ThemeResources.Text : ThemeResources.SecondaryText,
                active ? FontWeights.SemiBold : FontWeights.Normal);
            label.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(label);

            var pill = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6), Child = row, Margin = new Thickness(0, 0, 0, 2) };
            if (active) pill.SetResourceReference(Border.BackgroundProperty, ThemeResources.AccentSoft);
            else pill.Background = Brushes.Transparent;

            var button = new Button
            {
                Content = pill,
                Style = (Style)Application.Current.FindResource("Purrch.PlainButton"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            var section = s;
            button.Click += (_, _) => Show(section);
            _nav.Children.Add(button);
        }
    }

    private void BuildModules()
    {
        var header = new DockPanel();
        var reset = Ui.Pill("Reset order", "\uE72C", () => _settings.ResetModuleOrder());
        DockPanel.SetDock(reset, Dock.Right);
        header.Children.Add(reset);
        header.Children.Add(Ui.Text("Turn tools on or off. Drag icons in the tab bar to reorder them.", 12, ThemeResources.SecondaryText));
        Add(header);

        var grid = new UniformGrid { Columns = 3 };
        foreach (var module in _settings.ModuleOrder)
        {
            var row = new DockPanel();
            var toggle = Ui.Switch(_settings.IsEnabled(module), on => _settings.SetEnabled(module, on), enabled: module != ModuleId.Home);
            DockPanel.SetDock(toggle, Dock.Right);
            row.Children.Add(toggle);

            var glyph = Ui.Glyph(module.Glyph(), 12);
            glyph.Foreground = _settings.Theme.AccentFor(module).Brush();
            glyph.Width = 18;
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);

            var title = Ui.Text(module.Title(), 12);
            title.TextWrapping = TextWrapping.NoWrap;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Margin = new Thickness(8, 0, 6, 0);
            row.Children.Add(title);

            var card = Ui.Row(row, 9, new Thickness(10, 6, 10, 6));
            card.Margin = new Thickness(0, 0, 8, 8);
            grid.Children.Add(card);
        }
        Add(grid);
    }

    private void BuildAi()
    {
        Add(Heading("Model"));
        var providers = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var provider in Enum.GetValues<AiProvider>())
        {
            var p = provider;
            var pill = Ui.Pill(provider.Title(), _settings.AiProvider == provider ? "\uE73E" : "\uE945",
                () => { _settings.AiProvider = p; Show(Section.AI); }, prominent: _settings.AiProvider == provider);
            pill.Margin = new Thickness(0, 0, 8, 0);
            providers.Children.Add(pill);
        }
        Add(providers);
        Add(Ui.Text("The Assistant and Translate use your own API key. Keys are kept in Windows Credential Manager, " +
                    "never in Purrch's files.", 11, ThemeResources.SecondaryText));
        Add(Secret("Anthropic API key (Claude)", AiProvider.Claude));
        Add(Secret("OpenAI API key", AiProvider.OpenAI));
        Add(Secret("Gemini API key (free at aistudio.google.com/apikey)", AiProvider.Gemini));
        Add(Toggle("Web search", "Let Claude search the web when it needs fresh information.",
            _settings.AssistantWebSearch, v => _settings.AssistantWebSearch = v));
    }

    private void BuildIntegrations()
    {
        Add(Heading("Weather"));
        Add(Plain("City (blank = use location)", _settings.WeatherCity, v => _settings.WeatherCity = v));
        Add(Toggle("Fahrenheit", "", _settings.UseFahrenheit, v => _settings.UseFahrenheit = v));
    }

    /// A plain text field. Commits a moment after typing stops, so each key doesn't start a new lookup.
    private static UIElement Plain(string title, string value, Action<string> commit)
    {
        var box = Ui.Input(value);
        var save = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(0.6) };
        save.Tick += (_, _) =>
        {
            save.Stop();
            commit(box.Text.Trim());
        };
        box.TextChanged += (_, _) => { save.Stop(); save.Start(); };
        // Leaving the page (or the panel) shouldn't lose the last edit.
        box.Unloaded += (_, _) =>
        {
            if (!save.IsEnabled) return;
            save.Stop();
            commit(box.Text.Trim());
        };

        var frame = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 7, 8, 7),
            BorderThickness = new Thickness(0.6),
            Child = box,
            Margin = new Thickness(0, 4, 0, 0),
        };
        frame.SetResourceReference(Border.BackgroundProperty, ThemeResources.Surface);
        frame.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);

        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(title, 11, ThemeResources.SecondaryText));
        stack.Children.Add(frame);
        return stack;
    }

    /// A masked key field. Saves to Credential Manager a moment after typing stops; clearing it deletes the key.
    private static UIElement Secret(string title, AiProvider provider)
    {
        var box = new PasswordBox
        {
            Password = SecretStore.Get(provider.SecretName()),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 13,
            Padding = new Thickness(0),
        };
        box.SetResourceReference(Control.ForegroundProperty, ThemeResources.Text);
        box.SetResourceReference(PasswordBox.CaretBrushProperty, ThemeResources.Text);
        var saved = Ui.Text("", 11, ThemeResources.SecondaryText);
        var save = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(0.6) };
        save.Tick += (_, _) =>
        {
            save.Stop();
            SecretStore.Set(provider.SecretName(), box.Password.Trim());
            saved.Text = box.Password.Length == 0 ? "Removed" : "Saved";
        };
        box.PasswordChanged += (_, _) => { saved.Text = ""; save.Stop(); save.Start(); };

        var field = new DockPanel();
        DockPanel.SetDock(saved, Dock.Right);
        field.Children.Add(saved);
        field.Children.Add(box);
        var frame = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 7, 8, 7),
            BorderThickness = new Thickness(0.6),
            Child = field,
            Margin = new Thickness(0, 4, 0, 0),
        };
        frame.SetResourceReference(Border.BackgroundProperty, ThemeResources.Surface);
        frame.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);

        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(title, 11, ThemeResources.SecondaryText));
        stack.Children.Add(frame);
        return stack;
    }

    private void BuildAbout()
    {
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/Mascot.png")),
            Width = 64,
            Height = 54,
            Stretch = Stretch.Uniform,
        });
        var names = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Ui.Text("Purrch", 16, weight: FontWeights.Bold));
        names.Children.Add(Ui.Text($"Version {Version} for Windows", 12, ThemeResources.SecondaryText));
        top.Children.Add(names);
        Add(top);

        Add(Ui.Text("A small control centre at the top of your screen. No account, no telemetry: your clipboard, " +
                    "notes and usage stats never leave this PC. API keys are kept in Windows Credential Manager.",
                    12, ThemeResources.SecondaryText));
        Add(Ui.Pill("Show welcome again", "\uE76E", () => _settings.HasCompletedOnboarding = false));
    }

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "dev" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private void Add(UIElement element)
    {
        if (element is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 0, 0, 14);
        _content.Children.Add(element);
    }

    private static TextBlock Heading(string text) => Ui.Text(text, 13, weight: FontWeights.SemiBold);

    private static Border Toggle(string title, string detail, bool value, Action<bool> changed)
    {
        var row = new DockPanel();
        var toggle = Ui.Switch(value, changed);
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);

        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(Ui.Text(title, 13));
        if (detail.Length > 0) text.Children.Add(Ui.Text(detail, 11, ThemeResources.SecondaryText));
        row.Children.Add(text);
        return Ui.Row(row);
    }
}
