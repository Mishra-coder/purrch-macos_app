using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Interface;

namespace Purrch.Features.Notes;

/// A text box that never forgets: saved to %APPDATA%\Purrch\scratchpad.json as you type.
public sealed class ScratchpadStore
{
    public static ScratchpadStore Shared { get; } = new();

    private sealed class Data
    {
        public string Text { get; set; } = "";
        public bool Monospaced { get; set; }
        public double FontSize { get; set; } = 14;
    }

    private readonly Data _data;
    private readonly DebouncedSaver _saver;

    private ScratchpadStore()
    {
        _data = Store.Load<Data>("scratchpad");
        _data.FontSize = Math.Clamp(_data.FontSize, 10, 28);
        _saver = new DebouncedSaver(TimeSpan.FromSeconds(0.6), () => Store.Save("scratchpad", _data));
    }

    public string Text
    {
        get => _data.Text;
        set { if (_data.Text != value) { _data.Text = value; _saver.Schedule(); } }
    }

    public bool Monospaced
    {
        get => _data.Monospaced;
        set { _data.Monospaced = value; _saver.Schedule(); }
    }

    public double FontSize
    {
        get => _data.FontSize;
        set { _data.FontSize = Math.Clamp(value, 10, 28); _saver.Schedule(); }
    }

    public void Flush() => _saver.Flush();
}

public sealed class ScratchpadModule : DockPanel
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly FontFamily Proportional = new("Segoe UI");

    private readonly ScratchpadStore _store = ScratchpadStore.Shared;
    private readonly TextBox _editor;
    private readonly Grid _editorWithHint;
    private readonly TextBlock _stats = Ui.Text("", 11, ThemeResources.SecondaryText, FontWeights.Medium);
    private readonly TextBlock _size = Ui.Text("", 11, ThemeResources.SecondaryText, FontWeights.SemiBold);
    private readonly ContentControl _monoButton = new();
    private readonly ContentControl _copyButton = new();
    private readonly ContentControl _clearButton = new();
    private bool _confirmClear;

    public ScratchpadModule()
    {
        _editor = Ui.Input(_store.Text, _store.FontSize, multiline: true);
        _editorWithHint = Ui.WithPlaceholder(_editor, "Type anything. It's saved automatically.");
        _editor.TextChanged += (_, _) =>
        {
            _store.Text = _editor.Text;
            UpdateStats();
            RefreshClear();
        };

        var frame = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 8, 12, 8),
            BorderThickness = new Thickness(0.8),
            Child = _editorWithHint,
            Margin = new Thickness(0, 0, 0, 8),
        };
        frame.SetResourceReference(Border.BackgroundProperty, ThemeResources.Surface);
        frame.SetResourceReference(Border.BorderBrushProperty, ThemeResources.Border);

        var toolbar = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Ui.IconButton("\uE738", 24, () => SetSize(_store.FontSize - 1), tooltip: "Smaller text"));
        _size.Width = 38;
        _size.TextAlignment = TextAlignment.Center;
        right.Children.Add(_size.Tabular());
        right.Children.Add(Ui.IconButton("\uE710", 24, () => SetSize(_store.FontSize + 1), tooltip: "Larger text"));
        _monoButton.Margin = new Thickness(8, 0, 0, 0);
        right.Children.Add(_monoButton);
        _copyButton.Margin = new Thickness(8, 0, 0, 0);
        right.Children.Add(_copyButton);
        _clearButton.Margin = new Thickness(8, 0, 0, 0);
        right.Children.Add(_clearButton);
        DockPanel.SetDock(right, Dock.Right);
        toolbar.Children.Add(right);
        toolbar.Children.Add(_stats.Tabular());

        DockPanel.SetDock(toolbar, Dock.Bottom);
        Children.Add(toolbar);
        Children.Add(frame);

        ApplyFont();
        UpdateStats();
        RefreshCopy(copied: false);
        RefreshClear();
        Unloaded += (_, _) => _store.Flush();
    }

    private void SetSize(double size)
    {
        _store.FontSize = size;
        ApplyFont();
    }

    private void ApplyFont()
    {
        _editor.FontSize = _store.FontSize;
        _editor.FontFamily = _store.Monospaced ? Mono : Proportional;
        if (_editorWithHint.Children[1] is TextBlock hint)
        {
            hint.FontSize = _store.FontSize;
            hint.FontFamily = _editor.FontFamily;
        }
        _size.Text = $"{(int)_store.FontSize}pt";
        _monoButton.Content = Ui.IconButton("\uE943", 26, () =>
        {
            _store.Monospaced = !_store.Monospaced;
            ApplyFont();
        }, prominent: _store.Monospaced, tooltip: "Monospaced");
    }

    private void UpdateStats()
    {
        var text = _editor.Text;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        // Counted as you see them, like Swift's String.count: an emoji or an accented letter is one character.
        _stats.Text = $"{new StringInfo(text).LengthInTextElements} chars · {words} words";
    }

    private void RefreshCopy(bool copied)
    {
        _copyButton.Content = Ui.Pill(copied ? "Copied" : "Copy all", copied ? "\uE73E" : "\uE8C8", () =>
        {
            try
            {
                System.Windows.Clipboard.SetText(_editor.Text);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return; // clipboard busy; nothing copied
            }
            RefreshCopy(copied: true);
            After(1.5, () => RefreshCopy(copied: false));
        });
    }

    /// Clearing takes two clicks, so a stray click can't wipe the pad.
    private void RefreshClear()
    {
        var pill = Ui.Pill(_confirmClear ? "Confirm clear" : "Clear", "\uE74D", () =>
        {
            if (_confirmClear)
            {
                _editor.Text = "";
                _confirmClear = false;
            }
            else
            {
                _confirmClear = true;
                After(3, () => { _confirmClear = false; RefreshClear(); });
            }
            RefreshClear();
        }, prominent: _confirmClear);
        pill.IsEnabled = _editor.Text.Length > 0;
        pill.Opacity = pill.IsEnabled ? 1 : 0.5;
        _clearButton.Content = pill;
    }

    private static void After(double seconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }
}
