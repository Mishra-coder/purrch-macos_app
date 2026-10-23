using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Features.Assistant;
using Purrch.Interface;

namespace Purrch.Features.Translate;

/// Translate between languages (Hinglish included), or fix, improve, re-tone and shorten text.
public sealed partial class TranslateModule : DockPanel
{
    private readonly TranslateStore _store = TranslateStore.Shared;
    private readonly ContentControl _header = new();
    private readonly ContentControl _panes = new();
    private readonly TextBox _input;
    private readonly Border _inputPane;
    private readonly RichTextBox _output = Ui.SelectableText(14);
    private readonly Paragraph _outputText = new() { Margin = new Thickness(0) };
    /// Set while a mode switch rebuilds the header, so the new controls fade in.
    private bool _animateMode;
    private readonly ScrollViewer _outputScroll;
    private readonly TextBlock _stats = Ui.Text("", 11, ThemeResources.SecondaryText, FontWeights.Medium);
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };
    private bool _copied;

    public TranslateModule()
    {
        _input = Ui.Input(_store.Input, 14, multiline: true);
        _input.TextChanged += (_, _) =>
        {
            if (_store.Input == _input.Text) return;
            _store.Input = _input.Text;
            UpdateStats();
            AutoTranslate();
            RefreshActions();
        };
        // Ctrl+Enter runs the current mode from anywhere in the module, the result pane included.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            RunCurrent();
            e.Handled = true;
        };
        _inputPane = Pane(Ui.WithPlaceholder(_input, "", new Thickness(2, 0, 0, 0)));

        _output.Document.Blocks.Add(_outputText);
        _outputScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = _output,
        };

        _header.Margin = new Thickness(0, 0, 0, 8);
        SetDock(_header, Dock.Top);
        Children.Add(_header);

        var toolbar = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(_actions, Dock.Right);
        toolbar.Children.Add(_actions);
        _stats.ToolTip = "Ctrl+Enter to run";
        toolbar.Children.Add(_stats.Tabular());
        SetDock(toolbar, Dock.Bottom);
        Children.Add(toolbar);
        Children.Add(_panes);

        RefreshAll();
        Loaded += (_, _) =>
        {
            _store.Changed += OnStoreChanged;
            AppSettings.Shared.PropertyChanged += OnSettingsChanged;
            SecretStore.Changed += OnSecretChanged;
            RefreshAll();
            _input.Focus();
        };
        Unloaded += (_, _) =>
        {
            _store.Changed -= OnStoreChanged;
            AppSettings.Shared.PropertyChanged -= OnSettingsChanged;
            SecretStore.Changed -= OnSecretChanged;
        };
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.AiProvider)) RefreshAll();
    }

    /// A key added in Settings replaces the "needs an API key" screen straight away.
    private void OnSecretChanged(string name)
    {
        if (name != _store.Provider.SecretName()) return;
        RefreshAll();
        AutoTranslate();
    }

    private void OnStoreChanged()
    {
        // Swap and Accept move the result into the input box.
        if (_input.Text != _store.Input)
        {
            _input.Text = _store.Input;
            _input.CaretIndex = _input.Text.Length;
        }
        RefreshHeader();
        RefreshOutput();
        RefreshActions();
        UpdateStats();
    }

    private void RefreshAll()
    {
        RefreshHeader();
        RefreshPanes();
        RefreshOutput();
        RefreshActions();
        UpdateStats();
    }

    /// Translate mode follows the text live; the writing modes run on demand.
    private void AutoTranslate()
    {
        if (_store.Mode != WritingMode.Translate || !_store.Provider.HasKey()) return;
        _store.ScheduleTranslate();
    }

    private void RunCurrent()
    {
        if (!CanRun) return;
        _store.Run(_store.Mode);
    }

    private bool CanRun => !_store.IsBusy && _store.Provider.HasKey() && _store.Input.Trim().Length > 0;

    // MARK: - Header

    private void RefreshHeader()
    {
        var row = new DockPanel();
        var provider = _store.Provider;
        var providers = new ContextMenu();
        foreach (var p in Enum.GetValues<AiProvider>())
        {
            var choice = p;
            var item = new MenuItem { Header = p.Title(), IsCheckable = true, IsChecked = p == provider };
            item.Click += (_, _) => { _store.Provider = choice; RefreshAll(); AutoTranslate(); };
            providers.Items.Add(item);
        }
        var providerButton = AssistantModule.Dropdown(provider.ShortTitle(), providers);
        DockPanel.SetDock(providerButton, Dock.Right);
        row.Children.Add(providerButton);

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(ModePicker());
        // The controls that belong to the mode: languages and swap, or the tone menu.
        var extras = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(extras);
        if (_animateMode) Appear(extras);
        if (_store.Mode == WritingMode.Translate)
        {
            var sourceLabel = _store.Source == TranslateLanguage.Auto && _store.Detected is { } detected
                ? $"Detected: {detected}"
                : _store.Source.Title();
            extras.Children.Add(Spaced(LanguageMenu(sourceLabel, Enum.GetValues<TranslateLanguage>(), _store.Source,
                l => _store.Source = l)));
            var swap = Ui.IconButton("\uE8AB", 26, () =>
            {
                if (_store.Source == TranslateLanguage.Auto) return;
                (_store.Source, _store.Target) = (_store.Target, _store.Source);
                _store.SwapTexts();
                RefreshHeader();
                AutoTranslate();
            }, tooltip: "Swap languages");
            swap.IsEnabled = _store.Source != TranslateLanguage.Auto;
            swap.Opacity = swap.IsEnabled ? 1 : 0.4;
            extras.Children.Add(Spaced(swap));
            extras.Children.Add(Spaced(LanguageMenu(_store.Target.Title(), TranslateInfo.Targets, _store.Target,
                l => _store.Target = l)));
        }
        else if (_store.Mode == WritingMode.Tone)
        {
            var tones = new ContextMenu();
            foreach (var tone in Enum.GetValues<WritingTone>())
            {
                var choice = tone;
                var item = new MenuItem { Header = tone.ToString(), IsCheckable = true, IsChecked = tone == _store.Tone };
                item.Click += (_, _) => { _store.Tone = choice; RefreshHeader(); };
                tones.Items.Add(item);
            }
            extras.Children.Add(Spaced(AssistantModule.Dropdown(_store.Tone.ToString(), tones, dot: false)));
        }
        row.Children.Add(left);
        _header.Content = row;
    }

    private static FrameworkElement Spaced(FrameworkElement element)
    {
        element.Margin = new Thickness(8, 0, 0, 0);
        element.VerticalAlignment = VerticalAlignment.Center;
        return element;
    }

    private Button LanguageMenu(string label, IEnumerable<TranslateLanguage> options, TranslateLanguage selected,
                                Action<TranslateLanguage> choose)
    {
        var menu = new ContextMenu { MaxHeight = 360 };
        foreach (var language in options)
        {
            var choice = language;
            var item = new MenuItem { Header = language.Title(), IsCheckable = true, IsChecked = language == selected };
            item.Click += (_, _) =>
            {
                choose(choice);
                RefreshHeader();
                AutoTranslate();
            };
            menu.Items.Add(item);
        }
        return AssistantModule.Dropdown(label, menu, dot: false);
    }

    /// Five modes in one capsule; only the selected one shows its name, so the header fits.
    private UIElement ModePicker()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var mode in Enum.GetValues<WritingMode>())
        {
            var active = mode == _store.Mode;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Ui.Glyph(mode.Glyph(), 11, active ? ThemeResources.Background : ThemeResources.Text));
            if (active)
            {
                var title = Ui.Text(mode.Title(), 11, ThemeResources.Background, FontWeights.SemiBold);
                title.Margin = new Thickness(5, 0, 0, 0);
                content.Children.Add(title);
                if (_animateMode) Appear(title);
            }
            var pill = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(9, 5, 9, 5), Child = content };
            if (active) pill.SetResourceReference(Border.BackgroundProperty, ThemeResources.Accent);
            else pill.Background = Brushes.Transparent;
            var choice = mode;
            row.Children.Add(Ui.Bare(pill, () =>
            {
                if (_store.Mode == choice) return;
                _store.Mode = choice;
                _animateMode = true;
                RefreshAll();
                _animateMode = false;
                AutoTranslate();
            }, mode.Title()));
        }
        var capsule = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(2), Child = row };
        capsule.SetResourceReference(Border.BackgroundProperty, ThemeResources.Hairline);
        return capsule;
    }

    /// Fades and slides an element in, like the Mac's expo-out mode switch.
    private static void Appear(UIElement element)
    {
        var curve = Motion.ExpoOut(0.3);
        var slide = new TranslateTransform(-6, 0);
        element.RenderTransform = slide;
        element.Opacity = 0;
        element.Animate(OpacityProperty, 1, curve);
        slide.Animate(TranslateTransform.XProperty, 0, curve);
    }

    // MARK: - Panes

    private void RefreshPanes()
    {
        var provider = _store.Provider;
        if (!provider.HasKey())
        {
            _panes.Content = Ui.EmptyState("\uE8D7", $"{provider.Title()} needs an API key",
                provider == AiProvider.Gemini
                    ? "Get a free key at aistudio.google.com/apikey, then paste it in Settings → AI."
                    : "Add your key in Settings → AI, or switch to Local (on-device) or another provider.");
            return;
        }
        if (_inputPane.Child is Grid withHint && withHint.Children[1] is TextBlock hint)
        {
            hint.Text = _store.Mode == WritingMode.Translate
                ? "Type in any language… e.g. \"kal milte hain yaar\""
                : "Paste or type text to check…";
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        if (_inputPane.Parent is Grid old) old.Children.Remove(_inputPane);
        grid.Children.Add(_inputPane);
        var outputPane = Pane(_outputScroll);
        Grid.SetColumn(outputPane, 2);
        grid.Children.Add(outputPane);
        _panes.Content = grid;
    }

    private static Border Pane(UIElement child)
    {
        var pane = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(13, 8, 13, 8),
            BorderThickness = new Thickness(0.8),
            Child = child,
        };
        pane.SetResourceReference(Border.BackgroundProperty, ThemeResources.Surface);
        pane.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);
        return pane;
    }

    private void RefreshOutput()
    {
        // The result is selectable; so are the placeholder and error lines, which is harmless.
        var inlines = _outputText.Inlines;
        inlines.Clear();
        _output.Opacity = _store.IsBusy && _store.Output.Length > 0 ? 0.5 : 1;
        if (_store.Error is { } error)
        {
            inlines.Add(new Run("⚠ " + error));
            _output.SetResourceReference(Control.ForegroundProperty, ThemeResources.SecondaryText);
            return;
        }
        if (_store.Output.Length == 0)
        {
            inlines.Add(new Run(_store.IsBusy ? _store.Mode.BusyTitle()
                : _store.Mode == WritingMode.Translate ? "Translation appears here." : "Corrected text appears here."));
            _output.SetResourceReference(Control.ForegroundProperty, ThemeResources.SecondaryText);
            return;
        }
        _output.SetResourceReference(Control.ForegroundProperty, ThemeResources.Text);
        if (_store.OutputMode == WritingMode.Translate)
        {
            inlines.Add(new Run(_store.Output));
            return;
        }
        // Words that differ from the original are highlighted, like Grammarly's underlines.
        var accent = ((SolidColorBrush)FindResource(ThemeResources.Accent)).Color;
        foreach (var (word, space, changed) in WordDiff.Compare(_store.Output, _store.Original))
        {
            var run = new Run(word);
            if (changed)
            {
                run.Background = accent.WithOpacity(0.25).Brush();
                run.TextDecorations = TextDecorations.Underline;
            }
            inlines.Add(run);
            if (space.Length > 0) inlines.Add(new Run(space));
        }
    }

    // MARK: - Toolbar

    private void RefreshActions()
    {
        _actions.Children.Clear();
        var hasInput = _store.Input.Length > 0 || _store.Output.Length > 0;
        AddAction(Ui.Pill("Clear", "\uE74D", () => { _store.Clear(); }), hasInput);
        AddAction(Ui.Pill(_copied ? "Copied" : "Copy", _copied ? "\uE73E" : "\uE8C8", CopyOutput), _store.Output.Length > 0);
        if (_store.OutputMode != WritingMode.Translate && _store.Output.Length > 0)
            AddAction(Ui.Pill("Accept", "\uE73E", _store.Accept), true);
        AddAction(Ui.Pill(_store.IsBusy ? _store.Mode.BusyTitle() : _store.Mode.Title(), _store.Mode.Glyph(), RunCurrent,
            prominent: true), CanRun);
    }

    private void AddAction(Button button, bool enabled)
    {
        button.IsEnabled = enabled;
        button.Opacity = enabled ? 1 : 0.5;
        button.Margin = new Thickness(8, 0, 0, 0);
        _actions.Children.Add(button);
    }

    private void CopyOutput()
    {
        try { System.Windows.Clipboard.SetText(_store.Output); }
        catch (System.Runtime.InteropServices.ExternalException) { return; }
        _copied = true;
        RefreshActions();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) => { timer.Stop(); _copied = false; RefreshActions(); };
        timer.Start();
    }

    /// Where a sentence ends: . ! ? or the Hindi danda, then (after any closing quote or bracket) a space or the end.
    /// Dots inside "3.5" or "e.g." aren't followed by a space; common abbreviations ("Mr.", "e.g.", "vs.")
    /// and single initials ("J. Smith", "U.S.") don't count either.
    [GeneratedRegex(@"(?<!\b(?i:e\.g|i\.e|mr|mrs|ms|dr|prof|sr|jr|st|vs|approx|et al)|\b[A-Z])[.!?।]+(?=[""'”’)\]]*(?:\s|$))")]
    private static partial Regex SentenceEnd();

    /// Pieces of text with something in them between sentence ends, plus any unfinished last sentence.
    private static int CountSentences(string text)
    {
        var count = 0;
        var start = 0;
        foreach (Match end in SentenceEnd().Matches(text))
        {
            if (text[start..end.Index].Trim().Length > 0) count++;
            start = end.Index + end.Length;
        }
        if (text[start..].Trim().Length > 0) count++;
        return count;
    }

    /// Word and sentence counts plus reading time at 238 words a minute, like Grammarly's stats.
    private void UpdateStats()
    {
        var text = _store.Input;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var sentences = CountSentences(text);
        var seconds = (int)Math.Round(words / 238.0 * 60);
        var reading = seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60}s";
        _stats.Text = $"{words} words · {sentences} sent. · {reading}";
    }
}

