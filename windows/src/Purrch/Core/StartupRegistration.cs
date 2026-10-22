using Microsoft.Win32;

namespace Purrch.Core;

/// Launch at startup through the per-user Run key, the same place Task Manager's Startup tab reads.
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// Where Task Manager records "Disabled" for a Run entry without deleting it.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Purrch";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string) return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // First byte 2 means enabled, 3 means switched off in Task Manager.
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } flags || flags[0] % 2 == 0;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            if (enabled)
            {
                run.SetValue(ValueName, Command);
                // Turning it on here also clears an earlier "Disabled" from Task Manager.
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            Log.Write($"Launch at startup: {e.Message}");
        }
    }

    /// Keeps the entry pointing at this copy of Purrch if the app was moved or updated in a new folder.
    public static void RefreshPath()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(ValueName) is string current && current != Command && IsEnabled) Set(true);
    }
}
