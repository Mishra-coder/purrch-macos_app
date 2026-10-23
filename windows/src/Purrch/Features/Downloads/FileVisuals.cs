using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Purrch.Core;
using Purrch.DesignSystem;
using Purrch.Features.Converter;

namespace Purrch.Features.Downloads;

/// Tag colours, icons and thumbnails shared by Downloads and the File Converter. Labels, opening,
/// copying and dragging files are in <see cref="Core.FileMeta"/>.
internal static class FileVisuals
{
    // macOS system colours (dark appearance), as the Mac app's file tags use.
    public static readonly Color Pink = Color.FromRgb(0xFF, 0x37, 0x5F);
    public static readonly Color Purple = Color.FromRgb(0xBF, 0x5A, 0xF2);
    public static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);
    public static readonly Color Yellow = Color.FromRgb(0xFF, 0xD6, 0x0A);
    public static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);
    public static readonly Color Teal = Color.FromRgb(0x40, 0xC8, 0xE0);
    public static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    public static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    public static readonly Color Mint = Color.FromRgb(0x63, 0xE6, 0xE2);
    public static readonly Color Cyan = Color.FromRgb(0x64, 0xD2, 0xFF);
    public static readonly Color Indigo = Color.FromRgb(0x5E, 0x5C, 0xE6);

    private static readonly ConcurrentDictionary<string, ImageSource?> Thumbnails = new();
    private static readonly Dictionary<string, ImageSource?> Icons = new();

    /// A small picture of an image file, decoded off the UI thread and cached. Null when it can't be read.
    public static async Task<ImageSource?> ThumbnailAsync(string path, int pixels)
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
        var key = $"{path}|{stamp.Ticks}|{pixels}";
        if (Thumbnails.TryGetValue(key, out var cached)) return cached;
        var image = await Task.Run(() => ImageConverter.LoadImage(path, shortest: pixels));
        if (Thumbnails.Count > 300) Thumbnails.Clear();
        Thumbnails[key] = image;
        return image;
    }

    /// The icon File Explorer shows for a file; a folder glyph for folders. Cached per file.
    public static UIElement Icon(string path, double size)
    {
        if (Directory.Exists(path)) return Ui.Glyph("\uE8B7", size * 0.7, ThemeResources.Accent);
        if (!Icons.TryGetValue(path, out var source))
        {
            source = ShellIcon(path);
            if (Icons.Count > 300) Icons.Clear();
            Icons[path] = source;
        }
        return source != null
            ? new Image { Source = source, Width = size, Height = size, Stretch = Stretch.Uniform }
            : Ui.Glyph("\uE7C3", size * 0.7, ThemeResources.SecondaryText);
    }

    private static ImageSource? ShellIcon(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    /// An image thumbnail that fills a rounded square, or the file's icon until (or unless) one loads.
    public static Border Thumbnail(string path, double size, double radius)
    {
        var box = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(radius),
            Child = Icon(path, size),
        };
        if (ImageConverter.IsImage(path)) LoadInto(box, path, (int)Math.Ceiling(size * 2));
        return box;
    }

    private static async void LoadInto(Border box, string path, int pixels)
    {
        if (await ThumbnailAsync(path, pixels) is not { } image) return;
        box.Child = null;
        box.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
    }
}

/// Row container used by the file lists: a rounded surface that lightens under the pointer.
internal class ListRow : Border
{
    public ListRow(UIElement content)
    {
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(12, 8, 12, 8);
        BorderThickness = new Thickness(0.6);
        Child = content;
        SetResourceReference(BackgroundProperty, ThemeResources.Surface);
        SetResourceReference(BorderBrushProperty, ThemeResources.Hairline);
        MouseEnter += (_, _) => SetResourceReference(BackgroundProperty, ThemeResources.Fill);
        MouseLeave += (_, _) => SetResourceReference(BackgroundProperty, ThemeResources.Surface);
    }
}

internal static class FileUi
{
    /// Small rounded tag like "PNG" or "DONE".
    public static Border Tag(string text, Color color) => new()
    {
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 1, 6, 2),
        Background = color.WithOpacity(0.18).Brush(),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text.ToUpperInvariant(),
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = color.Brush(),
        },
    };

    /// Accent-coloured text that acts as a button ("Clear", "Select", "Open Downloads").
    public static Button Link(string title, Action onClick, double size = 11, FontWeight? weight = null) =>
        Ui.Bare(Ui.Text(title, size, ThemeResources.Accent, weight ?? FontWeights.Medium), onClick);

    /// Section heading in small spaced capitals. Hair spaces stand in for the Mac label's letter spacing.
    public static TextBlock Caps(string text, double size = 10) =>
        Ui.Text(string.Join("\u200A", text.ToUpperInvariant().ToCharArray()), size, ThemeResources.SecondaryText, FontWeights.SemiBold);

    /// Starts a drag after the pointer has moved a few pixels with the button down; a plain click still clicks.
    public static void DragOut(FrameworkElement element, Func<DataObject?> data)
    {
        Point? press = null;
        element.PreviewMouseLeftButtonDown += (_, e) => press = e.GetPosition(element);
        element.PreviewMouseLeftButtonUp += (_, _) => press = null;
        element.MouseLeave += (_, _) => press = null;
        element.PreviewMouseMove += (_, e) =>
        {
            if (press is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(element);
            if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
            press = null;
            if (data() is { } payload) DragDrop.DoDragDrop(element, payload, DragDropEffects.Copy);
        };
    }
}
