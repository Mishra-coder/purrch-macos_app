using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Purrch.Core;

namespace Purrch.Features.Captures;

public sealed record CaptureFile(string Path, DateTime Date, long Size, long Modified)
{
    public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.');
    public bool IsVideo => CapturesModel.VideoExtensions.Contains(System.IO.Path.GetExtension(Path));
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

public enum CaptureFilter { All, Screenshots, Recordings }

public static class CaptureFilterInfo
{
    public static IReadOnlyList<CaptureFilter> All { get; } = Enum.GetValues<CaptureFilter>();

    public static string Title(this CaptureFilter f) => f.ToString();

    public static bool Matches(this CaptureFilter f, CaptureFile file) => f switch
    {
        CaptureFilter.Screenshots => !file.IsVideo,
        CaptureFilter.Recordings => file.IsVideo,
        _ => true,
    };
}

/// Screenshots and screen recordings, newest first, from the folders Windows saves them to.
/// One per Captures view, like the Mac app's model; it watches the folders only while on screen.
public sealed class CapturesModel
{
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".heic" };
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov" };

    /// Newest captures kept in the grid; older ones stay in the folder.
    private const int Limit = 500;
    private static readonly Guid ScreenshotsFolderId = new("b7bede81-df94-4682-a7d8-57a52620b86f");
    private const uint KF_FLAG_DONT_VERIFY = 0x4000;
    private const uint KF_FLAG_CREATE = 0x8000;

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);
    private List<CaptureFile> _found = new();
    private bool _scanning, _rescan;

    /// Raised when the list, the loaded flag or the undo offer changes.
    public event Action? Changed;

    public IReadOnlyList<CaptureFile> Files { get; private set; } = Array.Empty<CaptureFile>();
    public bool Loaded { get; private set; }
    /// Captures just deleted. They go to the Recycle Bin once the Undo offer runs out.
    public IReadOnlyList<CaptureFile> LastTrashed { get; private set; } = Array.Empty<CaptureFile>();

    public CapturesModel()
    {
        _poll.Tick += (_, _) => Refresh();
        _settle.Tick += (_, _) => { _settle.Stop(); Refresh(); };
        _undoTimer.Tick += (_, _) => CommitTrash();
    }

    // MARK: - Folders

    /// Win+PrtScn and the Snipping Tool save here. The Pictures folder may live in OneDrive.
    public static string ScreenshotsFolder
    {
        get
        {
            if (SHGetKnownFolderPath(ScreenshotsFolderId, KF_FLAG_DONT_VERIFY, IntPtr.Zero, out var path) == 0
                && !string.IsNullOrEmpty(path))
                return path;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        }
    }

    private static string Videos => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    /// The Snipping Tool's recordings, then Xbox Game Bar's captures.
    public static string[] RecordingFolders => new[] { Path.Combine(Videos, "Screen Recordings"), Path.Combine(Videos, "Captures") };

    private static IEnumerable<string> Folders() =>
        new[] { ScreenshotsFolder }.Concat(RecordingFolders)
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// The folder "Open folder" shows: the recordings one on the Recordings filter, if it exists.
    public static string FolderFor(CaptureFilter filter)
    {
        if (filter == CaptureFilter.Recordings && RecordingFolders.FirstOrDefault(Directory.Exists) is { } recordings) return recordings;
        var screenshots = ScreenshotsFolder;
        return Directory.Exists(screenshots) ? screenshots : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    // MARK: - Watching

    public void Start()
    {
        Watch();
        _poll.Start();
        // Quitting during the Undo offer still sends the deleted captures to the Recycle Bin.
        Dispatcher.CurrentDispatcher.ShutdownStarted += OnShutdown;
        Refresh();
    }

    public void Stop()
    {
        Dispatcher.CurrentDispatcher.ShutdownStarted -= OnShutdown;
        _poll.Stop();
        _settle.Stop();
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        CommitTrash();
    }

    private void OnShutdown(object? sender, EventArgs e) => CommitTrash();

    /// File events say "look again" at once; the 3-second poll covers folders that don't exist yet.
    private void Watch()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        foreach (var folder in Folders().Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    IncludeSubdirectories = false,
                };
                // Events arrive on a pool thread; a screenshot fires several in a row, so settle first.
                FileSystemEventHandler changed = (_, _) => dispatcher.BeginInvoke(SettleThenRefresh);
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Changed += changed;
                watcher.Renamed += (_, _) => dispatcher.BeginInvoke(SettleThenRefresh);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception e) when (e is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                Log.Write($"Can't watch {folder}: {e.Message}");
            }
        }
    }

    private void SettleThenRefresh()
    {
        _settle.Stop();
        _settle.Start();
    }

    // MARK: - Scanning

    public void Refresh()
    {
        if (_scanning)
        {
            _rescan = true;
            return;
        }
        _scanning = true;
        var folders = Folders().ToList();
        Task.Run(() => Scan(folders)).ContinueWith(t =>
        {
            _scanning = false;
            _found = t.Result;
            // Set before publishing, so the first scan's update already shows "No screenshots yet".
            var first = !Loaded;
            Loaded = true;
            Publish(force: first);
            if (!_rescan) return;
            _rescan = false;
            Refresh();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// Shows what is on disk, minus captures waiting to go to the Recycle Bin.
    private void Publish(bool force = false)
    {
        var visible = _found.Where(f => !_hidden.Contains(f.Path)).ToList();
        var changed = force || !Loaded || !visible.SequenceEqual(Files);
        Files = visible;
        if (changed) Changed?.Invoke();
    }

    private static List<CaptureFile> Scan(IEnumerable<string> folders)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var found = new List<CaptureFile>();
        foreach (var folder in folders)
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
                {
                    if (!ImageExtensions.Contains(file.Extension) && !VideoExtensions.Contains(file.Extension)) continue;
                    // Copying a file resets its creation time; the earlier of the two is when it was taken.
                    var date = file.CreationTime < file.LastWriteTime ? file.CreationTime : file.LastWriteTime;
                    found.Add(new CaptureFile(file.FullName, date, file.Length, file.LastWriteTimeUtc.Ticks));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A folder that vanished or went offline mid-scan; the next scan tries again.
            }
        }
        return found.OrderByDescending(f => f.Date).Take(Limit).ToList();
    }

    // MARK: - Actions

    /// Opens the Windows screen snip (the same as Win+Shift+S) once the panel has folded away. The snip
    /// always ends up as a file in Screenshots, like the Mac app's `screencapture -i` into the folder:
    /// Windows 11 saves it itself, and on Windows 10 (or with auto-save off) Purrch saves the clipboard copy.
    public void TakeScreenshot()
    {
        NotchState.Shared.ForceClose();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.35) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
                SnipCatcher.Arm();
            }
            catch (System.ComponentModel.Win32Exception e) { Log.Write($"Screen snip unavailable: {e.Message}"); }
        };
        timer.Start();
    }

    /// The Screenshots folder, created (the way File Explorer would) if it isn't there yet.
    internal static string CreateScreenshotsFolder()
    {
        if (SHGetKnownFolderPath(ScreenshotsFolderId, KF_FLAG_CREATE, IntPtr.Zero, out var path) == 0 && Directory.Exists(path))
            return path;
        var folder = ScreenshotsFolder;
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// Takes captures off the grid and offers Undo for a few seconds; after that they go to the
    /// Recycle Bin (never deleted outright). Leaving the module ends the offer early.
    public void Trash(IReadOnlyCollection<CaptureFile> targets)
    {
        if (targets.Count == 0) return;
        CommitTrash();
        foreach (var file in targets) _hidden.Add(file.Path);
        LastTrashed = targets.ToList();
        _undoTimer.Start();
        Publish(force: true);
    }

    public void UndoTrash()
    {
        _undoTimer.Stop();
        foreach (var file in LastTrashed) _hidden.Remove(file.Path);
        LastTrashed = Array.Empty<CaptureFile>();
        Publish(force: true);
    }

    private void CommitTrash()
    {
        _undoTimer.Stop();
        if (LastTrashed.Count == 0) return;
        foreach (var file in LastTrashed)
        {
            try
            {
                if (File.Exists(file.Path))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file.Path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                _found.RemoveAll(f => f.Path == file.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or System.Security.SecurityException)
            {
                // In use or protected: it comes back into the grid.
                Log.Write($"Couldn't move {file.Path} to the Recycle Bin: {e.Message}");
            }
            _hidden.Remove(file.Path);
        }
        LastTrashed = Array.Empty<CaptureFile>();
        Publish(force: true);
    }

    public bool Copy(IReadOnlyCollection<CaptureFile> targets) => FileMeta.CopyFiles(targets.Select(f => f.Path).ToList());

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out string path);
}

