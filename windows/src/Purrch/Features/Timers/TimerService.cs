using System.IO;
using System.Media;
using System.Windows.Threading;
using Purrch.Core;
using Purrch.Interface;

namespace Purrch.Features.Timers;

public enum PomodoroPhase { Focus, ShortBreak, LongBreak }

public static class PomodoroPhaseInfo
{
    public static string Title(this PomodoroPhase p) => p switch
    {
        PomodoroPhase.Focus => "Focus",
        PomodoroPhase.ShortBreak => "Short Break",
        _ => "Long Break",
    };

    /// Stopwatch while focusing, a moon while resting.
    public static string Glyph(this PomodoroPhase p) => p == PomodoroPhase.Focus ? "\uE916" : "\uE708";
}

/// Pomodoro, countdown, stopwatch and water tracking. Lives for the whole app, so timers keep
/// running (and show in the closed pill) while the panel is shut.
public sealed class TimerService
{
    public static TimerService Shared { get; } = new();

    private const string WaterReminderTitle = "Drink some water";
    private static readonly TimeSpan WaterInterval = TimeSpan.FromMinutes(30);

    /// Something other than the passing of time changed: what is running, settings, laps, water.
    public event Action? StateChanged;
    /// Raised about 20 times a second while anything runs.
    public event Action? Ticked;

    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _waterReminder = new() { Interval = WaterInterval };
    private readonly bool _loading;

    private sealed class Prefs
    {
        public int FocusMinutes { get; set; } = 25;
        public int BreakMinutes { get; set; } = 5;
        public int LongBreakMinutes { get; set; } = 15;
        public int Completed { get; set; }
        public string CompletedDay { get; set; } = "";
    }

    private sealed class Water
    {
        public int Glasses { get; set; }
        public int Goal { get; set; } = 8;
        public bool Enabled { get; set; }
        public string Day { get; set; } = "";
    }

    private TimerService()
    {
        _loading = true;
        var prefs = Store.Load<Prefs>("timers");
        _focusMinutes = Math.Clamp(prefs.FocusMinutes, 1, 90);
        _breakMinutes = Math.Clamp(prefs.BreakMinutes, 1, 60);
        LongBreakMinutes = Math.Clamp(prefs.LongBreakMinutes, 1, 60);
        CompletedPomodoros = prefs.CompletedDay == Today ? prefs.Completed : 0;
        _pomoRemainingPaused = PomoDuration(PomodoroPhase.Focus);

        var water = Store.Load<Water>("water");
        _waterGoal = Math.Max(1, water.Goal);
        _waterEnabled = water.Enabled;
        _waterDay = water.Day;
        Glasses = water.Day == Today ? Math.Min(water.Glasses, _waterGoal) : 0;
        _loading = false;

        _ticker.Tick += (_, _) => Tick();
        _waterReminder.Tick += (_, _) =>
            TrayIcon.Current?.Notify(WaterReminderTitle, "Time for a glass. Tap a drop in Purrch to log it.");
        if (_waterEnabled) _waterReminder.Start();

        LiveActivityHub.Shared.Register(() =>
            LiveLabel is { } label ? new LiveActivity(LiveActivityKind.Timer, label, LiveGlyph) : null);
    }

    private static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    // MARK: - Pomodoro

    private int _focusMinutes;
    private int _breakMinutes;
    private double _pomoRemainingPaused;
    private DateTime? _pomoEnd;

    public PomodoroPhase PomoPhase { get; private set; } = PomodoroPhase.Focus;
    public bool PomoRunning { get; private set; }
    public int LongBreakMinutes { get; private set; }
    public int CompletedPomodoros { get; private set; }

    public int FocusMinutes
    {
        get => _focusMinutes;
        set { _focusMinutes = Math.Clamp(value, 1, 90); PomoSettingsChanged(); }
    }

    public int BreakMinutes
    {
        get => _breakMinutes;
        set { _breakMinutes = Math.Clamp(value, 1, 60); PomoSettingsChanged(); }
    }

    public double PomoDuration(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.Focus => _focusMinutes * 60,
        PomodoroPhase.ShortBreak => _breakMinutes * 60,
        _ => LongBreakMinutes * 60,
    };

    public double PomoRemaining => _pomoEnd is { } end ? Math.Max(0, (end - DateTime.UtcNow).TotalSeconds) : _pomoRemainingPaused;

    public double PomoProgress
    {
        get
        {
            var total = PomoDuration(PomoPhase);
            return total > 0 ? 1 - PomoRemaining / total : 0;
        }
    }

