using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Purrch.Core;
using Purrch.DesignSystem;

namespace Purrch.Interface;

/// The always-on-top window at the top centre of the primary screen. It is mostly transparent;
/// only the pill (and the panel it grows into) catches the mouse.
public partial class NotchWindow : Window
{
    /// How far the top corners flare outward into the screen edge.
    private const double Flare = 10;
    private static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    private readonly NotchState _state = NotchState.Shared;
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly DispatcherTimer _pointerTimer;
    private readonly DispatcherTimer _fullscreenTimer;
    private DateTime? _hoverStart;
    private DateTime? _leaveStart;
    private bool _wasButtonDown;
    private IntPtr _hwnd;

    public NotchWindow()
    {
        InitializeComponent();

        _pointerTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(33) };
        _pointerTimer.Tick += (_, _) => TrackPointer();
        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();

        _state.PropertyChanged += OnStateChanged;
        _settings.PropertyChanged += OnSettingsChanged;
        LiveActivityHub.Shared.Changed += RefreshActivity;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Body.MouseLeftButtonDown += OnBodyMouseDown;
        KeyDown += OnKeyDown;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            Position();
            ApplyTheme();
            RefreshActivity();
            Render(Motion.Close, animated: false);
            _pointerTimer.Start();
            _fullscreenTimer.Start();
        };
        Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // A tool window stays out of Alt+Tab and the taskbar.
        var style = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        style = (style | Native.WS_EX_TOOLWINDOW) & ~(long)Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(style));
    }

    /// Top centre of the primary screen, in device-independent pixels.
    private void Position()
    {
        Left = Math.Round((SystemParameters.PrimaryScreenWidth - Width) / 2);
        Top = 0;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Position);

    // MARK: - State

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NotchState.IsExpanded):
                Render(_state.IsExpanded ? Motion.Open : Motion.Close);
                if (!_state.IsExpanded) _state.EnsureSelectionEnabled();
                break;
            case nameof(NotchState.IsPeeking):
                Render(Motion.Peek);
                break;
            case nameof(NotchState.LiveActivityWing):
                Render(Motion.Pop);
                break;
            case nameof(NotchState.Selected):
            case nameof(NotchState.ShowSettings):
                ApplyTheme();
                break;
            case nameof(NotchState.IsHidden):
                if (_state.IsHidden) { _state.ForceClose(); Hide(); }
                else Show();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.ThemeId):
                ApplyTheme();
                Render(Motion.ExpoOut(0.35));
                break;
            case nameof(AppSettings.ShowLiveActivities):
                RefreshActivity();
                break;
            case nameof(AppSettings.HideInFullscreen):
                CheckFullscreen();
                break;
        }
    }

    /// Publishes theme brushes. Inside a module, the Playful theme tints everything with that module's colour.
    private void ApplyTheme()
    {
        var theme = _settings.Theme;
        var accent = _state.ShowSettings ? theme.Accent : theme.AccentFor(_state.Selected);
        ThemeResources.Apply(theme, accent);

        Surface.Fill = theme.Background.Brush();
        Surface.Sheen = new RadialGradientBrush(theme.Accent.WithOpacity(theme.Id == ThemeId.Minimal ? 0.07 : 0.16), Colors.Transparent)
        {
            Center = new Point(0.5, 0),
            GradientOrigin = new Point(0.5, 0),
            RadiusX = 0.46,
            RadiusY = 0.86,
            Opacity = _state.IsExpanded ? 1 : 0,
        };
        Surface.Stroke = new LinearGradientBrush(Colors.Transparent,
            theme.Border.WithOpacity(theme.Id == ThemeId.Minimal ? 0.35 : 0.8), 90)
        {
            Opacity = _state.IsExpanded ? 1 : 0,
        };
        Glow.Color = theme.Accent;
    }

    private void RefreshActivity()
    {
        var activity = LiveActivityHub.Shared.Current;
        Collapsed.Show(activity);
        _state.LiveActivityWing = activity.Wing;
        UpdateGlow(Motion.Pop);
    }

    /// Animates the body to the size and look the state calls for.
    private void Render(Curve curve, bool animated = true)
    {
        var (width, height) = _state.CurrentSize;
        var expanded = _state.IsExpanded;
        var bodyWidth = width + Flare * 2;
        var radius = expanded ? 28 : 10;

        if (animated)
        {
            Body.Animate(WidthProperty, bodyWidth, curve);
            Body.Animate(HeightProperty, height, curve);
            Surface.Animate(NotchSurface.BottomRadiusProperty, radius, curve);
            Shadow.Animate(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, expanded ? 0.6 : 0, curve);
        }
        else
        {
            Body.Set(WidthProperty, bodyWidth);
            Body.Set(HeightProperty, height);
            Surface.Set(NotchSurface.BottomRadiusProperty, radius);
            Shadow.Set(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, expanded ? 0.6 : 0);
        }

        SwapContent(expanded, animated);
        UpdateGlow(curve);
    }

    private bool _contentExpanded;

    private void SwapContent(bool expanded, bool animated)
    {
        if (expanded == _contentExpanded && animated) return;
        _contentExpanded = expanded;
        var fade = animated ? 1.0 : 0.0;

        if (expanded)
        {
            Collapsed.Animate(OpacityProperty, 0, Motion.Linear(0.08 * fade));
            Expanded.Visibility = Visibility.Visible;
            Expanded.Animate(OpacityProperty, 1, Motion.Linear(0.12 * fade));
            if (animated) Expanded.PlayEntrance();
            Surface.Sheen?.Animate(Brush.OpacityProperty, 1, Motion.ExpoOut(0.4 * fade));
            Surface.Stroke?.Animate(Brush.OpacityProperty, 1, Motion.ExpoOut(0.4 * fade));
        }
        else
        {
            Expanded.Animate(OpacityProperty, 0, Motion.Linear(0.1 * fade), completed: () =>
            {
                if (!_state.IsExpanded) Expanded.Visibility = Visibility.Collapsed;
            });
            Collapsed.Animate(OpacityProperty, 1, Motion.Linear(0.25 * fade), delay: 0.15 * fade);
            Surface.Sheen?.Animate(Brush.OpacityProperty, 0, Motion.Linear(0.15 * fade));
            Surface.Stroke?.Animate(Brush.OpacityProperty, 0, Motion.Linear(0.15 * fade));
        }
    }

    /// Theme glow around the open panel; a slow breathing glow around the closed pill while something is live.
    private void UpdateGlow(Curve curve)
    {
        var theme = _settings.Theme;
        var opacityProperty = System.Windows.Media.Effects.DropShadowEffect.OpacityProperty;
        if (_state.IsExpanded)
        {
            Glow.BlurRadius = 26;
            Glow.Animate(opacityProperty, theme.Glow ? 0.28 : 0, curve);
        }
        else if (_state.LiveActivityWing > 0)
        {
            Glow.BlurRadius = 14;
            Glow.BeginAnimation(opacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.1, 0.55, TimeSpan.FromSeconds(1.6))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                EasingFunction = new System.Windows.Media.Animation.SineEase(),
            });
        }
        else
        {
            Glow.Animate(opacityProperty, 0, curve);
        }
    }

    // MARK: - Pointer

    private void OnBodyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_state.IsExpanded) return;
        _state.Open();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_state.IsExpanded) return;
        _state.ForceClose();
        e.Handled = true;
    }

    /// Where the pill is (or is growing to), in window coordinates, padded for a forgiving hover.
    private Rect ShapeRect(double padding = 0)
    {
        var (width, height) = _state.CurrentSize;
        return new Rect((ActualWidth - width) / 2 - padding, 0, width + padding * 2, height + padding + 1);
    }

    private Point? PointerInWindow()
    {
        if (!Native.GetCursorPos(out var p) || PresentationSource.FromVisual(this) == null) return null;
        return PointFromScreen(new Point(p.X, p.Y));
    }

    /// Polled 30 times a second, like the Mac app: hovering opens, leaving closes, clicking outside closes.
    private void TrackPointer()
    {
        if (_state.IsHidden) { _state.SetPeeking(false); return; }
        if (PointerInWindow() is not { } mouse) return;
        var down = Native.IsMouseButtonDown();
        var pressed = down && !_wasButtonDown;
        _wasButtonDown = down;
        var now = DateTime.UtcNow;

        if (!_state.IsExpanded)
        {
            _leaveStart = null;
            // The pill swells a little under the pointer before it opens (or to invite a click).
            var over = !down && ShapeRect(6).Contains(mouse);
            _state.SetPeeking(over);

            if (!_settings.OpenOnHover || !over)
            {
                _hoverStart = null;
                return;
            }
            if (_hoverStart is { } start)
            {
                if ((now - start).TotalSeconds > 0.16)
                {
                    _hoverStart = null;
                    _state.Open();
                }
            }
            else
            {
                _hoverStart = now;
            }
            return;
        }

        _hoverStart = null;
        var inside = ShapeRect(18).Contains(mouse);
        if (pressed && !ShapeRect().Contains(mouse))
        {
            // A click in another app folds the panel away.
            _leaveStart = null;
            _state.Close();
            return;
        }
        if (inside) _state.HoldUntilHovered = false;
        if (_state.IsPinned || _state.HoldUntilHovered || down || inside)
        {
            _leaveStart = null;
            return;
        }
        if (!_settings.OpenOnHover) return;
        if (_leaveStart is { } left)
        {
            if ((now - left).TotalSeconds > 0.35)
            {
                _leaveStart = null;
                _state.Close();
            }
        }
        else
        {
            _leaveStart = now;
        }
    }

    // MARK: - Full screen

    /// Hide while another app fills the primary screen (a video, a game, a presentation).
    private void CheckFullscreen()
    {
        var covered = _settings.HideInFullscreen && IsPrimaryScreenCovered();
        if (covered != _state.IsHidden) _state.IsHidden = covered;
        if (!_state.IsHidden && _hwnd != IntPtr.Zero)
        {
            // Other always-on-top windows can climb above us; quietly take the top spot back.
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    private bool IsPrimaryScreenCovered()
    {
        if (_hwnd == IntPtr.Zero) return false;
        if (Native.SHQueryUserNotificationState(out var quns) == 0 && quns == Native.QUNS_RUNNING_D3D_FULL_SCREEN)
            return true;

        var front = Native.GetForegroundWindow();
        if (front == IntPtr.Zero || front == _hwnd) return false;
        if (ShellClasses.Contains(Native.ClassName(front))) return false;
        // A maximised window can overhang the screen edges; it isn't full screen.
        if (Native.IsZoomed(front)) return false;

        var monitor = Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST);
        if (Native.MonitorFromWindow(front, Native.MONITOR_DEFAULTTONEAREST) != monitor) return false;
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info) || !Native.GetWindowRect(front, out var rect)) return false;
        var m = info.rcMonitor;
        return rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
    }
}
