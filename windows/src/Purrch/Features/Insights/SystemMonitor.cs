using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using Purrch.Core;

namespace Purrch.Features.Insights;

public sealed record BatteryInfo(
    bool Present,
    double Level,
    bool IsCharging,
    bool OnAC,
    double? Health,
    /// Minutes to empty on battery, or to full while charging, when Windows can tell.
    int? MinutesRemaining,
    int? CycleCount = null)
{
    /// Segoe battery glyph for the level, with the charging bolt while charging.
    public string Glyph
    {
        get
        {
            if (!Present) return "\uE83E";
            var tenths = (int)Math.Floor(Math.Clamp(Level, 0, 1) * 10);
            // Battery0-9 run from E850, BatteryCharging0-8 from E85A; the full ones sit apart.
            if (IsCharging) return tenths >= 9 ? "\uE83E" : ((char)(0xE85A + tenths)).ToString();
            return tenths >= 10 ? "\uE83F" : ((char)(0xE850 + tenths)).ToString();
        }
    }
}

/// Windows has no thermal state for apps, so this is estimated from how far the CPU is held below its top speed.
public enum ThermalState { Unknown, Nominal, Fair, Serious, Critical }

/// Samples CPU, memory, disk, network, battery and (estimated) thermal state once a second while the System
/// module is on screen. The battery cycle count comes from WMI, once a minute, off the UI thread.
public sealed class SystemMonitor
{
    public static SystemMonitor Shared { get; } = new();

    private const int HistoryLength = 60;
    private static readonly TimeSpan CycleInterval = TimeSpan.FromMinutes(1);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ulong _lastIdle, _lastTotal;
    private long _lastDown, _lastUp;
    private DateTime _lastNetSample;
    private int _users;
    private int? _cycles;
    private DateTime _cyclesQueried = DateTime.MinValue;
    private bool _cyclesPending;

    public event Action? Updated;

    public double Cpu { get; private set; }
    public List<double> CpuHistory { get; } = new();
    public ulong MemoryTotal { get; private set; }
    public ulong MemoryUsed { get; private set; }
    public double MemoryFraction => MemoryTotal == 0 ? 0 : (double)MemoryUsed / MemoryTotal;
    public long DiskTotal { get; private set; }
    public long DiskFree { get; private set; }
    public double DiskFraction => DiskTotal == 0 ? 0 : 1 - (double)DiskFree / DiskTotal;
    public string DiskName { get; private set; } = "C:";
    /// Bytes per second.
    public double NetDown { get; private set; }
    public double NetUp { get; private set; }
    public List<double> NetDownHistory { get; } = new();
    public List<double> NetUpHistory { get; } = new();
    public BatteryInfo Battery { get; private set; } = new(false, 0, false, true, null, null);
    public ThermalState Thermal { get; private set; }
    public TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    private SystemMonitor()
    {
        _timer.Tick += (_, _) => Sample();
    }

    /// Views call Start when they appear and Stop when they go; sampling runs while anyone watches.
    public void Start()
    {
        if (_users++ > 0) return;
        Prime();
        Sample();
        _timer.Start();
    }

    public void Stop()
    {
        if (_users == 0 || --_users > 0) return;
        _timer.Stop();
    }

