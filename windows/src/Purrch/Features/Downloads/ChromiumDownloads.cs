using System.IO;
using Microsoft.Data.Sqlite;
using Purrch.Core;

namespace Purrch.Features.Downloads;

/// What Chrome, Edge, Brave and Vivaldi know about their downloads in progress, above all the expected size.
/// They keep it in each profile's History database rather than next to the ".crdownload" file, so Purrch reads
/// a copy of that database (the browser keeps the original locked).
internal static class ChromiumDownloads
{
    /// One in-progress row: the ".crdownload" being written, the name it will get, and bytes so far and expected.
    public sealed record Entry(string CurrentPath, string TargetPath, long Received, long Total);

    private static readonly string[] UserDataFolders =
    {
        @"Google\Chrome\User Data",
        @"Microsoft\Edge\User Data",
        @"BraveSoftware\Brave-Browser\User Data",
        @"Vivaldi\User Data",
    };

    // The last read of each History file, keyed by its size and write time (and its -wal's), so an unchanged
    // database isn't copied again. Only one read runs at a time (see DownloadsService).
    private static readonly Dictionary<string, (string Stamp, List<Entry> Rows)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Logged = new(StringComparer.OrdinalIgnoreCase);

    /// Downloads in progress in every profile of every Chromium browser installed. Call off the UI thread.
    public static List<Entry> InProgress()
    {
        var all = new List<Entry>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var userData in UserDataFolders.Select(f => Path.Combine(local, f)).Where(Directory.Exists))
        {
            List<string> profiles;
            try
            {
                // "Default", "Profile 1", ...; other folders there (caches, crash reports) have no History.
                profiles = Directory.EnumerateDirectories(userData).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }
            foreach (var profile in profiles)
            {
                var history = Path.Combine(profile, "History");
                if (File.Exists(history)) all.AddRange(Read(history));
            }
        }
        return all;
    }

    private static List<Entry> Read(string history)
    {
        var stamp = Stamp(history);
        var known = Cache.TryGetValue(history, out var cached);
        if (known && cached.Stamp == stamp) return cached.Rows;
        // Unreadable this time: keep the last answer, and try again on the next call.
        if (Query(history) is not { } rows) return known ? cached.Rows : new List<Entry>();
        Cache[history] = (stamp, rows);
        return rows;
    }

    private static string Stamp(string history) => string.Join("|", new[] { history, history + "-wal" }.Select(path =>
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "-";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "?";
        }
    }));

    /// Copies the database (and its write-ahead log, if any) to a temporary folder and reads the copy.
    /// Null when it couldn't be read this time, for example while the browser was half way through a write.
    private static List<Entry>? Query(string history)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Purrch-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, "History");
            CopyShared(history, copy);
            if (File.Exists(history + "-wal")) CopyShared(history + "-wal", copy + "-wal");

            var connection = new SqliteConnectionStringBuilder
            {
                DataSource = copy,
                // Read-write on the copy, so SQLite can fold the log in; pooling off, so the folder can be deleted.
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            };
            using var db = new SqliteConnection(connection.ToString());
            db.Open();
            using var command = db.CreateCommand();
            // state 0 is IN_PROGRESS.
            command.CommandText = "SELECT current_path, target_path, received_bytes, total_bytes FROM downloads WHERE state = 0";
            using var reader = command.ExecuteReader();
            var rows = new List<Entry>();
            while (reader.Read())
            {
                rows.Add(new Entry(
                    reader.IsDBNull(0) ? "" : reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt64(3)));
            }
            return rows;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException
                                      or InvalidOperationException or System.Security.SecurityException)
        {
            // Once per database, not every few seconds.
            if (Logged.Add(history)) Log.Write($"Couldn't read {history}: {e.Message}");
            return null;
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// The browser has the file open for writing; a plain File.Copy asks for exclusive read access and fails.
    private static void CopyShared(string from, string to)
    {
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = new FileStream(to, FileMode.CreateNew, FileAccess.Write);
        source.CopyTo(target);
    }
}
