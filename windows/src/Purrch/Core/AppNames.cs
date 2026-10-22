using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Purrch.Core;

public static class AppNames
{
    /// Friendly name of the app that owns a window, e.g. "Microsoft Edge" or "Notepad"; null for Purrch itself.
    public static string? Of(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;
            using var process = Process.GetProcessById((int)pid);
            if (process.Id == Environment.ProcessId) return null;
            try
            {
                var description = process.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Elevated or protected process: fall back to its name.
            }
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