    private void Prime()
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            _lastIdle = idle.Value;
            _lastTotal = kernel.Value + user.Value;
        }
        (_lastDown, _lastUp) = NetworkTotals();
        _lastNetSample = DateTime.UtcNow;
    }

    private void Sample()
    {
        SampleCpu();
        SampleMemory();
        SampleDisk();
        SampleNetwork();
        SampleBattery();
        SampleThermal();
        Updated?.Invoke();
    }

    private void SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return;
        // Kernel time includes idle time, so busy = total - idle.
        var total = kernel.Value + user.Value;
        var dTotal = total - _lastTotal;
        var dIdle = idle.Value - _lastIdle;
        _lastIdle = idle.Value;
        _lastTotal = total;
        if (dTotal == 0) return;
        Cpu = Math.Clamp(1 - (double)dIdle / dTotal, 0, 1);
        Push(CpuHistory, Cpu);
    }

    private void SampleMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return;
        MemoryTotal = status.ullTotalPhys;
        MemoryUsed = status.ullTotalPhys - status.ullAvailPhys;
    }

    private void SampleDisk()
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var drive = new DriveInfo(root);
            DiskTotal = drive.TotalSize;
            DiskFree = drive.AvailableFreeSpace;
            DiskName = root.TrimEnd('\\');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Leave the last reading.
        }
    }

    private void SampleNetwork()
    {
        var (down, up) = NetworkTotals();
        var now = DateTime.UtcNow;
        var seconds = Math.Max(0.001, (now - _lastNetSample).TotalSeconds);
        // Counters reset when an adapter reconnects; don't show a negative rate.
        NetDown = Math.Max(0, (down - _lastDown) / seconds);
        NetUp = Math.Max(0, (up - _lastUp) / seconds);
        (_lastDown, _lastUp, _lastNetSample) = (down, up, now);
        Push(NetDownHistory, NetDown);
        Push(NetUpHistory, NetUp);
    }

    private static (long Down, long Up) NetworkTotals()
    {
        long down = 0, up = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                down += stats.BytesReceived;
                up += stats.BytesSent;
            }
        }
        catch (NetworkInformationException)
        {
        }
        return (down, up);
    }

    private void SampleBattery()
    {
        if (!GetSystemPowerStatus(out var power)) return;
        const byte NoBattery = 128, Charging = 8, Unknown = 255;
        if (power.BatteryFlag == Unknown || (power.BatteryFlag & NoBattery) != 0)
        {
            Battery = new BatteryInfo(false, 0, false, true, null, null);
            return;
        }
        var level = power.BatteryLifePercent <= 100 ? power.BatteryLifePercent / 100.0 : 0;
        var onAC = power.ACLineStatus == 1;
        var charging = (power.BatteryFlag & Charging) != 0;
        var (health, toFull) = Capacities();
        var minutes = charging ? toFull
            : power.BatteryLifeTime >= 0 && !onAC ? power.BatteryLifeTime / 60 : (int?)null;
        RefreshCycles();
        Battery = new BatteryInfo(true, level, charging, onAC, health, minutes, _cycles);
    }

    /// Full-charge capacity against design capacity, and minutes until full at the current charge rate,
    /// as Windows reports them for the combined battery.
    private static (double? Health, int? ToFull) Capacities()
    {
        double? health = null;
        int? toFull = null;
        try
        {
            var report = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            var full = report.FullChargeCapacityInMilliwattHours;
            if (full is int f && report.DesignCapacityInMilliwattHours is int design && design > 0)
                health = Math.Clamp((double)f / design, 0, 1.2);
            // The charge rate is positive while charging, in milliwatts; capacities are in milliwatt-hours.
            if (full is int capacity && report.RemainingCapacityInMilliwattHours is int remaining
                && report.ChargeRateInMilliwatts is int rate && rate > 0 && capacity > remaining)
                toFull = (int)Math.Round((double)(capacity - remaining) / rate * 60);
        }
        catch (Exception)
        {
            // Not every device exposes capacities.
        }
        return (health, toFull);
    }

    /// Asks WMI for the cycle count at most once a minute. WMI can take a moment, so it runs on the thread
    /// pool and the next sample picks the answer up.
    private void RefreshCycles()
    {
        if (_cyclesPending || DateTime.UtcNow - _cyclesQueried < CycleInterval) return;
        _cyclesPending = true;
        _cyclesQueried = DateTime.UtcNow;
        var dispatcher = Dispatcher.CurrentDispatcher;
        Task.Run(QueryCycles).ContinueWith(t => dispatcher.BeginInvoke(() =>
        {
            _cycles = t.Result;
            _cyclesPending = false;
        }));
    }

    /// The battery driver's cycle count. Many drivers don't keep one and report 0; that counts as unknown.
    private static int? QueryCycles()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CycleCount FROM BatteryCycleCount");
            using var results = searcher.Get();
            foreach (var item in results)
            {
                using (item)
                {
                    if (item["CycleCount"] is { } value && Convert.ToInt32(value) is var count and > 0) return count;
                }
            }
        }
        catch (Exception e)
        {
            // The class is missing on desktops and on some laptops' drivers.
            Log.Write($"Battery cycle count unavailable: {e.Message}");
        }
        return null;
    }

    /// Estimates the thermal state from CPU throttling: how far each core's speed limit (and, under load,
    /// its current speed) sits below its maximum. Unknown when Windows won't say.
    private void SampleThermal()
    {
        var count = Environment.ProcessorCount;
        var info = new PROCESSOR_POWER_INFORMATION[count];
        var size = (uint)(Marshal.SizeOf<PROCESSOR_POWER_INFORMATION>() * count);
        if (CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, info, size) != 0)
        {
            Thermal = ThermalState.Unknown;
            return;
        }
        double max = 0, limit = 0, current = 0;
        foreach (var p in info)
        {
            max += p.MaxMhz;
            limit += Math.Min(p.MhzLimit, p.MaxMhz);
            current += Math.Min(p.CurrentMhz, p.MaxMhz);
        }
        if (max <= 0 || limit <= 0)
        {
            Thermal = ThermalState.Unknown;
            return;
        }
        // An idle CPU slows down to save power, which isn't throttling; only trust the current speed when busy.
        var ratio = limit / max;
        if (Cpu >= 0.5 && current > 0) ratio = Math.Min(ratio, current / max);
        Thermal = ratio >= 0.9 ? ThermalState.Nominal : ratio >= 0.7 ? ThermalState.Fair : ThermalState.Serious;
    }

    private static void Push(List<double> history, double value)
    {
        history.Add(value);
        if (history.Count > HistoryLength) history.RemoveAt(0);
    }

    // MARK: - Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME64
    {
        public uint Low, High;
        public ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSOR_POWER_INFORMATION
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    private const int ProcessorInformation = 11;

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLength,
                                                      [Out] PROCESSOR_POWER_INFORMATION[] output, uint outputLength);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FILETIME64 idle, out FILETIME64 kernel, out FILETIME64 user);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}

