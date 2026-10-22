using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Purrch.Core;

namespace Purrch.Features.Insights;

public sealed record BatteryInfo(
    bool Present,
    double Level,
    bool IsCharging,
    bool OnAC,
    double? Health,
    int? MinutesRemaining);

/// Samples CPU, memory, disk, network and battery once a second while the System module is on screen.
public sealed class SystemMonitor
{
    public static SystemMonitor Shared { get; } = new();

    private const int HistoryLength = 60;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ulong _lastIdle, _lastTotal;
    private long _lastDown, _lastUp;
    private DateTime _lastNetSample;
    private int _users;

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
        var minutes = power.BatteryLifeTime >= 0 && !onAC ? power.BatteryLifeTime / 60 : (int?)null;
        Battery = new BatteryInfo(true, level, (power.BatteryFlag & Charging) != 0, onAC, Health(), minutes);
    }

    /// Full-charge capacity against design capacity, as Windows reports it for the combined battery.
    private static double? Health()
    {
        try
        {
            var report = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            if (report.FullChargeCapacityInMilliwattHours is int full && report.DesignCapacityInMilliwattHours is int design && design > 0)
                return Math.Clamp((double)full / design, 0, 1.2);
        }
        catch (Exception)
        {
            // Not every device exposes capacities.
        }
        return null;
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

    public static string Gb(ulong bytes) => $"{bytes / 1_073_741_824.0:0.0} GB";

    /// "Windows 11 (22631)" or "Windows 10 (19045)".
    public static string WindowsVersion()
    {
        var v = Environment.OSVersion.Version;
        return $"Windows {(v.Build >= 22000 ? 11 : 10)} ({v.Build})";
    }
}
