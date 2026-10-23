using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Purrch.DesignSystem;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Purrch.Core;

/// Explorer's thumbnails (pictures, videos, PDFs; the file icon for everything else), made on one
/// background thread and kept in memory. The Windows side of the Mac app's QuickLook thumbnails.
public static class Thumbnails
{
    private sealed record Job(string Path, int Size, string Key, CancellationToken Token,
                              Action<BitmapSource?> Done, Dispatcher Dispatcher);

    private const int CacheLimit = 800;
    private static readonly BlockingCollection<Job> Jobs = new();
    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new();

    static Thumbnails()
    {
        // Shell thumbnail handlers are COM objects that expect a single-threaded apartment.
        var worker = new Thread(Work) { IsBackground = true, Name = "Purrch thumbnails", Priority = ThreadPriority.BelowNormal };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    /// Calls back on the caller's thread with a thumbnail about <paramref name="size"/> pixels on its long side,
    /// or null when Windows has none. The key includes size and write time, so a file that is still being
    /// written gets a fresh thumbnail once it is complete.
    public static void Request(string path, int size, Action<BitmapSource?> done, CancellationToken token = default)
    {
        string key;
        try
        {
            var info = new FileInfo(path);
            key = info.Exists
                ? $"{path}|{size}|{info.LastWriteTimeUtc.Ticks}|{info.Length}"
                : $"{path}|{size}|{Directory.GetLastWriteTimeUtc(path).Ticks}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            done(null);
            return;
        }
        if (Cache.TryGetValue(key, out var cached))
        {
            done(cached);
            return;
        }
        Jobs.Add(new Job(path, size, key, token, done, Dispatcher.CurrentDispatcher));
    }

    private static void Work()
    {
        foreach (var job in Jobs.GetConsumingEnumerable())
        {
            // Tiles that scrolled away or were rebuilt don't need theirs any more.
            if (job.Token.IsCancellationRequested) continue;
            if (!Cache.TryGetValue(job.Key, out var image))
            {
                image = Make(job.Path, job.Size);
                if (Cache.Count > CacheLimit) Cache.Clear();
                Cache[job.Key] = image;
            }
            job.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!job.Token.IsCancellationRequested) job.Done(image);
            });
        }
    }

    private static BitmapSource? Make(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        var bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory == null) return null;
            if (factory.GetImage(new NativeSize { Width = size, Height = size }, 0, out bitmap) != 0 || bitmap == IntPtr.Zero)
                return null;
            return ToBitmapSource(bitmap);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
    }

    /// Copies the shell's 32-bit bitmap top-down, keeping transparency (CreateBitmapSourceFromHBitmap
    /// turns transparent icon corners black).
    private static BitmapSource? ToBitmapSource(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out var info) == 0 || info.bmWidth <= 0 || info.bmHeight == 0) return null;
        var width = info.bmWidth;
        var height = Math.Abs(info.bmHeight);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
        };
        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }

        // Thumbnails of photos often leave alpha at zero; those are opaque, not invisible.
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] == 0) continue;
            hasAlpha = true;
            break;
        }
        var source = BitmapSource.Create(width, height, 96, 96, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        // Room for the one colour-table entry BITMAPINFO declares; unused at 32 bits.
        public int bmiColors;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}