public static class SystemFormat
{
    public static string Rate(double bytesPerSecond) => Format.Bytes((long)bytesPerSecond) + "/s";

    public static string Uptime(TimeSpan t)
    {
        if (t.Days > 0) return $"{t.Days}d {t.Hours}h";
        if (t.Hours > 0) return $"{t.Hours}h {t.Minutes}m";
        return $"{t.Minutes}m";
    }

    /// Label and dot colour, the same as the Mac app's: green, yellow, orange, red.
    public static (string Label, Color Colour) Thermal(ThermalState state) => state switch
    {
        ThermalState.Nominal => ("Nominal", Color.FromRgb(0x30, 0xD1, 0x58)),
        ThermalState.Fair => ("Fair", Color.FromRgb(0xFF, 0xD6, 0x0A)),
        ThermalState.Serious => ("Serious", Color.FromRgb(0xFF, 0x9F, 0x0A)),
        ThermalState.Critical => ("Critical", Color.FromRgb(0xFF, 0x45, 0x3A)),
        _ => ("Normal", Color.FromRgb(0x8E, 0x8E, 0x93)),
    };

    public static string Gb(ulong bytes) => $"{bytes / 1_073_741_824.0:0.0} GB";

    /// "Windows 11 (22631)" or "Windows 10 (19045)".
    public static string WindowsVersion()
    {
        var v = Environment.OSVersion.Version;
        return $"Windows {(v.Build >= 22000 ? 11 : 10)} ({v.Build})";
    }
}