/// Word-level comparison for highlighting edits.
public static class WordDiff
{
    /// The edited text as words (each with the whitespace after it), marking those not in the original.
    public static List<(string Word, string Space, bool Changed)> Compare(string edited, string original)
    {
        var next = Tokens(edited);
        var old = Tokens(original).Select(t => t.Word).ToArray();
        var changed = new bool[next.Count];
        // Longest common subsequence of words; anything outside it was inserted or changed.
        // Very long texts skip highlighting rather than build a huge table.
        if ((long)next.Count * old.Length <= 4_000_000)
        {
            var n = next.Count;
            var m = old.Length;
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lcs[i, j] = next[i].Word == old[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int a = 0, b = 0;
            while (a < n)
            {
                if (b < m && next[a].Word == old[b]) { a++; b++; }
                else if (b < m && lcs[a, b + 1] > lcs[a + 1, b]) b++;
                else changed[a++] = true;
            }
        }
        return next.Select((t, i) => (t.Word, t.Space, changed[i])).ToList();
    }

    private static List<(string Word, string Space)> Tokens(string text)
    {
        var result = new List<(string, string)>();
        var word = new System.Text.StringBuilder();
        var space = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                space.Append(ch);
            }
            else
            {
                if (space.Length > 0)
                {
                    result.Add((word.ToString(), space.ToString()));
                    word.Clear();
                    space.Clear();
                }
                word.Append(ch);
            }
        }
        if (word.Length > 0 || space.Length > 0) result.Add((word.ToString(), space.ToString()));
        return result;
    }
}
