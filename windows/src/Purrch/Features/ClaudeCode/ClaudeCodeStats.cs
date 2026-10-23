using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Purrch.Core;

namespace Purrch.Features.ClaudeCode;

/// One usage-bearing assistant line from a Claude Code transcript.
internal sealed record UsageRecord(string? Key, DateTime Date, long Tokens, long Output, int ToolCalls, string Session);

/// What one transcript file has given so far. Transcripts only grow, so a changed file is read from
/// <see cref="Offset"/> on rather than from the start.
internal sealed class TranscriptSummary
{
    public DateTime Modified { get; set; }
    /// Just past the last complete line read.
    public long Offset { get; set; }
    public List<UsageRecord> Records { get; } = new();
    public DateTime? Latest { get; set; }
    public string? LatestCwd { get; set; }
}

internal sealed class UsageSnapshot
{
    public long TodayTokens { get; set; }
    public long TodayOutput { get; set; }
    public int TodayTools { get; set; }
    public int TodaySessions { get; set; }
    public string? Project { get; set; }
    /// Local day -> tokens.
    public Dictionary<DateTime, long> Daily { get; } = new();
    public int Streak { get; set; }
    public DateTime? LastActivity { get; set; }
}

/// Today's Claude Code usage, read from the JSONL transcripts Claude Code keeps in %USERPROFILE%\.claude\projects.
/// Nothing leaves the PC. Parsing runs off the UI thread; unchanged files come from a cache.
public sealed class ClaudeCodeStats
{
    public static ClaudeCodeStats Shared { get; } = new();

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    private static readonly byte[] UsageMarker = Encoding.UTF8.GetBytes("\"usage\"");
    private static readonly byte[] CwdMarker = Encoding.UTF8.GetBytes("\"cwd\"");

    private Dictionary<string, TranscriptSummary> _cache = new(StringComparer.OrdinalIgnoreCase);
    private UsageSnapshot _snapshot = new();

    /// New numbers arrived, or loading started or stopped.
    public event Action? Changed;

    public long TodayTokens => _snapshot.TodayTokens;
    public long TodayOutput => _snapshot.TodayOutput;
    public int TodayTools => _snapshot.TodayTools;
    public int TodaySessions => _snapshot.TodaySessions;
    public string? Project => _snapshot.Project;
    public IReadOnlyDictionary<DateTime, long> Daily => _snapshot.Daily;
    public int Streak => _snapshot.Streak;
    public DateTime? LastActivity => _snapshot.LastActivity;
    public bool HasLoaded { get; private set; }
    public bool IsLoading { get; private set; }

    public static bool RootExists => Directory.Exists(Root);

    private ClaudeCodeStats() { }

    /// Call on the UI thread.
    public async void Refresh()
    {
        if (IsLoading) return;
        IsLoading = true;
        Changed?.Invoke();
        var oldCache = _cache;
        var (snapshot, newCache) = await Task.Run(() => Compute(oldCache));
        _cache = newCache;
        _snapshot = snapshot;
        HasLoaded = true;
        IsLoading = false;
        Changed?.Invoke();
    }

    // MARK: Parsing (off the UI thread)

