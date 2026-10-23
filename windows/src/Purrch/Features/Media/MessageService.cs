using Purrch.Core;

namespace Purrch.Features.Media;

/// The text that scrolls across the closed pill, and whether it shows. Lives for the whole app,
/// because the pill shows it while the panel is shut.
public sealed class MessageService
{
    public static MessageService Shared { get; } = new();

    public const int MaxLength = 80;

    private sealed class Saved
    {
        public string Text { get; set; } = "Hello from Purrch";
        public bool Enabled { get; set; }
    }

    private readonly Saved _saved;
    private readonly DebouncedSaver _saver;

    /// Raised when the text or the switch changes.
    public event Action? Changed;

    private MessageService()
    {
        _saved = Store.Load<Saved>("message");
        if (_saved.Text.Length > MaxLength) _saved.Text = _saved.Text[..MaxLength];
        _saver = new DebouncedSaver(TimeSpan.FromSeconds(0.6), () => Store.Save("message", _saved));
        // Takes priority over timers and music while on (the hub ranks by kind).
        LiveActivityHub.Shared.Register(() =>
            IsShowing ? new LiveActivity(LiveActivityKind.Message, _saved.Text) : null);
    }

    public string Text
    {
        get => _saved.Text;
        set
        {
            var text = value.Length > MaxLength ? value[..MaxLength] : value;
            if (text == _saved.Text) return;
            _saved.Text = text;
            Update();
        }
    }

    public bool Enabled
    {
        get => _saved.Enabled;
        set
        {
            if (value == _saved.Enabled) return;
            _saved.Enabled = value;
            Update();
        }
    }

    /// Blank text has nothing to scroll, so the switch can't turn it on.
    public bool HasText => _saved.Text.Trim().Length > 0;

    public bool IsShowing => _saved.Enabled && HasText;

    /// Saves now if typing left a save pending (when Purrch quits).
    public void Flush() => _saver.Flush();

    private void Update()
    {
        _saver.Schedule();
        Changed?.Invoke();
        LiveActivityHub.Shared.Refresh();
    }
}