    public void PomoToggle()
    {
        if (PomoRunning)
        {
            _pomoRemainingPaused = PomoRemaining;
            _pomoEnd = null;
            PomoRunning = false;
        }
        else
        {
            _pomoEnd = DateTime.UtcNow.AddSeconds(_pomoRemainingPaused);
            PomoRunning = true;
        }
        Refresh();
    }

    public void PomoReset()
    {
        _pomoEnd = null;
        PomoRunning = false;
        _pomoRemainingPaused = PomoDuration(PomoPhase);
        Refresh();
    }

    public void PomoSkip() => AdvancePomodoro(completedNaturally: false);

    private void PomoSettingsChanged()
    {
        SavePrefs();
        if (!PomoRunning && !_loading) _pomoRemainingPaused = PomoDuration(PomoPhase);
        StateChanged?.Invoke();
    }

    private void AdvancePomodoro(bool completedNaturally)
    {
        var wasRunning = PomoRunning;
        if (PomoPhase == PomodoroPhase.Focus)
        {
            if (completedNaturally) CompletedPomodoros++;
            PomoPhase = completedNaturally && CompletedPomodoros > 0 && CompletedPomodoros % 4 == 0
                ? PomodoroPhase.LongBreak
                : PomodoroPhase.ShortBreak;
            SavePrefs();
        }
        else
        {
            PomoPhase = PomodoroPhase.Focus;
        }
        _pomoRemainingPaused = PomoDuration(PomoPhase);
        _pomoEnd = null;
        PomoRunning = false;
        // Skipping keeps the flow going; a natural end waits for the user.
        if (wasRunning && !completedNaturally) PomoToggle();
        else Refresh();
    }

    // MARK: - Countdown

    private double _countdownRemainingPaused = 10 * 60;
    private DateTime? _countdownEnd;

    public bool CountdownActive { get; private set; }
    public bool CountdownRunning { get; private set; }
    public double CountdownTotal { get; private set; } = 15 * 60;
    public int CustomMinutes { get; private set; } = 15;

    public double CountdownRemaining =>
        _countdownEnd is { } end ? Math.Max(0, (end - DateTime.UtcNow).TotalSeconds)
        : CountdownActive ? _countdownRemainingPaused
        : CustomMinutes * 60;

    public void CountdownStart(int minutes)
    {
        CountdownTotal = Math.Max(1, minutes) * 60;
        _countdownRemainingPaused = CountdownTotal;
        CountdownActive = true;
        _countdownEnd = DateTime.UtcNow.AddSeconds(CountdownTotal);
        CountdownRunning = true;
        Refresh();
    }

    public void CountdownToggle()
    {
        if (!CountdownActive)
        {
            CountdownStart(CustomMinutes);
            return;
        }
        if (CountdownRunning)
        {
            _countdownRemainingPaused = CountdownRemaining;
            _countdownEnd = null;
            CountdownRunning = false;
        }
        else
        {
            _countdownEnd = DateTime.UtcNow.AddSeconds(_countdownRemainingPaused);
            CountdownRunning = true;
        }
        Refresh();
    }

    public void CountdownCancel()
    {
        _countdownEnd = null;
        CountdownRunning = false;
        CountdownActive = false;
        CountdownTotal = CustomMinutes * 60;
        Refresh();
    }

    public void AdjustCustom(int minutes)
    {
        CustomMinutes = Math.Clamp(CustomMinutes + minutes, 1, 180);
        if (!CountdownActive) CountdownTotal = CustomMinutes * 60;
        StateChanged?.Invoke();
    }

    // MARK: - Stopwatch

    private DateTime? _stopwatchStart;
    private double _stopwatchAccumulated;
    private readonly List<double> _laps = new();

    public bool StopwatchRunning { get; private set; }
    /// Newest first.
    public IReadOnlyList<double> Laps => _laps;

    public double StopwatchElapsed =>
        _stopwatchAccumulated + (_stopwatchStart is { } start ? (DateTime.UtcNow - start).TotalSeconds : 0);

    public void StopwatchToggle()
    {
        if (_stopwatchStart is { } start)
        {
            _stopwatchAccumulated += (DateTime.UtcNow - start).TotalSeconds;
            _stopwatchStart = null;
            StopwatchRunning = false;
        }
        else
        {
            _stopwatchStart = DateTime.UtcNow;
            StopwatchRunning = true;
        }
        Refresh();
    }

    public void StopwatchLap()
    {
        if (!StopwatchRunning) return;
        _laps.Insert(0, StopwatchElapsed);
        if (_laps.Count > 99) _laps.RemoveAt(_laps.Count - 1);
        StateChanged?.Invoke();
    }

