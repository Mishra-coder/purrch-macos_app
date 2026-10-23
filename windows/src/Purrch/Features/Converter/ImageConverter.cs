using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Purrch.Core;
using Windows.Foundation;
using Windows.Storage.Streams;
using WinImaging = Windows.Graphics.Imaging;

namespace Purrch.Features.Converter;

/// Output formats: the Mac app's JPEG, PNG, HEIC and PDF, plus the other formats Windows writes.
/// HEIC goes through Windows' own HEIF encoder, which needs HEVC Video Extensions from the Store.
public enum ConvertFormat { Jpeg, Png, Heic, Tiff, Bmp, Gif, Pdf }

public static class ConvertFormatInfo
{
    public static IReadOnlyList<ConvertFormat> All { get; } = Enum.GetValues<ConvertFormat>();

    public static string Title(this ConvertFormat f) => f switch
    {
        ConvertFormat.Jpeg => "JPEG",
        ConvertFormat.Png => "PNG",
        ConvertFormat.Heic => "HEIC",
        ConvertFormat.Tiff => "TIFF",
        ConvertFormat.Bmp => "BMP",
        ConvertFormat.Gif => "GIF",
        _ => "PDF",
    };

    public static string Extension(this ConvertFormat f) => f switch
    {
        ConvertFormat.Jpeg => "jpg",
        ConvertFormat.Png => "png",
        ConvertFormat.Heic => "heic",
        ConvertFormat.Tiff => "tiff",
        ConvertFormat.Bmp => "bmp",
        ConvertFormat.Gif => "gif",
        _ => "pdf",
    };

    /// The quality slider applies (a PDF stores its pages as JPEG).
    public static bool Lossy(this ConvertFormat f) => f is ConvertFormat.Jpeg or ConvertFormat.Heic or ConvertFormat.Pdf;
}

public sealed record ConversionResult(IReadOnlyList<string> Sources, string? Output, long Before, long After, string? Error)
{
    public Guid Id { get; } = Guid.NewGuid();

    public double Saved => Before > 0 ? 1 - (double)After / Before : 0;
}