/// After the Screenshot button opens the screen snip, waits up to a minute for the snip to reach the clipboard
/// and saves it into Screenshots as "Screenshot 2026-10-23 141503.png", unless Windows saved one there itself.
internal static class SnipCatcher
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private static readonly DispatcherTimer Timeout = new() { Interval = TimeSpan.FromSeconds(60) };
    // Windows 11's Snipping Tool writes its own file a moment after the clipboard copy; give it time first.
    private static readonly DispatcherTimer Settle = new() { Interval = TimeSpan.FromSeconds(2) };
    private static HwndSource? _listener;
    private static DateTime _since;
    private static byte[]? _png;

    static SnipCatcher()
    {
        Timeout.Tick += (_, _) => Disarm();
        Settle.Tick += (_, _) => SaveUnlessSaved();
    }

    public static void Arm()
    {
        Disarm();
        Settle.Stop();
        _png = null;
        _since = DateTime.UtcNow;
        // A message-only window: invisible, never activated, exists only to receive WM_CLIPBOARDUPDATE.
        _listener = new HwndSource(new HwndSourceParameters("Purrch snip listener") { ParentWindow = HWND_MESSAGE });
        _listener.AddHook(WndProc);
        if (!AddClipboardFormatListener(_listener.Handle))
        {
            Log.Write($"Snip listener failed ({Marshal.GetLastWin32Error()})");
            Disarm();
            return;
        }
        Timeout.Start();
    }

    private static void Disarm()
    {
        Timeout.Stop();
        if (_listener == null) return;
        RemoveClipboardFormatListener(_listener.Handle);
        _listener.Dispose();
        _listener = null;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Read a moment later: the snip may still hold the clipboard open.
        if (msg == WM_CLIPBOARDUPDATE) Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, Grab);
        return IntPtr.Zero;
    }

    private static void Grab()
    {
        // Text copied meanwhile, or the clipboard is busy: keep waiting for the picture.
        if (_listener == null || ReadPng() is not { } png) return;
        Disarm();
        _png = png;
        Settle.Start();
    }

    private static void SaveUnlessSaved()
    {
        Settle.Stop();
        var png = _png;
        _png = null;
        if (png == null) return;
        try
        {
            var folder = CapturesModel.CreateScreenshotsFolder();
            if (SavedSince(folder, _since)) return;
            var name = $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}";
            var path = Path.Combine(folder, name + ".png");
            for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{name} {n}.png");
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            file.Write(png);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            Log.Write($"Couldn't save the snip: {e.Message}");
        }
    }

    /// Windows 11 saved the snip itself (Snipping Tool, "Automatically save screenshots").
    private static bool SavedSince(string folder, DateTime since) =>
        new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true })
            .Any(f => CapturesModel.ImageExtensions.Contains(f.Extension) && f.CreationTimeUtc >= since.AddSeconds(-1));

    /// The clipboard picture as PNG, or null when there is none (yet).
    private static byte[]? ReadPng()
    {
        try
        {
            // The snip offers PNG itself; that keeps it byte for byte.
            if (System.Windows.Clipboard.GetData("PNG") is MemoryStream stream) return stream.ToArray();
            if (!System.Windows.Clipboard.ContainsImage() || System.Windows.Clipboard.GetImage() is not { } image) return null;
            // The clipboard bitmap's alpha is often zero although the snip is opaque; drop it.
            var opaque = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(opaque));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            return buffer.ToArray();
        }
        catch (Exception e) when (e is ExternalException or OutOfMemoryException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
