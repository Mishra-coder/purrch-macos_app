using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Purrch.Core;

namespace Purrch.Features.Clipboard;

public enum ClipKind { Text, Url, Files, Image }

public sealed class ClipItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ClipKind Kind { get; set; }
    public string Title { get; set; } = "";
    /// Text or URL string.
    public string? Text { get; set; }
    /// File paths for <see cref="ClipKind.Files"/>.
    public List<string>? Paths { get; set; }
    /// Saved PNG for <see cref="ClipKind.Image"/>.
    public string? ImagePath { get; set; }
    public long Size { get; set; }
    public string? SourceApp { get; set; }
    public DateTime Date { get; set; }
    public bool Pinned { get; set; }
    /// Used for de-duplication.
    public string Signature { get; set; } = "";

    public string TagLabel => Kind switch
    {
        ClipKind.Text => "Text",
        ClipKind.Url => "Link",
        ClipKind.Image => "Image",
        _ => Paths is [var first, ..] ? FileLabel(first) : "File",
    };

    private static string FileLabel(string path)
    {
        if (Directory.Exists(path)) return "Folder";
        var ext = System.IO.Path.GetExtension(path).TrimStart('.');
        return ext.Length is > 0 and <= 5 ? ext.ToUpperInvariant() : "File";
    }
}

/// Clipboard history. Windows tells us about every clipboard change through a hidden listener window;
/// each change becomes an item (text, link, files or image), newest first, up to 300 unpinned items.
public sealed class ClipboardService
{
    public static ClipboardService Shared { get; } = new();