/// Local image conversion with WPF imaging (Windows Imaging Component). Nothing leaves the PC.
/// Every method here runs off the UI thread and returns frozen images.
public static class ImageConverter
{
    /// What the file picker shows. HEIC, AVIF and WebP need Microsoft's codecs from the Store. Drops also take
    /// any other file Windows can decode (camera RAW, PSD and so on when a codec is installed); see <see cref="CanRead"/>.
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".ico",
        ".heic", ".heif", ".avif", ".webp", ".jxr", ".wdp",
    };

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    /// Shown with a button to the Store when HEIC can't be written.
    public const string HevcMissing = "HEIC needs HEVC Video Extensions from the Microsoft Store.";
    /// HEVC Video Extensions from Device Manufacturer (free).
    public const string HevcStoreLink = "ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7";

    /// Whether Windows can decode the file, whatever its extension. Reads only the header.
    public static bool CanRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames.Count > 0;
        }
        catch (Exception e) when (IsImageError(e))
        {
            return false;
        }
    }

    public static IReadOnlyList<ConversionResult> Convert(IReadOnlyList<string> paths, ConvertFormat format, double quality, int? maxDimension)
    {
        if (format == ConvertFormat.Pdf) return new[] { MakePdf(paths, quality, maxDimension) };
        return paths.Select(p => ConvertOne(p, format, quality, maxDimension)).ToList();
    }

    /// Decodes an image with its EXIF rotation applied. <paramref name="longest"/> caps the longer side;
    /// <paramref name="shortest"/> instead sizes the shorter side (for thumbnails that fill a square).
    /// Never scales up. Returns null when Windows can't read the file.
    public static BitmapSource? LoadImage(string path, int? longest = null, int? shortest = null)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return null;
            var frame = decoder.Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;
            var orientation = Orientation(frame);

            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // read it all now, so the file isn't kept open
            image.StreamSource = stream;
            // Decode straight to the smaller size, like ImageIO's thumbnail option; much faster for big JPEGs.
            var (limit, actual) = longest is { } l ? (l, Math.Max(width, height))
                : shortest is { } s ? (s, Math.Min(width, height))
                : (0, 0);
            if (limit > 0 && actual > limit)
            {
                var scale = (double)limit / actual;
                if (width >= height) image.DecodePixelWidth = Math.Max(1, (int)Math.Round(width * scale));
                else image.DecodePixelHeight = Math.Max(1, (int)Math.Round(height * scale));
            }
            image.EndInit();
            image.Freeze();
            return Orient(image, orientation);
        }
        catch (Exception e) when (IsImageError(e))
        {
            return null;
        }
    }

    /// The exceptions WIC and file access throw for missing, locked, corrupt or unsupported images.
    public static bool IsImageError(Exception e) =>
        e is IOException or UnauthorizedAccessException or NotSupportedException or FormatException
            or ArgumentException or InvalidOperationException or ExternalException or OverflowException
            or OutOfMemoryException;

    private static ConversionResult ConvertOne(string path, ConvertFormat format, double quality, int? maxDimension)
    {
        var before = SizeOf(path);
        var sources = new[] { path };
        var image = LoadImage(path, maxDimension);
        if (image == null) return new(sources, null, before, 0, ReadError(path));
        if (format == ConvertFormat.Heic) return ConvertHeic(path, image, quality, before);

        BitmapEncoder encoder;
        BitmapSource frame;
        try
        {
            (encoder, frame) = format switch
            {
                ConvertFormat.Jpeg => ((BitmapEncoder)new JpegBitmapEncoder { QualityLevel = Quality(quality) }, OnWhite(image)),
                ConvertFormat.Png => (new PngBitmapEncoder(), image),
                ConvertFormat.Tiff => (new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw }, image),
                ConvertFormat.Bmp => (new BmpBitmapEncoder(), image),
                // GIF holds 256 colours; pick the best 256 for this picture rather than a fixed web palette.
                _ => (new GifBitmapEncoder(), Indexed(OnWhite(image))),
            };
            encoder.Frames.Add(BitmapFrame.Create(frame));
        }
        catch (Exception e) when (IsImageError(e))
        {
            Log.Write($"Converter: {Path.GetFileName(path)} to {format.Title()} failed: {e.Message}");
            return new(sources, null, before, 0, e is OutOfMemoryException ? "Image too large" : $"{format.Title()} not supported");
        }

        var output = UniquePath(path, "-converted", format.Extension());
        return Write(output, s => encoder.Save(s))
            ? new(sources, output, before, SizeOf(output), null)
            : new(sources, null, before, 0, "Write failed");
    }

    private static ConversionResult ConvertHeic(string path, BitmapSource image, double quality, long before)
    {
        var sources = new[] { path };
        if (!HeifEncoderInstalled()) return new(sources, null, before, 0, HevcMissing);
        byte[] heic;
        try
        {
            heic = EncodeHeic(image, quality);
        }
        catch (Exception e) when (IsImageError(e))
        {
            Log.Write($"Converter: {Path.GetFileName(path)} to HEIC failed (0x{e.HResult:X8}): {e.Message}");
            // The HEIF encoder is there but the HEVC codec it hands the pixels to isn't, almost always.
            return new(sources, null, before, 0, e is OutOfMemoryException ? "Image too large" : HevcMissing);
        }
        var output = UniquePath(path, "-converted", ConvertFormat.Heic.Extension());
        return Write(output, s => s.Write(heic))
            ? new(sources, output, before, SizeOf(output), null)
            : new(sources, null, before, 0, "Write failed");
    }

    /// Windows lists the HEIF encoder with HEIF Image Extensions installed (built into Windows 11).
    private static bool HeifEncoderInstalled()
    {
        try
        {
            return WinImaging.BitmapEncoder.GetEncoderInformationEnumerator().Any(i => i.CodecId == WinImaging.BitmapEncoder.HeifEncoderId);
        }
        catch (Exception e) when (IsImageError(e))
        {
            return false;
        }
    }

    /// HEIC through WinRT imaging, with the quality slider as the encoder's ImageQuality. Flattened onto white:
    /// the photo-style HEIC Windows writes has no transparency.
    private static byte[] EncodeHeic(BitmapSource image, double quality)
    {
        var bgra = new FormatConvertedBitmap(OnWhite(image), PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        bgra.CopyPixels(pixels, stride, 0);
        return EncodeHeicAsync(pixels, width, height, Dpi(image.DpiX), Dpi(image.DpiY), quality).GetAwaiter().GetResult();
    }

    private static async Task<byte[]> EncodeHeicAsync(byte[] pixels, int width, int height, double dpiX, double dpiY, double quality)
    {
        using var stream = new InMemoryRandomAccessStream();
        var options = new WinImaging.BitmapPropertySet
        {
            ["ImageQuality"] = new WinImaging.BitmapTypedValue((float)Math.Clamp(quality, 0, 1), PropertyType.Single),
        };
        var encoder = await WinImaging.BitmapEncoder.CreateAsync(WinImaging.BitmapEncoder.HeifEncoderId, stream, options);
        encoder.SetPixelData(WinImaging.BitmapPixelFormat.Bgra8, WinImaging.BitmapAlphaMode.Ignore,
            (uint)width, (uint)height, dpiX, dpiY, pixels);
        await encoder.FlushAsync();

        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static ConversionResult MakePdf(IReadOnlyList<string> paths, double quality, int? maxDimension)
    {
        var before = paths.Sum(SizeOf);
        var pages = new List<(byte[] Jpeg, int Width, int Height)>();
        foreach (var path in paths)
        {
            if (LoadImage(path, maxDimension) is not { } image) continue;
            try
            {
                // Pages are stored as JPEG, so the quality slider shrinks the PDF too.
                var encoder = new JpegBitmapEncoder { QualityLevel = Quality(quality) };
                encoder.Frames.Add(BitmapFrame.Create(Rgb(image)));
                using var buffer = new MemoryStream();
                encoder.Save(buffer);
                pages.Add((buffer.ToArray(), image.PixelWidth, image.PixelHeight));
            }
            catch (Exception e) when (IsImageError(e))
            {
                Log.Write($"Converter: {Path.GetFileName(path)} skipped in PDF: {e.Message}");
            }
        }
        if (pages.Count == 0 || paths.Count == 0) return new(paths, null, before, 0, "No readable images");

        var output = UniquePath(paths[0], paths.Count > 1 ? "-combined" : "", "pdf");
        return Write(output, s => PdfWriter.Write(s, pages))
            ? new(paths, output, before, SizeOf(output), null)
            : new(paths, null, before, 0, "Write failed");
    }

    private static int Quality(double quality) => Math.Clamp((int)Math.Round(quality * 100), 1, 100);

    private static string ReadError(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".heic" or ".heif" or ".avif" => "Couldn't read image. Install HEIF Image Extensions from the Microsoft Store.",
        ".webp" => "Couldn't read image. Install Webp Image Extensions from the Microsoft Store.",
        _ => "Couldn't read image",
    };

    /// Writes to a new file, removing it again if anything goes wrong half way.
    private static bool Write(string output, Action<Stream> save)
    {
        try
        {
            using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
                save(file);
            return true;
        }
        catch (Exception e) when (IsImageError(e))
        {
            Log.Write($"Converter: writing {output} failed: {e.Message}");
            try { File.Delete(output); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return 0; }
    }

    /// "photo-converted.jpg" next to the original, then "photo-converted 2.jpg" and so on.
    private static string UniquePath(string source, string suffix, string extension)
    {
        var dir = Path.GetDirectoryName(source) ?? "";
        var name = Path.GetFileNameWithoutExtension(source) + suffix;
        var candidate = Path.Combine(dir, $"{name}.{extension}");
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate)
                        || string.Equals(candidate, source, StringComparison.OrdinalIgnoreCase); n++)
            candidate = Path.Combine(dir, $"{name} {n}.{extension}");
        return candidate;
    }

    // MARK: - Pixels

    /// EXIF orientation (1 to 8), read through the metadata policy so it works for JPEG, TIFF and HEIC alike.
    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata
                && metadata.GetQuery("System.Photo.Orientation") is ushort value
                && value is >= 1 and <= 8)
                return value;
        }
        catch (Exception e) when (IsImageError(e)) { }
        return 1;
    }

    private static BitmapSource Orient(BitmapSource image, int orientation)
    {
        if (orientation <= 1) return image;
        var transform = new TransformGroup();
        if (orientation is 2 or 4 or 5 or 7) transform.Children.Add(new ScaleTransform(-1, 1));
        var angle = orientation switch { 3 or 4 => 180, 5 or 6 => 90, 7 or 8 => 270, _ => 0 };
        if (angle != 0) transform.Children.Add(new RotateTransform(angle));
        var rotated = new TransformedBitmap(image, transform);
        rotated.Freeze();
        return rotated;
    }

    /// Flattens transparency onto white (JPEG and GIF have no alpha; black would show through otherwise).
    private static BitmapSource OnWhite(BitmapSource image)
    {
        if (!HasAlpha(image.Format)) return image;
        var premultiplied = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        int width = premultiplied.PixelWidth, height = premultiplied.PixelHeight;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        premultiplied.CopyPixels(pixels, stride, 0);

        var outStride = (width * 3 + 3) & ~3;
        var output = new byte[checked(outStride * height)];
        for (var y = 0; y < height; y++)
        {
            int i = y * stride, o = y * outStride;
            for (var x = 0; x < width; x++, i += 4, o += 3)
            {
                var clear = 255 - pixels[i + 3];
                output[o] = (byte)Math.Min(255, pixels[i] + clear);
                output[o + 1] = (byte)Math.Min(255, pixels[i + 1] + clear);
                output[o + 2] = (byte)Math.Min(255, pixels[i + 2] + clear);
            }
        }
        var flat = BitmapSource.Create(width, height, Dpi(image.DpiX), Dpi(image.DpiY), PixelFormats.Bgr24, null, output, outStride);
        flat.Freeze();
        return flat;
    }

    /// Plain 24-bit colour, which is what the PDF's /DeviceRGB images promise (no grey or CMYK JPEGs).
    private static BitmapSource Rgb(BitmapSource image)
    {
        var flat = OnWhite(image);
        if (flat.Format == PixelFormats.Bgr24) return flat;
        var rgb = new FormatConvertedBitmap(flat, PixelFormats.Bgr24, null, 0);
        rgb.Freeze();
        return rgb;
    }

    private static BitmapSource Indexed(BitmapSource image)
    {
        var indexed = new FormatConvertedBitmap(image, PixelFormats.Indexed8, new BitmapPalette(image, 256), 0);
        indexed.Freeze();
        return indexed;
    }

    private static bool HasAlpha(PixelFormat format) =>
        format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 || format == PixelFormats.Rgba64
        || format == PixelFormats.Prgba64 || format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float
        || format == PixelFormats.Indexed1 || format == PixelFormats.Indexed2 || format == PixelFormats.Indexed4
        || format == PixelFormats.Indexed8; // palettes (GIF, some PNGs) can hold a transparent entry

    private static double Dpi(double dpi) => dpi is > 1 and < 10000 ? dpi : 96;
}

