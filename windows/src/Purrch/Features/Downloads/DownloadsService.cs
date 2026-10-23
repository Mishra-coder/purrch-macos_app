using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using Purrch.Core;

namespace Purrch.Features.Downloads;

public sealed record DownloadItem(string Path, long Size, DateTime Date)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed class ActiveDownload
{
    public required string Path { get; init; }
    /// Name of the finished file. A Chromium browser's history replaces "Unconfirmed 123456" with the real one.
    public required string Name { get; set; }
    public long Bytes { get; set; }
    public long? Total { get; set; }
    /// 0 to 1. Real when <see cref="Total"/> is known, estimated otherwise.
    public double Progress { get; set; }
    /// Bytes per second, smoothed.
    public double Speed { get; set; }
    public DateTime LastChange { get; set; }
}

/// Watches the Downloads folder. Browsers write a download to a partial file (Chrome and Edge ".crdownload",
/// Firefox ".part") and rename it when done; those partial files become live progress rows, and the pill
/// shows a progress ring while any are present. For Chrome, Edge, Brave and Vivaldi the expected size comes
/// from the browser's history, so their rows show a real percentage and time left.
public sealed class DownloadsService
{
    public static DownloadsService Shared { get; } = new();

    private static readonly HashSet<string> PartialExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".download",
    };
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _completion = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly Dictionary<string, ActiveDownload> _tracked = new(StringComparer.OrdinalIgnoreCase);
    /// Being moved to the Recycle Bin right now; kept off the list so a scan meanwhile doesn't bring them back.
    private readonly HashSet<string> _recycling = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private DateTime _lastFullScan = DateTime.MinValue;
    private bool _started, _scanning, _rescan;
    /// Chromium downloads in progress, from the browsers' history; read at most every 2.5 seconds, and only
    /// while a ".crdownload" still has no known size.
    private IReadOnlyList<ChromiumDownloads.Entry> _browserDownloads = Array.Empty<ChromiumDownloads.Entry>();
    private DateTime _lastBrowserRead = DateTime.MinValue;
    private bool _readingBrowsers, _browsersUnavailable;

    /// Anything on screen may have changed: the lists, progress, or the Recycle Bin bar.
    public event Action? Changed;

    public string Folder { get; private set; } = ResolveFolder();
    public bool AccessDenied { get; private set; }
    public IReadOnlyList<DownloadItem> Recent { get; private set; } = Array.Empty<DownloadItem>();
    public IReadOnlyList<ActiveDownload> Active { get; private set; } = Array.Empty<ActiveDownload>();
    /// Finished during this session, newest first.
    public IReadOnlyList<DownloadItem> Completed { get; private set; } = Array.Empty<DownloadItem>();
    /// Files on their way to the Recycle Bin. They go after a few seconds, so "Undo" just keeps them.
    public IReadOnlyList<string> PendingTrash { get; private set; } = Array.Empty<string>();

    /// Average progress of everything downloading; 1 for a moment after the last one finishes.
    public double? ActiveProgress { get; private set; }
    public string? ActiveName { get; private set; }

    private DownloadsService()
    {
        _ticker.Tick += (_, _) => Tick();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Scan(); };
        _completion.Tick += (_, _) =>
        {
            _completion.Stop();
            if (_tracked.Count > 0) return;
            SetActivity(null, null);
        };
        _undoTimer.Tick += (_, _) => CommitTrash();
        LiveActivityHub.Shared.Register(() => ActiveProgress is { } progress
            ? new LiveActivity(LiveActivityKind.Download, ActiveName ?? "", Progress: progress)
            : null);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        StartWatching();
        Scan();
        _ticker.Start();
    }

    /// When the panel shows Downloads, or after "Try again": pick up a moved Downloads folder and look again.
    public void Rescan()
    {
        var folder = ResolveFolder();
        if (!string.Equals(folder, Folder, StringComparison.OrdinalIgnoreCase))
        {
            Folder = folder;
            StopWatching();
        }
        if (_watcher == null) StartWatching();
        Scan();
    }

    private void Tick()
    {
        // Fast polling while something downloads; a slow background refresh otherwise.
        if (_tracked.Count > 0 || (DateTime.Now - _lastFullScan).TotalSeconds > 15 || _watcher == null)
        {
            if (_watcher == null) StartWatching();
            Scan();
        }
    }

    // MARK: - Watching

    private void StartWatching()
    {
        if (!Directory.Exists(Folder)) return;
        try
        {
            // Names only: progress comes from the one-second poll, as on the Mac. Watching sizes too would
            // fire for every block a browser writes.
            var watcher = new FileSystemWatcher(Folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
                InternalBufferSize = 32 * 1024,
            };
            watcher.Created += OnFolderEvent;
            watcher.Deleted += OnFolderEvent;
            watcher.Renamed += OnFolderEvent;
            watcher.Error += (_, e) =>
            {
                // Buffer overflow (many files at once) just needs a rescan; a vanished folder needs a new watcher.
                Log.Write($"Downloads watcher: {e.GetException().Message}");
                _dispatcher.BeginInvoke(() =>
                {
                    if (!Directory.Exists(Folder)) StopWatching();
                    ScheduleScan();
                });
            };
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _watcher = null;
        }
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    // Raised on a thread-pool thread.
    private void OnFolderEvent(object sender, FileSystemEventArgs e) => _dispatcher.BeginInvoke(ScheduleScan);

    private void ScheduleScan()
    {
        if (!_debounce.IsEnabled) _debounce.Start();
    }

    // MARK: - Scan

    private sealed record Entry(string Path, string Name, bool IsDirectory, bool IsPartial, long Size, DateTime Date, DateTime Modified);

    /// Lists the folder off the UI thread, then works out progress, finished files and the recent list here.
    private async void Scan()
    {
        if (_scanning) { _rescan = true; return; }
        _scanning = true;
        _lastFullScan = DateTime.Now;
        var folder = Folder;
        var entries = await Task.Run(() => List(folder));
        _scanning = false;

        if (folder == Folder) Apply(entries);
        if (_rescan)
        {
            _rescan = false;
            Scan();
        }
    }

    /// Null when the folder can't be read.
    private static List<Entry>? List(string folder)
    {
        try
        {
            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
            };
            var list = new List<Entry>();
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                var isDirectory = info is DirectoryInfo;
                var isPartial = !isDirectory && PartialExtensions.Contains(info.Extension);
                long size = 0;
                if (info is FileInfo file)
                {
                    // The listing's size lags behind for files still open for writing; ask the file itself.
                    if (isPartial) file.Refresh();
                    size = file.Exists ? file.Length : 0;
                }
                var created = info.CreationTime;
                var modified = info.LastWriteTime;
                list.Add(new Entry(info.FullName, info.Name, isDirectory, isPartial, size,
                    created > modified ? created : modified, modified));
            }
            return list;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            Log.Write($"Downloads unreadable: {e.Message}");
            return null;
        }
    }

    private void Apply(List<Entry>? entries)
    {
        if (entries == null)
        {
            AccessDenied = true;
            Recent = Array.Empty<DownloadItem>();
            Changed?.Invoke();
            return;
        }
        AccessDenied = false;

        var now = DateTime.Now;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var finished = new List<DownloadItem>();

        foreach (var entry in entries.Where(e => e.IsPartial))
        {
            seen.Add(entry.Path);
            var bytes = entry.Size;
            if (!_tracked.TryGetValue(entry.Path, out var item))
            {
                item = new ActiveDownload
                {
                    Path = entry.Path,
                    Name = Path.GetFileNameWithoutExtension(entry.Name),
                    Bytes = bytes,
                    Progress = 0.02,
                    LastChange = now,
                };
                _tracked[entry.Path] = item;
            }
            else
            {
                var dt = (now - item.LastChange).TotalSeconds;
                if (bytes != item.Bytes && dt > 0)
                {
                    var instant = Math.Max(0, bytes - item.Bytes) / dt;
                    item.Speed = item.Speed == 0 ? instant : item.Speed * 0.6 + instant * 0.4;
                    item.LastChange = now;
                }
                else if (dt > 3)
                {
                    item.Speed = 0;
                }
                // Firefox doesn't record the expected size anywhere Purrch can read, and a Chromium download
                // reaches the history a few seconds late: creep towards 95% as data keeps arriving.
                var step = bytes > 0 ? 0.04 : 0.01;
                if (item.Total == null) item.Progress = Math.Min(0.95, item.Progress + (0.95 - item.Progress) * step);
            }
            item.Bytes = bytes;
            if (BrowserEntry(entry.Path) is { } known)
            {
                if (known.Total > 0) item.Total = known.Total;
                if (known.TargetPath.Length > 0) item.Name = Path.GetFileName(known.TargetPath);
            }
            if (item.Total is { } total and > 0) item.Progress = Math.Min(1, (double)bytes / total);
        }

        // Anything no longer partial has either finished or been cancelled.
        foreach (var (path, item) in _tracked.Where(t => !seen.Contains(t.Key)).ToList())
        {
            _tracked.Remove(path);
            var final = path[..^Path.GetExtension(path).Length];
            var done = entries.FirstOrDefault(e => !e.IsPartial && string.Equals(e.Path, final, StringComparison.OrdinalIgnoreCase));
            // Chrome's "Unconfirmed 123.crdownload" ends up with a different name.
            var stem = item.Name.Split('.')[0];
            done ??= entries.FirstOrDefault(e => !e.IsPartial
                && e.Name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                && (now - e.Modified).TotalSeconds < 10);
            if (done != null) finished.Add(new DownloadItem(done.Path, done.IsDirectory ? 0 : done.Size, now));
        }

        Active = _tracked.Values.OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToList();
        if (_tracked.Count == 0) _browserDownloads = Array.Empty<ChromiumDownloads.Entry>();
        else ReadBrowsersIfNeeded();

        // Firefox keeps an empty placeholder under the final name while the ".part" fills up; hide it until done.
        var placeholders = new HashSet<string>(
            entries.Where(e => e.IsPartial).Select(e => e.Path[..^Path.GetExtension(e.Path).Length]),
            StringComparer.OrdinalIgnoreCase);
        var trashing = new HashSet<string>(PendingTrash.Concat(_recycling), StringComparer.OrdinalIgnoreCase);
        Recent = entries
            .Where(e => !e.IsPartial && !placeholders.Contains(e.Path) && !trashing.Contains(e.Path))
            .OrderByDescending(e => e.Date)
            .Take(20)
            .Select(e => new DownloadItem(e.Path, e.IsDirectory ? 0 : e.Size, e.Date))
            .ToList();
        // Finished files that have since been moved or deleted drop out of "Just finished".
        var present = new HashSet<string>(entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        if (Completed.Any(c => !present.Contains(c.Path)))
            Completed = Completed.Where(c => present.Contains(c.Path)).ToList();

        if (finished.Count > 0) Complete(finished);
        PublishLiveActivity();
        Changed?.Invoke();
    }

    // MARK: - Chromium history

    /// The history row for a ".crdownload": by the file being written, or by the name it will get.
    private ChromiumDownloads.Entry? BrowserEntry(string partial) =>
        _browserDownloads.FirstOrDefault(d => SamePath(d.CurrentPath, partial)
            || (d.TargetPath.Length > 0 && SamePath(d.TargetPath + ".crdownload", partial)));

    /// Looks the sizes up in the browsers' history, off the UI thread, while a ".crdownload" has none yet.
    /// The next scan (a second later while downloading) uses what it found.
    private async void ReadBrowsersIfNeeded()
    {
        if (_readingBrowsers || _browsersUnavailable || (DateTime.Now - _lastBrowserRead).TotalSeconds < 2.5) return;
        if (!_tracked.Values.Any(a => a.Total == null && a.Path.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))) return;
        _readingBrowsers = true;
        try
        {
            _browserDownloads = await Task.Run(() => ChromiumDownloads.InProgress());
        }
        // SQLite's native library missing or blocked: estimates it is, as before.
        catch (Exception e) when (e is TypeInitializationException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            Log.Write($"Browser history unavailable: {e.Message}");
            _browsersUnavailable = true;
        }
        _lastBrowserRead = DateTime.Now;
        _readingBrowsers = false;
    }

    private void Complete(List<DownloadItem> items)
    {
        Completed = items.Concat(Completed.Where(c => !items.Any(i => SamePath(i.Path, c.Path)))).Take(5).ToList();
        if (_tracked.Count > 0) return;
        // Show a full ring briefly in the closed pill.
        SetActivity(1, items[^1].Name);
        _completion.Stop();
        _completion.Start();
    }

    private void PublishLiveActivity()
    {
        if (_tracked.Count == 0)
        {
            if (ActiveProgress is < 1) SetActivity(null, null);
            return;
        }
        _completion.Stop();
        var items = _tracked.Values.ToList();
        SetActivity(items.Average(i => i.Progress), items.Count == 1 ? items[0].Name : $"{items.Count} downloads");
    }

    private void SetActivity(double? progress, string? name)
    {
        if (progress == ActiveProgress && name == ActiveName) return;
        ActiveProgress = progress;
        ActiveName = name;
        LiveActivityHub.Shared.Refresh();
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // MARK: - Recycle Bin

    /// Takes the files off the list now and sends them to the Recycle Bin (never deletes outright) after a few
    /// seconds, unless "Undo" is pressed first.
    public void Trash(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        CommitTrash();
        PendingTrash = paths.ToList();
        Recent = Recent.Where(r => !paths.Contains(r.Path, StringComparer.OrdinalIgnoreCase)).ToList();
        Completed = Completed.Where(c => !paths.Contains(c.Path, StringComparer.OrdinalIgnoreCase)).ToList();
        _undoTimer.Stop();
        _undoTimer.Start();
        Changed?.Invoke();
    }

    public void UndoTrash()
    {
        _undoTimer.Stop();
        PendingTrash = Array.Empty<string>();
        Changed?.Invoke();
        Scan();
    }

    /// Recycles whatever is still waiting, now. Called when Purrch quits.
    public void Flush()
    {
        _undoTimer.Stop();
        var paths = PendingTrash;
        PendingTrash = Array.Empty<string>();
        Recycle(paths);
    }

    private async void CommitTrash()
    {
        _undoTimer.Stop();
        var paths = PendingTrash;
        if (paths.Count == 0) return;
        PendingTrash = Array.Empty<string>();
        _recycling.UnionWith(paths);
        Changed?.Invoke();
        await Task.Run(() => Recycle(paths));
        _recycling.ExceptWith(paths);
        Scan();
    }

    private static void Recycle(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                    FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
                else if (File.Exists(path))
                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException or System.Security.SecurityException)
            {
                Log.Write($"Couldn't move {Path.GetFileName(path)} to the Recycle Bin: {e.Message}");
            }
        }
    }

    // MARK: - Folder

    /// The real Downloads folder, wherever the user has moved it (Properties, Location in File Explorer).
    private static string ResolveFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out var pointer) == 0)
            {
                try
                {
                    if (Marshal.PtrToStringUni(pointer) is { Length: > 0 } path) return path;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not on Windows; falls through to the usual place.
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
