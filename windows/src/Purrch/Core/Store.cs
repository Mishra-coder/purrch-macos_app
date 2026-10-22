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
    /// "4:05", or "1:04:05" from an hour up.
    public static string Duration(double seconds)
    {
        var s = Math.Max(0, (int)Math.Round(seconds));
        return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }
}
