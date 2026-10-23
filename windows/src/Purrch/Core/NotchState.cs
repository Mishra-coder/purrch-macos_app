using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Purrch.Core;

/// Shared UI state for the pill window. The window watches it and animates to match.
public sealed class NotchState : INotifyPropertyChanged
{
    public static NotchState Shared { get; } = new();

    /// Size of the closed pill. Windows has no camera housing, so this is the "notch".
    public static readonly (double Width, double Height) PillSize = (190, 30);
    public static readonly (double Width, double Height) ExpandedSize = (780, 420);
    /// The small live-video window that hovering opens while a browser video plays.
    public static readonly (double Width, double Height) VideoPeekSize = (540, 352);
    /// Tallest the theater panel gets; the window is sized to fit it.
    public const double MaxTheaterHeight = 500;

    private bool _isExpanded, _isPinned, _isPeeking, _isHidden, _showSettings, _isVideoPeek;
    private string? _theaterVideoId;
    private ModuleId _selected = AppSettings.Shared.LastModule;
    private double _liveActivityWing;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsExpanded { get => _isExpanded; private set => Set(ref _isExpanded, value); }
    public bool IsPinned { get => _isPinned; set => Set(ref _isPinned, value); }
    /// Pointer is resting on the closed pill, just before it opens.
    public bool IsPeeking { get => _isPeeking; private set => Set(ref _isPeeking, value); }
    /// Open as a small live-video window (a browser video is playing) instead of the full panel.
    public bool IsVideoPeek { get => _isVideoPeek; private set => Set(ref _isVideoPeek, value); }
    /// A browser video filling the whole expanded panel ("theater"), by video id.
    public string? TheaterVideoId { get => _theaterVideoId; private set => Set(ref _theaterVideoId, value); }
    /// Hidden while a full-screen app covers the primary screen.
    public bool IsHidden { get => _isHidden; set => Set(ref _isHidden, value); }
    public bool ShowSettings
    {
        get => _showSettings;
        set
        {
            // Leaving settings after turning off the module on screen lands on Home.
            if (!value) EnsureSelectionEnabled();
            Set(ref _showSettings, value);
        }
    }

    /// The window that was in front before Purrch took focus (for "the app you were in").
    public IntPtr PreviousApp { get; set; }

    /// Opened from the tray or a second launch: stay open until the pointer has visited the panel once.
    public bool HoldUntilHovered { get; set; }

    /// +1 when the last tab change moved right, -1 when it moved left. Drives the slide direction.
    public int SwitchDirection { get; set; } = 1;

    public ModuleId Selected
    {
        get => _selected;
        private set
        {
            if (Set(ref _selected, value)) AppSettings.Shared.LastModule = value;
        }
    }

    /// Extra width on each side of the pill used by the closed live activity.
    public double LiveActivityWing { get => _liveActivityWing; set => Set(ref _liveActivityWing, value); }

    /// Full panel for a video: tall enough that a 16:9 picture fills the whole width under the header.
    public static (double Width, double Height) TheaterSize
    {
        get
        {
            var width = ExpandedSize.Width;
            var header = Math.Max(PillSize.Height, 28) + 6;
            var video = (width - 24) * 9 / 16;
            return (width, Math.Min(MaxTheaterHeight, Math.Round(header + video + 12)));
        }
    }

    public (double Width, double Height) CurrentSize
    {
        get
        {
            if (IsExpanded)
            {
                if (IsVideoPeek) return VideoPeekSize;
                return TheaterVideoId != null ? TheaterSize : ExpandedSize;
            }
            var peek = IsPeeking ? 1 : 0;
            return (PillSize.Width + LiveActivityWing * 2 + peek * 18, PillSize.Height + peek * 5);
        }
    }

    public void Open(ModuleId? module = null)
    {
        // Asking for a specific module leaves theater; a plain hover-open returns to the video.
        if (module is { } m)
        {
            Selected = m;
            TheaterVideoId = null;
        }
        IsPeeking = false;
        IsVideoPeek = false;
        IsExpanded = true;
    }

    /// Hover while a browser video plays: show just that video, live.
    public void OpenVideoPeek()
    {
        IsPeeking = false;
        IsVideoPeek = true;
        IsExpanded = true;
    }

    /// From the video window to the full panel, keeping it open.
    public void ExpandToFullPanel(ModuleId? module = null)
    {
        if (module is { } m)
        {
            ShowSettings = false;
            Selected = m;
        }
        IsVideoPeek = false;
    }

    /// Opens without hover (tray, second launch); closes normally once the pointer has come and gone.
    public void Present(ModuleId? module = null, bool settings = false)
    {
        HoldUntilHovered = true;
        Open(module);
        if (settings) { SwitchDirection = 1; ShowSettings = true; }
    }

    /// Theater survives a close, so hovering the pill again picks the video back up; Exit leaves it.
    public void Close()
    {
        if (IsPinned) return;
        HoldUntilHovered = false;
        IsExpanded = false;
        IsVideoPeek = false;
        ShowSettings = false;
    }

    /// Closes even when pinned, and leaves theater, for explicit requests (Esc, the close command, going full screen).
    public void ForceClose()
    {
        IsPinned = false;
        TheaterVideoId = null;
        Close();
    }

    public void EnterTheater(string videoId) => TheaterVideoId = videoId;

    public void ExitTheater() => TheaterVideoId = null;

    public void Select(ModuleId module)
    {
        var modules = AppSettings.Shared.EnabledModules;
        var from = IndexOf(modules, Selected);
        var to = IndexOf(modules, module);
        SwitchDirection = to >= from ? 1 : -1;
        ShowSettings = false;
        Selected = module;
    }

    /// Falls back to Home when the selected module has just been turned off.
    public void EnsureSelectionEnabled()
    {
        if (!AppSettings.Shared.IsEnabled(Selected)) Selected = ModuleId.Home;
    }

    public void SetPeeking(bool peeking) => IsPeeking = peeking;

    private static int IndexOf(IReadOnlyList<ModuleId> list, ModuleId m)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == m) return i;
        return 0;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
