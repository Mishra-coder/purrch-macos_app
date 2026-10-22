using System.IO;
using System.Text.Json;

namespace Purrch.Core;

/// Small JSON files in %APPDATA%\Purrch, one per feature (timers.json, water.json, ...).
public static class Store
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static string PathFor(string name) => System.IO.Path.Combine(AppPaths.Root, name + ".json");

    public static T Load<T>(string name) where T : new()
    {
        try
        {
            var path = PathFor(name);
            if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? new T();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write($"{name}.json unreadable, using defaults: {e.Message}");
        }
        return new T();
    }

    public static void Save<T>(string name, T value)
    {
        try
        {
            // Write next to the real file, then swap, so a crash mid-write can't leave half a file.
            var path = PathFor(name);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"{name}.json not saved: {e.Message}");
        }
    }
}

public static class Format
{
    private static readonly string[] Units = { "bytes", "KB", "MB", "GB", "TB" };

    /// "512 bytes", "1.4 MB": decimal units, as the Mac app shows them.
    public static string Bytes(long count)
    {
        if (count < 1000) return count == 1 ? "1 byte" : $"{count} bytes";
        double value = count;
        var unit = 0;
        while (value >= 1000 && unit < Units.Length - 1) { value /= 1000; unit++; }
        return $"{value:0.#} {Units[unit]}";
    }

    /// "now", "5m", "3h", "2d".
    public static string Relative(DateTime date)
    {
        var seconds = (int)(DateTime.Now - date).TotalSeconds;
        if (seconds < 60) return "now";
        if (seconds < 3600) return $"{seconds / 60}m";
        if (seconds < 86400) return $"{seconds / 3600}h";
        return $"{seconds / 86400}d";
    }

    /// "4:05", or "1:04:05" from an hour up.
    public static string Duration(double seconds)
    {
        var s = Math.Max(0, (int)Math.Round(seconds));
        return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }
}