    public void StopwatchReset()
    {
        _stopwatchStart = null;
        _stopwatchAccumulated = 0;
        StopwatchRunning = false;
        _laps.Clear();
        Refresh();
    }

    // MARK: - Water

    private int _waterGoal;
    private bool _waterEnabled;
    private string _waterDay;

    public int Glasses { get; private set; }

    public int WaterGoal
    {
        get => _waterGoal;
        set { _waterGoal = Math.Max(1, value); SaveWater(); StateChanged?.Invoke(); }
    }

    /// Reminds every 30 minutes with a Windows notification while Purrch runs.
    public bool WaterEnabled
    {
        get => _waterEnabled;
        set
        {
            if (_waterEnabled == value) return;
            _waterEnabled = value;
            if (value) _waterReminder.Start(); else _waterReminder.Stop();
            SaveWater();
            StateChanged?.Invoke();
        }
    }

    /// Tapping the n-th drop fills up to it; tapping the last filled drop empties it.
    public void TapDrop(int index)
    {
        RolloverWaterDay();
        Glasses = Math.Clamp(index + 1 == Glasses ? index : index + 1, 0, _waterGoal);
        SaveWater();
        StateChanged?.Invoke();
    }

    private void RolloverWaterDay()
    {
        if (_waterDay == Today) return;
        _waterDay = Today;
        Glasses = 0;
    }

    private void SaveWater()
    {
        if (_loading) return;
        Store.Save("water", new Water { Glasses = Glasses, Goal = _waterGoal, Enabled = _waterEnabled, Day = _waterDay });
    }

    private void SavePrefs()
    {
        if (_loading) return;
        Store.Save("timers", new Prefs
        {
            FocusMinutes = _focusMinutes,
            BreakMinutes = _breakMinutes,
            LongBreakMinutes = LongBreakMinutes,
            Completed = CompletedPomodoros,
            CompletedDay = Today,
        });
    }

    // MARK: - Ticking and the closed pill

    /// "23:42" for the closed pill while a pomodoro or countdown runs.
    public string? LiveLabel { get; private set; }
    public string LiveGlyph { get; private set; } = "\uE916";

    private bool NeedsTicker => PomoRunning || CountdownRunning || StopwatchRunning;

    /// Starts or stops the one shared ticker and updates the live label.
    private void Refresh()
    {
        if (NeedsTicker) _ticker.Start(); else _ticker.Stop();
        UpdateLive();
        StateChanged?.Invoke();
    }

    private void Tick()
    {
        if (PomoRunning && PomoRemaining <= 0)
        {
            var finished = PomoPhase;
            AdvancePomodoro(completedNaturally: true);
            Finish(finished == PomodoroPhase.Focus ? "Focus session done" : "Break is over",
                   finished == PomodoroPhase.Focus ? $"Nice work. Time for a {PomoPhase.Title().ToLowerInvariant()}."
                                                   : "Ready for another focus session?");
        }
        if (CountdownRunning && CountdownRemaining <= 0)
        {
            _countdownEnd = null;
            CountdownRunning = false;
            CountdownActive = false;
            _countdownRemainingPaused = 0;
            Refresh();
            Finish("Timer finished", $"Your {Format.Duration(CountdownTotal)} timer is up.");
        }
        UpdateLive();
        Ticked?.Invoke();
    }

    private static void Finish(string title, string body)
    {
        PlayChime();
        TrayIcon.Current?.Notify(title, body);
        if (AppSettings.Shared.IsEnabled(ModuleId.Timers)) NotchState.Shared.Present(ModuleId.Timers);
    }

    private static void PlayChime()
    {
        var chime = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Alarm01.wav");
        try
        {
            if (File.Exists(chime)) new SoundPlayer(chime).Play();
            else SystemSounds.Exclamation.Play();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            SystemSounds.Exclamation.Play();
        }
    }

    private void UpdateLive()
    {
        string? label = null;
        var glyph = "\uE916";
        if (CountdownRunning)
        {
            label = Format.Duration(Math.Ceiling(CountdownRemaining));
        }
        else if (PomoRunning)
        {
            label = Format.Duration(Math.Ceiling(PomoRemaining));
            glyph = PomoPhase.Glyph();
        }
        if (label == LiveLabel && glyph == LiveGlyph) return;
        LiveLabel = label;
        LiveGlyph = glyph;
        LiveActivityHub.Shared.Refresh();
    }
}
