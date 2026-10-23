using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;
using Purrch.Core;

namespace Purrch.Features.ScreenTime;

/// What screentime.json holds: seconds per app per day, and what each app is called.
public sealed class ScreenTimeData
{
    public sealed record App(string Name, string? Path);

    /// "yyyy-MM-dd" -> app key -> seconds.
    public Dictionary<string, Dictionary<string, double>> Days { get; set; } = new();
    /// App key (the exe name in lower case, "chrome") -> its name and exe.
    public Dictionary<string, App> Apps { get; set; } = new();
}

/// Tracks how long each foreground app is used, per day, like Screen Time on the Mac. Time doesn't count
/// while the session is locked, the PC sleeps, or nothing has been touched for two minutes. Keeps 7 days.
public sealed class ScreenTimeService
{
    public static ScreenTimeService Shared { get; } = new();

    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(2);
    private const int KeepDays = 7;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DebouncedSaver _saver;
    private readonly ScreenTimeData _data;
    /// Kept in a field so the garbage collector doesn't free it while Windows still calls it.
    private readonly WinEventProc _onForeground;
    private IntPtr _hook;
    private string? _current;
    private uint _currentPid;
    private DateTime _lastTick = DateTime.Now;
    private bool _locked, _suspended, _started, _savePending;

    private ScreenTimeService()
    {
        _data = Store.Load<ScreenTimeData>("screentime");
        _saver = new DebouncedSaver(TimeSpan.FromSeconds(30), Save);
        _timer.Tick += (_, _) => Accumulate();
        _onForeground = (_, _, hwnd, _, _, _, _) => SwitchTo(hwnd);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        Prune();
        _lastTick = DateTime.Now;
        SwitchTo(GetForegroundWindow());
        // Out-of-context hooks call back on this thread through its message loop, so switches are exact.
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _onForeground, 0, 0, WINEVENT_OUTOFCONTEXT);
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _timer.Start();
    }

    /// Counts the last few seconds and saves (when Purrch quits).
    public void Flush()
    {
        if (!_started) return;
        Accumulate();
        _saver.Flush();
        _timer.Stop();
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _started = false;
    }

    // SystemEvents raise on their own thread; the totals belong to the UI thread.
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        bool? locked = e.Reason switch
        {
            SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect => true,
            SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect => false,
            _ => null,
        };
        if (locked is { } value) _dispatcher.BeginInvoke(() => SetState(locked: value, suspended: null));
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume)
            _dispatcher.BeginInvoke(() => SetState(locked: null, suspended: e.Mode == PowerModes.Suspend));
    }

    private void SetState(bool? locked, bool? suspended)
    {
        Accumulate();
        if (locked is { } l) _locked = l;
        if (suspended is { } s) _suspended = s;
    }

    private void SwitchTo(IntPtr hwnd)
    {
        Accumulate();
        _current = AppKey(hwnd);
    }

    private bool IsUserActive
    {
        get
        {
            if (_locked || _suspended) return false;
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return true;
            // Both are milliseconds since boot in 32 bits; unsigned subtraction survives the 49-day wrap.
            var idle = unchecked((uint)Environment.TickCount - info.dwTime);
            return idle < IdleLimit.TotalMilliseconds;
        }
    }

    private void Accumulate()
    {
        var now = DateTime.Now;
        // Never more than 10 seconds at once, so a sleep the events missed doesn't count.
        var elapsed = Math.Min((now - _lastTick).TotalSeconds, 10);
        _lastTick = now;
        if (elapsed <= 0 || _current is not { } current || !IsUserActive) return;
        var key = DayKey(now);
        if (!_data.Days.TryGetValue(key, out var apps)) _data.Days[key] = apps = new();
        apps[current] = apps.GetValueOrDefault(current) + elapsed;
        // Scheduling again would push the save back on every tick; once is enough until it has run.
        if (_savePending) return;
        _savePending = true;
        _saver.Schedule();
    }

    private void Save()
    {
        _savePending = false;
        Prune();
        Store.Save("screentime", _data);
    }

    private void Prune()
    {
        var cutoff = DayKey(DateTime.Today.AddDays(-(KeepDays - 1)));
        foreach (var stale in _data.Days.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList())
            _data.Days.Remove(stale);
        var used = _data.Days.Values.SelectMany(d => d.Keys).ToHashSet();
        foreach (var gone in _data.Apps.Keys.Where(k => !used.Contains(k)).ToList())
            _data.Apps.Remove(gone);
    }

    // MARK: Which app

    /// The app that owns a window, as a key into <see cref="ScreenTimeData.Apps"/>. Null for no window.
    private string? AppKey(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        if (pid == _currentPid && _current != null) return _current;
        var path = ImagePath(pid);
        // Store apps draw inside ApplicationFrameHost; the app itself owns a child window.
        if (path != null && Path.GetFileName(path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
            && HostedProcess(hwnd, pid) is { } hosted)
        {
            pid = hosted;
            path = ImagePath(pid);
        }
        _currentPid = pid;

        string key, name;
        if (path != null)
        {
            key = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (_data.Apps.TryGetValue(key, out var known) && known.Path == path) return key;
            name = FriendlyName(path);
        }
        else
        {
            // Protected processes may not even say where their exe is; the process name still works.
            try
            {
                using var process = Process.GetProcessById((int)pid);
                name = process.ProcessName;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return null;
            }
            key = name.ToLowerInvariant();
            if (_data.Apps.ContainsKey(key)) return key;
        }
        _data.Apps[key] = new ScreenTimeData.App(name, path);
        return key;
    }

    private static uint? HostedProcess(IntPtr frame, uint framePid)
    {
        uint? found = null;
        EnumChildWindows(frame, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var pid);
            if (pid == 0 || pid == framePid) return true;
            found = pid;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// Full path of a process's exe. Limited access works for elevated apps too.
    private static string? ImagePath(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// "Google Chrome" for chrome.exe; the exe name when it has no description.
    private static string FriendlyName(string path)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description)) return description;
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Fall back to the exe name.
        }
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length == 0 ? path : char.ToUpperInvariant(name[0]) + name[1..];
    }

    // MARK: Queries

    public static string DayKey(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public double TodayTotal => _data.Days.GetValueOrDefault(DayKey(DateTime.Today))?.Values.Sum() ?? 0;

    /// Apps used today, most used first.
    public IReadOnlyList<(string Key, double Seconds)> TodayApps =>
        (_data.Days.GetValueOrDefault(DayKey(DateTime.Today)) ?? new())
            .Select(p => (p.Key, p.Value)).OrderByDescending(p => p.Value).ToList();

    /// The last 7 days (oldest first) with total seconds.
    public IReadOnlyList<(DateTime Date, double Seconds)> Week =>
        Enumerable.Range(0, KeepDays).Reverse().Select(offset =>
        {
            var day = DateTime.Today.AddDays(-offset);
            return (day, _data.Days.GetValueOrDefault(DayKey(day))?.Values.Sum() ?? 0);
        }).ToList();

    public string Name(string key) => _data.Apps.GetValueOrDefault(key)?.Name ?? key;

    public string? ExePath(string key) => _data.Apps.GetValueOrDefault(key)?.Path;

    // MARK: Win32

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
                                                 uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