    private static (UsageSnapshot, Dictionary<string, TranscriptSummary>) Compute(Dictionary<string, TranscriptSummary> cache)
    {
        var cutoff = DateTime.Now.AddDays(-120);
        var newCache = new Dictionary<string, TranscriptSummary>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        if (!RootExists) return (Aggregate(newCache.Values), newCache);
        try
        {
            foreach (var path in Directory.EnumerateFiles(Root, "*.jsonl", options))
            {
                // Hidden on the Mac means a dot in front; match that too.
                if (Path.GetFileName(path).StartsWith('.')) continue;
                FileInfo info;
                try { info = new FileInfo(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
                var modified = info.LastWriteTime;
                if (modified < cutoff) continue;
                cache.TryGetValue(path, out var cached);
                if (cached != null && cached.Modified == modified)
                {
                    newCache[path] = cached;
                    continue;
                }
                // A file shorter than what was read was rewritten; start over. The old summary stays with the
                // old cache, which the UI thread may still hand back if this run fails.
                var summary = cached != null && info.Length >= cached.Offset ? Copy(cached) : new TranscriptSummary();
                summary.Modified = modified;
                Parse(path, summary);
                newCache[path] = summary;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Claude Code: {e.Message}");
        }
        return (Aggregate(newCache.Values), newCache);
    }

    private static TranscriptSummary Copy(TranscriptSummary old)
    {
        var copy = new TranscriptSummary { Offset = old.Offset, Latest = old.Latest, LatestCwd = old.LatestCwd };
        copy.Records.AddRange(old.Records);
        return copy;
    }

    /// Reads complete lines from the summary's offset on. A last line without its newline is still being
    /// written; it is left for next time.
    private static void Parse(string path, TranscriptSummary summary)
    {
        byte[] data;
        try
        {
            // Claude Code may be writing the file right now.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (summary.Offset > stream.Length) summary.Offset = 0;
            stream.Position = summary.Offset;
            data = new byte[stream.Length - summary.Offset];
            stream.ReadExactly(data);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var start = 0;
        while (start < data.Length)
        {
            var newline = Array.IndexOf(data, (byte)'\n', start);
            if (newline < 0) break;
            ParseLine(data.AsMemory(start, newline - start), path, summary);
            start = newline + 1;
        }
        summary.Offset += start;
    }

    private static void ParseLine(ReadOnlyMemory<byte> line, string path, TranscriptSummary summary)
    {
        if (line.IsEmpty) return;
        var hasUsage = line.Span.IndexOf(UsageMarker) >= 0;
        if (!hasUsage && line.Span.IndexOf(CwdMarker) < 0) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var obj = doc.RootElement;
            if (obj.ValueKind != JsonValueKind.Object
                || !obj.TryGetProperty("timestamp", out var stamp) || stamp.ValueKind != JsonValueKind.String
                || !DateTime.TryParse(stamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
                return;
            date = date.Kind == DateTimeKind.Local ? date : date.ToLocalTime();

            if (StringOf(obj, "cwd") is { } cwd && date >= (summary.Latest ?? DateTime.MinValue))
            {
                summary.Latest = date;
                summary.LatestCwd = cwd;
            }
            if (!hasUsage || StringOf(obj, "type") != "assistant"
                || !obj.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                return;

            long Int(string name) => usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? (v.TryGetInt64(out var n) ? n : (long)v.GetDouble())
                : 0;
            var output = Int("output_tokens");
            var total = Int("input_tokens") + output + Int("cache_creation_input_tokens") + Int("cache_read_input_tokens");
            var tools = 0;
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var part in content.EnumerateArray())
                    if (part.ValueKind == JsonValueKind.Object && StringOf(part, "type") == "tool_use") tools++;
            var key = StringOf(message, "id") is { } id ? id + "|" + (StringOf(obj, "requestId") ?? "") : null;
            summary.Records.Add(new UsageRecord(key, date, total, output, tools, StringOf(obj, "sessionId") ?? path));
        }
        catch (JsonException)
        {
            // A line that isn't JSON: skip it, as the Mac app does.
        }
    }

    private static string? StringOf(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static UsageSnapshot Aggregate(IEnumerable<TranscriptSummary> summaries)
    {
        var today = DateTime.Today;
        var snapshot = new UsageSnapshot();
        var seen = new HashSet<string>();
        var sessions = new HashSet<string>();
        var latest = DateTime.MinValue;
        foreach (var summary in summaries)
        {
            if (summary.Latest is { } date && date > latest)
            {
                latest = date;
                snapshot.Project = summary.LatestCwd is { } cwd ? ProjectName(cwd) : null;
            }
            foreach (var record in summary.Records)
            {
                var isToday = record.Date >= today;
                // Tool calls are spread over the streamed copies of a message, so count every line.
                if (isToday) snapshot.TodayTools += record.ToolCalls;
                if (record.Key != null && !seen.Add(record.Key)) continue;
                var day = record.Date.Date;
                snapshot.Daily[day] = snapshot.Daily.GetValueOrDefault(day) + record.Tokens;
                if (isToday)
                {
                    snapshot.TodayTokens += record.Tokens;
                    snapshot.TodayOutput += record.Output;
                    sessions.Add(record.Session);
                }
            }
        }
        snapshot.TodaySessions = sessions.Count;
        snapshot.LastActivity = latest == DateTime.MinValue ? null : latest;
        var streakDay = snapshot.Daily.GetValueOrDefault(today) > 0 ? today : today.AddDays(-1);
        while (snapshot.Daily.GetValueOrDefault(streakDay) > 0)
        {
            snapshot.Streak++;
            streakDay = streakDay.AddDays(-1);
        }
        return snapshot;
    }

    /// "C:\Users\me\code\purrch" -> "purrch". Transcripts written on another system may use either slash.
    private static string ProjectName(string cwd)
    {
        var trimmed = cwd.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }
}

public static class CompactNumber
{
    /// 842_200_000 -> "842.2M", as the Mac app shows big numbers.
    public static string Format(double value)
    {
        var v = Math.Abs(value);
        var sign = value < 0 ? "-" : "";
        string F(double x, string suffix)
        {
            var s = x >= 100 ? x.ToString("0", CultureInfo.InvariantCulture) : x.ToString("0.0", CultureInfo.InvariantCulture);
            return sign + (s.EndsWith(".0") ? s[..^2] : s) + suffix;
        }
        if (v >= 1e12) return F(v / 1e12, "T");
        if (v >= 1e9) return F(v / 1e9, "B");
        if (v >= 1e6) return F(v / 1e6, "M");
        if (v >= 1e3) return F(v / 1e3, "K");
        return sign + ((long)v).ToString(CultureInfo.InvariantCulture);
    }
}
