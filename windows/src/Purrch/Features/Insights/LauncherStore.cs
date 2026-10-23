using System.Diagnostics;
using System.IO;
using Purrch.Core;

namespace Purrch.Features.Insights;

/// The apps in Home's Launch widget: programs or Start menu shortcuts, saved in %APPDATA%\Purrch\launcher.json.
public sealed class LauncherStore
{
    public static LauncherStore Shared { get; } = new();

    /// Opens Windows Settings; it is an app with no file of its own.
    public const string SettingsApp = "ms-settings:";

    public event Action? Changed;

    public IReadOnlyList<string> Paths => _paths;

    private readonly List<string> _paths;

    private LauncherStore()
    {
        var saved = Store.Load<List<string>?>("launcher", null);
        _paths = (saved ?? Defaults()).Where(Exists).ToList();
    }

    /// Windows counterparts of the Mac defaults: browser, mail, notes, calculator, terminal and settings.
    private static List<string> Defaults()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.SystemDirectory;
        return new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(windows, "explorer.exe"),
            Path.Combine(system, "notepad.exe"),
            Path.Combine(system, "calc.exe"),
            Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe"),
            SettingsApp,
        };
    }

    private static bool Exists(string path) => path == SettingsApp || File.Exists(path);

    /// "Notepad" for notepad.exe or Notepad.lnk; the exe's own description when it has one.
    public static string Name(string path)
    {
        if (path == SettingsApp) return "Settings";
        if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(description)) return description;
            }
            catch (FileNotFoundException) { }
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    public void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(p => !_paths.Contains(p, StringComparer.OrdinalIgnoreCase))) _paths.Add(path);
        Save();
    }

    public void Remove(string path)
    {
        _paths.Remove(path);
        Save();
    }

    public void Launch(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"Launch {path}: {e.Message}");
        }
        NotchState.Shared.Close();
    }

    private void Save()
    {
        Store.Save("launcher", _paths);
        Changed?.Invoke();
    }
}
