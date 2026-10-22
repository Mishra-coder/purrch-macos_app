using System.IO;

namespace Purrch.Core;

/// Where Purrch keeps its files: %APPDATA%\Purrch.
public static class AppPaths
{
    public static string Root { get; } = CreateRoot();

    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Log => Path.Combine(Root, "purrch.log");

    private static string CreateRoot()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Purrch");
        Directory.CreateDirectory(root);
        return root;
    }
}

/// Append-only log in %APPDATA%\Purrch\purrch.log, for diagnosing problems on machines we can't debug.
public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(AppPaths.Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
    }
}