    private const int MaxItems = 300;
    /// What browsers put on the clipboard for a link (the Mac's public.url).
    private const string UrlFormat = "UniformResourceLocatorW";
    private const string AnsiUrlFormat = "UniformResourceLocator";
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    /// Clipboard formats apps set to ask history tools not to record them (password managers, mostly).
    private static readonly string[] OptOutFormats =
    {
        "ExcludeClipboardContentFromMonitorProcessing",
        "Clipboard Viewer Ignore",
    };
    /// DWORD formats where 0 means "don't keep this in clipboard history".
    private static readonly string[] OptOutFlags = { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" };

    public static string ImageDirectory { get; } = CreateImageDirectory();

    private readonly List<ClipItem> _items;
    private HwndSource? _listener;
    private uint _ownWriteSequence;
    private bool _capturePending;

    /// Raised when the list or the "Copied" marker changes.
    public event Action? Changed;

    public Guid? LastCopiedId { get; private set; }

    private ClipboardService()
    {
        _items = Store.Load<List<ClipItem>>("clipboard");
    }

    private static string CreateImageDirectory()
    {
        var dir = System.IO.Path.Combine(AppPaths.Root, "clipboard-images");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public IReadOnlyList<ClipItem> Items => _items;

    /// Ordered for display: pinned first, then most recent.
    public IReadOnlyList<ClipItem> Ordered => _items.Where(i => i.Pinned).Concat(_items.Where(i => !i.Pinned)).ToList();

    public void Start()
    {
        if (_listener != null) return;
        // A message-only window: invisible, never activated, exists only to receive WM_CLIPBOARDUPDATE.
        _listener = new HwndSource(new HwndSourceParameters("Purrch clipboard listener") { ParentWindow = HWND_MESSAGE });
        _listener.AddHook(WndProc);
        if (!AddClipboardFormatListener(_listener.Handle))
            Log.Write($"Clipboard listener failed ({Marshal.GetLastWin32Error()})");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && !_capturePending)
        {
            // Read a moment later: the app that copied may still hold the clipboard open.
            _capturePending = true;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _capturePending = false;
                Capture();
            });
        }
        return IntPtr.Zero;
    }

    // MARK: - Capture

    private void Capture()
    {
        // Don't record our own writes.
        if (GetClipboardSequenceNumber() == _ownWriteSequence) return;
        if (IsOptedOut()) return;

        try
        {
            var source = AppNames.Of(Native.GetForegroundWindow());
            var now = DateTime.Now;

            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var paths = System.Windows.Clipboard.GetFileDropList().Cast<string>().ToList();
                if (paths.Count == 0) return;
                var signature = "files:" + string.Join("\n", paths);
                if (Bump(signature, source)) return;
                var first = System.IO.Path.GetFileName(paths[0].TrimEnd('\\'));
                var title = paths.Count == 1 ? first : $"{first} +{paths.Count - 1} more";
                var size = paths.Where(File.Exists).Sum(p => new FileInfo(p).Length);
                Insert(new ClipItem { Kind = ClipKind.Files, Title = title, Paths = paths, Size = size, SourceApp = source, Date = now, Signature = signature });
                return;
            }

            if (ReadPng() is var (png, width, height))
            {
                var digest = Convert.ToHexString(SHA256.HashData(png))[..24].ToLowerInvariant();
                var signature = "image:" + digest;
                if (Bump(signature, source)) return;
                var file = System.IO.Path.Combine(ImageDirectory, digest + ".png");
                File.WriteAllBytes(file, png);
                Insert(new ClipItem
                {
                    Kind = ClipKind.Image,
                    Title = $"Image {width}×{height}",
                    ImagePath = file,
                    Size = png.Length,
                    SourceApp = source,
                    Date = now,
                    Signature = signature,
                });
                return;
            }

            var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : ReadUrl();
            if (text == null) return;
            var trimmed = text.Trim();
            if (trimmed.Length == 0) return;
            var textSignature = "text:" + text;
            if (Bump(textSignature, source)) return;
            var isLink = !trimmed.Any(char.IsWhiteSpace)
                && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            var firstLine = trimmed.Split('\n', 2)[0].TrimEnd('\r');
            Insert(new ClipItem
            {
                Kind = isLink ? ClipKind.Url : ClipKind.Text,
                Title = firstLine.Length > 200 ? firstLine[..200] : firstLine,
                Text = text,
                Size = System.Text.Encoding.UTF8.GetByteCount(text),
                SourceApp = source,
                Date = now,
                Signature = textSignature,
            });
        }
        catch (Exception e) when (e is COMException or ExternalException or IOException or NotSupportedException)
        {
            // Another app is holding the clipboard, or the data is in a form WPF can't read.
            Log.Write($"Clipboard capture skipped: {e.Message}");
        }
    }

    /// The picture on the clipboard as PNG bytes. The "PNG" format that browsers, the Snipping Tool and
    /// Office add keeps transparency; the plain bitmap, read when there is none, loses it.
    private static (byte[] Png, int Width, int Height)? ReadPng()
    {
        if (System.Windows.Clipboard.ContainsData("PNG") && System.Windows.Clipboard.GetData("PNG") is MemoryStream stream)
        {
            var bytes = stream.ToArray();
            try
            {
                var frame = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                return (bytes, frame.PixelWidth, frame.PixelHeight);
            }
            catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException or IOException)
            {
                // Not a PNG after all; try the bitmap.
            }
        }
        if (!System.Windows.Clipboard.ContainsImage() || System.Windows.Clipboard.GetImage() is not { } image) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return (buffer.ToArray(), image.PixelWidth, image.PixelHeight);
    }

    /// A link copied without any text alongside it, as some apps do.
    private static string? ReadUrl()
    {
        string? url = null;
        if (System.Windows.Clipboard.GetData(UrlFormat) is MemoryStream wide) url = Encoding.Unicode.GetString(wide.ToArray());
        else if (System.Windows.Clipboard.GetData(AnsiUrlFormat) is MemoryStream ansi) url = Encoding.Latin1.GetString(ansi.ToArray());
        url = url?.Split('\0', 2)[0];
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }

    private static bool IsOptedOut()
    {
        foreach (var name in OptOutFormats)
            if (IsClipboardFormatAvailable(RegisterClipboardFormat(name))) return true;
        foreach (var name in OptOutFlags)
        {
            if (!IsClipboardFormatAvailable(RegisterClipboardFormat(name))) continue;
            try
            {
                if (System.Windows.Clipboard.GetData(name) is MemoryStream { Length: >= 4 } flag)
                {
                    var bytes = flag.ToArray();
                    if (BitConverter.ToInt32(bytes, 0) == 0) return true;
                }
            }
            catch (Exception e) when (e is COMException or ExternalException)
            {
                return true; // when in doubt, don't record
            }
        }
        return false;
    }

    /// Moves an identical existing item to the top. Returns true when found.
    private bool Bump(string signature, string? source)
    {
        var index = _items.FindIndex(i => i.Signature == signature);
        if (index < 0) return false;
        var item = _items[index];
        _items.RemoveAt(index);
        item.Date = DateTime.Now;
        if (source != null) item.SourceApp = source;
        _items.Insert(0, item);
        Save();
        return true;
    }

    private void Insert(ClipItem item)
    {
        _items.Insert(0, item);
        while (_items.Count > MaxItems)
        {
            var index = _items.FindLastIndex(i => !i.Pinned);
            if (index < 0) break;
            var removed = _items[index];
            _items.RemoveAt(index);
            DeleteFiles(removed);
        }
        Save();
    }

    private void DeleteFiles(ClipItem item)
    {
        if (item.ImagePath is not { } path) return;
        // Only remove when no other item points at the same file.
        if (_items.Any(i => i.ImagePath == path)) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Save()
    {
        Store.Save("clipboard", _items);
        Changed?.Invoke();
    }

    // MARK: - Actions

    public void Copy(ClipItem item)
    {
        try
        {
            var data = new DataObject();
            switch (item.Kind)
            {
                case ClipKind.Text:
                    data.SetText(item.Text ?? "");
                    break;
                case ClipKind.Url:
                    data.SetText(item.Text ?? "");
                    // Apps that take links (browsers, Office) read the URL format; everything else the text.
                    data.SetData(UrlFormat, new MemoryStream(Encoding.Unicode.GetBytes((item.Text ?? "").Trim() + "\0")));
                    break;
                case ClipKind.Files:
                    var files = new StringCollection();
                    files.AddRange((item.Paths ?? new List<string>()).ToArray());
                    data.SetFileDropList(files);
                    break;
                case ClipKind.Image:
                    if (item.ImagePath is not { } path || !File.Exists(path)) return;
                    data.SetImage(LoadImage(path));
                    // Many apps prefer PNG, which keeps transparency.
                    data.SetData("PNG", new MemoryStream(File.ReadAllBytes(path)));
                    break;
            }
            System.Windows.Clipboard.SetDataObject(data, copy: true);
            _ownWriteSequence = GetClipboardSequenceNumber();
        }
        catch (Exception e) when (e is COMException or ExternalException or IOException)
        {
            Log.Write($"Clipboard copy failed: {e.Message}");
            return;
        }

        var index = _items.FindIndex(i => i.Id == item.Id);
        if (index >= 0)
        {
            _items.RemoveAt(index);
            item.Date = DateTime.Now;
            _items.Insert(0, item);
        }
        LastCopiedId = item.Id;
        Save();

        var id = item.Id;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (LastCopiedId != id) return;
            LastCopiedId = null;
            Changed?.Invoke();
        };
        timer.Start();
    }

    public void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        Save();
    }

    public void Remove(ClipItem item)
    {
        _items.RemoveAll(i => i.Id == item.Id);
        DeleteFiles(item);
        Save();
    }

    public void ClearUnpinned()
    {
        var removed = _items.Where(i => !i.Pinned).ToList();
        _items.RemoveAll(i => !i.Pinned);
        foreach (var item in removed) DeleteFiles(item);
        Save();
    }

    /// What dragging an item out of the list hands to the drop target.
    public static DataObject DragData(ClipItem item)
    {
        var data = new DataObject();
        switch (item.Kind)
        {
            case ClipKind.Files:
                data.SetData(DataFormats.FileDrop, (item.Paths ?? new List<string>()).ToArray());
                break;
            case ClipKind.Image when item.ImagePath != null:
                data.SetData(DataFormats.FileDrop, new[] { item.ImagePath });
                break;
            default:
                data.SetText(item.Text ?? "");
                break;
        }
        return data;
    }

    public static BitmapImage LoadImage(string path, int decodeWidth = 0)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
        image.UriSource = new Uri(path);
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        image.EndInit();
        image.Freeze();
        return image;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);
}