/// The smallest PDF that does the job: one page per JPEG, each page the size of its picture.
internal static class PdfWriter
{
    public static void Write(Stream stream, IReadOnlyList<(byte[] Jpeg, int Width, int Height)> pages)
    {
        var offsets = new List<long>();
        void Text(string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            stream.Write(bytes, 0, bytes.Length);
        }
        void Begin(int id)
        {
            offsets.Add(stream.Position);
            Text($"{id} 0 obj\n");
        }

        Text("%PDF-1.4\n");
        // Objects: 1 catalog, 2 page tree, then three per page (page, contents, image).
        Begin(1);
        Text("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Begin(2);
        var kids = string.Join(" ", pages.Select((_, i) => $"{3 + i * 3} 0 R"));
        Text($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n");

        for (var i = 0; i < pages.Count; i++)
        {
            var (jpeg, width, height) = pages[i];
            int page = 3 + i * 3, contents = page + 1, image = page + 2;
            Begin(page);
            Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] " +
                 $"/Resources << /XObject << /Im0 {image} 0 R >> >> /Contents {contents} 0 R >>\nendobj\n");
            var draw = $"q {width} 0 0 {height} 0 0 cm /Im0 Do Q\n";
            Begin(contents);
            Text($"<< /Length {draw.Length} >>\nstream\n{draw}endstream\nendobj\n");
            Begin(image);
            Text($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB " +
                 $"/BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
            stream.Write(jpeg, 0, jpeg.Length);
            Text("\nendstream\nendobj\n");
        }

        var xref = stream.Position;
        Text($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Text($"{offset:D10} 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