/// File facts and actions shared by Shelf and Captures (FileMeta in the Mac app).
public static class FileMeta
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic" };

    /// Bytes on disk; a folder counts its first 5000 files.
    public static long Size(string path, int limit = 5000)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            long total = 0;
            var count = 0;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                total += file.Length;
                if (++count > limit) break;
            }
            return total;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return 0;
        }
    }

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    /// Short label like "png" or "Folder" (shown upper-case in a tag).
    public static string Label(string path)
    {
        if (Directory.Exists(path)) return "Folder";
        var ext = Path.GetExtension(path).TrimStart('.');
        return ext.Length == 0 ? "File" : ext;
    }

    /// Stable colour per file extension (the Mac system colours, dark variants).
    public static Color ColorFor(string label)
    {
        switch (label.ToLowerInvariant())
        {
            case "png" or "jpg" or "jpeg" or "heic" or "gif" or "webp" or "tiff" or "bmp" or "image": return Rgb(0xFF, 0x37, 0x5F);
            case "mov" or "mp4" or "m4v" or "mkv" or "avi": return Rgb(0xBF, 0x5A, 0xF2);
            case "pdf": return Rgb(0xFF, 0x45, 0x3A);
            case "zip" or "7z" or "rar" or "msi" or "exe" or "gz" or "tar": return Rgb(0xFF, 0xD6, 0x0A);
            case "folder": return Rgb(0x0A, 0x84, 0xFF);
            case "text" or "txt" or "md" or "rtf": return Rgb(0x40, 0xC8, 0xE0);
            case "link" or "url": return Rgb(0x30, 0xD1, 0x58);
            case "cs" or "json" or "js" or "ts" or "py" or "html" or "css" or "lock" or "sln" or "csproj": return Rgb(0xFF, 0x9F, 0x0A);
            default:
                Color[] palette = { Rgb(0x66, 0xD4, 0xCF), Rgb(0x64, 0xD2, 0xFF), Rgb(0x5E, 0x5C, 0xE6), Rgb(0xFF, 0x9F, 0x0A), Rgb(0x30, 0xD1, 0x58) };
                return palette[label.Sum(c => c) % palette.Length];
        }
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// Small upper-case type label, like the Mac app's Tag.
    public static System.Windows.Controls.Border Tag(string text, Color colour)
    {
        var tag = new System.Windows.Controls.Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Background = colour.WithOpacity(0.18).Brush(),
            Child = new System.Windows.Controls.TextBlock { Text = text.ToUpperInvariant(), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = colour.Brush() },
        };
        // Sits on the theme background so it stays readable over a busy thumbnail.
        if (Application.Current.Resources[ThemeResources.Background] is SolidColorBrush background)
            return new System.Windows.Controls.Border { CornerRadius = new CornerRadius(4), Background = background.Color.WithOpacity(0.7).Brush(), Child = tag };
        return tag;
    }

    /// Opens with the default app.
    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Write($"Couldn't open {path}: {e.Message}");
        }
    }

    /// Opens File Explorer with the file selected.
    public static void Reveal(string path) => Reveal(new[] { path });

    /// Opens File Explorer with all the files selected, one window per folder they are in
    /// (FileMeta.reveal with several URLs in the Mac app).
    public static void Reveal(IEnumerable<string> paths)
    {
        foreach (var folder in paths.GroupBy(p => Path.GetDirectoryName(p.TrimEnd('\\')) ?? p, StringComparer.OrdinalIgnoreCase))
        {
            if (!SelectInFolder(folder.Key, folder.ToList())) RevealOne(folder.First());
        }
    }

    private static void RevealOne(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (System.ComponentModel.Win32Exception e) { Log.Write($"Couldn't show {path}: {e.Message}"); }
    }

    /// The shell call behind explorer /select, which unlike the command line can select several items.
    private static bool SelectInFolder(string folder, IReadOnlyList<string> paths)
    {
        var folderId = ILCreateFromPathW(folder);
        if (folderId == IntPtr.Zero) return false;
        var items = paths.Select(p => ILCreateFromPathW(p)).Where(id => id != IntPtr.Zero).ToArray();
        try
        {
            return items.Length > 0 && SHOpenFolderAndSelectItems(folderId, (uint)items.Length, items, 0) == 0;
        }
        catch (COMException e)
        {
            Log.Write($"Couldn't show {folder}: {e.Message}");
            return false;
        }
        finally
        {
            foreach (var id in items) ILFree(id);
            ILFree(folderId);
        }
    }

    /// Opens a folder in File Explorer.
    public static void OpenFolder(string folder)
    {
        try { Process.Start("explorer.exe", $"\"{folder}\""); }
        catch (System.ComponentModel.Win32Exception e) { Log.Write($"Couldn't open {folder}: {e.Message}"); }
    }

    /// Copies the files, so Ctrl+V in File Explorer pastes them. A single picture also goes on the clipboard
    /// as pixels, so Ctrl+V in a chat or a document pastes the picture itself.
    public static bool CopyFiles(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return false;
        try
        {
            var data = new DataObject();
            var files = new StringCollection();
            files.AddRange(paths.ToArray());
            data.SetFileDropList(files);
            if (paths.Count == 1 && paths.First() is var single && IsImage(single) && LoadImage(single) is { } image)
            {
                data.SetImage(image);
                if (string.Equals(Path.GetExtension(single), ".png", StringComparison.OrdinalIgnoreCase))
                    data.SetData("PNG", new MemoryStream(File.ReadAllBytes(single)));
            }
            System.Windows.Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (Exception e) when (e is COMException or ExternalException or IOException or UnauthorizedAccessException)
        {
            Log.Write($"Copying files failed: {e.Message}");
            return false;
        }
    }

    /// What dragging files out of Purrch hands to the drop target. Copy only: the originals stay put.
    public static DataObject DragData(IEnumerable<string> paths)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths.ToArray());
        return data;
    }

    /// Paths in a drop, or none.
    public static string[] DroppedPaths(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths ? paths : Array.Empty<string>();

    /// Reads a picture without locking the file, even while another app is still writing it.
    /// Returns null when it can't be read (yet).
    public static BitmapImage? LoadImage(string path, int decodeWidth = 0)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                       or FileFormatException or InvalidOperationException or ArgumentException or COMException)
        {
            return null;
        }
    }

    /// The Windows share sheet (Nearby Sharing, Mail, apps), the counterpart of AirDrop.
    public static void Share(IReadOnlyList<string> paths, DependencyObject from)
    {
        if (paths.Count == 0 || Window.GetWindow(from) is not { } window) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        try
        {
            var manager = DataTransferManagerInterop.GetForWindow(hwnd);
            Windows.Foundation.TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? handler = null;
            handler = async (sender, args) =>
            {
                sender.DataRequested -= handler;
                var request = args.Request;
                var deferral = request.GetDeferral();
                try
                {
                    var items = new List<IStorageItem>();
                    foreach (var path in paths)
                    {
                        items.Add(Directory.Exists(path)
                            ? await StorageFolder.GetFolderFromPathAsync(path)
                            : await StorageFile.GetFileFromPathAsync(path));
                    }
                    request.Data.Properties.Title = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files";
                    request.Data.SetStorageItems(items);
                }
                // An async event handler must not throw: it would take the app down.
                catch (Exception e)
                {
                    Log.Write($"Share failed: {e.Message}");
                    request.FailWithDisplayText("Purrch couldn't read these files.");
                }
                finally
                {
                    deferral.Complete();
                }
            };
            manager.DataRequested += handler;
            DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException)
        {
            Log.Write($"Share sheet unavailable: {e.Message}");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPathW(string path);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr id);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr[] items, uint flags);
}

/// A file's NTFS identity (volume serial plus file ID), the Windows side of the Mac app's bookmarks:
/// the ID stays the same when the file is renamed or moved within its drive, so a moved file can be found again.
public static class FileIdentity
{
    /// Volume serial and the 128-bit file ID as hex, or null when the drive has no stable IDs (FAT, network shares).
    public static (ulong Volume, string Id)? Of(string path)
    {
        using var handle = Open(path);
        if (handle.IsInvalid) return null;
        return Read(handle);
    }

    /// Where the file with this identity is now, or null when it is gone (or in the Recycle Bin).
    /// <paramref name="hint"/> is the last known path; its drive is searched.
    public static string? Find(string hint, ulong volume, string id)
    {
        if (id.Length != 32 || !ulong.TryParse(id[..16], System.Globalization.NumberStyles.HexNumber, null, out var high)
            || !ulong.TryParse(id[16..], System.Globalization.NumberStyles.HexNumber, null, out var low))
            return null;
        string? root;
        try { root = Path.GetPathRoot(hint); }
        catch (ArgumentException) { return null; }
        if (string.IsNullOrEmpty(root)) return null;

        // Any handle on the drive tells OpenFileById which volume to look on.
        using var rootHandle = Open(root);
        if (rootHandle.IsInvalid || Read(rootHandle) is not { } drive || drive.Volume != volume) return null;

        var descriptor = new FILE_ID_DESCRIPTOR { Size = Marshal.SizeOf<FILE_ID_DESCRIPTOR>(), Type = ExtendedFileIdType, Low = low, High = high };
        using var file = OpenFileById(rootHandle, ref descriptor, 0, ShareAll, IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS);
        if (file.IsInvalid) return null;

        var buffer = new char[1024];
        var length = GetFinalPathNameByHandle(file, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) return null;
        var found = new string(buffer, 0, (int)length);
        // "\\?\C:\..." and "\\?\UNC\server\share\..." back to the usual forms.
        if (found.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) found = @"\\" + found[8..];
        else if (found.StartsWith(@"\\?\", StringComparison.Ordinal)) found = found[4..];
        // Deleting moves a file into the Recycle Bin with the same ID; that counts as gone.
        if (found.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(found) || Directory.Exists(found) ? found : null;
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle Open(string path) =>
        // No access rights needed to read the ID; backup semantics lets the same call open folders.
        CreateFile(path, 0, ShareAll, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

    private static (ulong Volume, string Id)? Read(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (GetFileInformationByHandleEx(handle, FileIdInfo, out FILE_ID_INFO info, Marshal.SizeOf<FILE_ID_INFO>()))
            return (info.VolumeSerialNumber, $"{info.High:X16}{info.Low:X16}");
        // Older file systems only have the 64-bit index; it fits the low half of the 128-bit ID.
        if (GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION legacy))
            return (legacy.VolumeSerialNumber, $"{0:X16}{((ulong)legacy.FileIndexHigh << 32) | legacy.FileIndexLow:X16}");
        return null;
    }

    private const uint ShareAll = 0x1 | 0x2 | 0x4; // read, write, delete
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const int FileIdInfo = 18;
    private const int ExtendedFileIdType = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        // FILE_ID_128, little-endian halves.
        public ulong Low, High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_DESCRIPTOR
    {
        public int Size;
        public int Type;
        // The union's FILE_ID_128 (16 bytes); a 64-bit FileIdType would use Low alone.
        public ulong Low, High;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int infoClass,
        out FILE_ID_INFO info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenFileById(Microsoft.Win32.SafeHandles.SafeFileHandle volumeHint,
        ref FILE_ID_DESCRIPTOR id, uint access, uint share, IntPtr security, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, [Out] char[] path,
        uint size, uint flags);
}
