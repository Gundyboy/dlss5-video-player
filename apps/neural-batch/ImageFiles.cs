using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NeuralBatch;

internal static class ImageFiles
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp" };

    public static ImagePixels Read(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        PixelMath.ValidateDimensions(frame.PixelWidth, frame.PixelHeight);
        BitmapSource converted = frame.ColorContexts?.Count > 0
            ? new ColorConvertedBitmap(frame, frame.ColorContexts[0], new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32)
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(frame.PixelWidth * frame.PixelHeight * 4)];
        converted.CopyPixels(pixels, frame.PixelWidth * 4, 0);
        var image = new ImagePixels(frame.PixelWidth, frame.PixelHeight, pixels, ValidDpi(frame.DpiX), ValidDpi(frame.DpiY));
        if (frame.Metadata is BitmapMetadata metadata)
        {
            foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                try { if (metadata.GetQuery(query) is ushort orientation) return Orient(image, orientation); }
                catch (NotSupportedException) { }
            }
        }
        return image;
    }

    private static double ValidDpi(double value) => double.IsFinite(value) && value > 0 ? value : 96;

    internal static ImagePixels Orient(ImagePixels source, int orientation)
    {
        if (orientation < 2 || orientation > 8) return source;
        var swapped = orientation >= 5;
        var width = swapped ? source.Height : source.Width;
        var height = swapped ? source.Width : source.Height;
        var output = new byte[source.Bgra.Length];
        for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++)
        {
            var (dx, dy) = orientation switch
            {
                2 => (source.Width - 1 - x, y), 3 => (source.Width - 1 - x, source.Height - 1 - y),
                4 => (x, source.Height - 1 - y), 5 => (y, x), 6 => (source.Height - 1 - y, x),
                7 => (source.Height - 1 - y, source.Width - 1 - x), _ => (y, source.Width - 1 - x)
            };
            source.Bgra.AsSpan((y * source.Width + x) * 4, 4).CopyTo(output.AsSpan((dy * width + dx) * 4));
        }
        return new(width, height, output, swapped ? source.DpiY : source.DpiX, swapped ? source.DpiX : source.DpiY);
    }

    internal static void WritePng(string path, ImagePixels image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, image.DpiX, image.DpiY, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    public static string SaveResult(string input, string folder, ImagePixels image, BatchOptions options, CancellationToken cancellation)
    {
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, ".dlss-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            WritePng(temporary, image);
            cancellation.ThrowIfCancellationRequested();
            var stem = Path.GetFileNameWithoutExtension(input);
            if (stem.Length > 160) stem = stem[..160];
            var suffix = $" - DLSS {options.Mix}%" + (options.Scale == 2 ? " - 2x" : "") + (options.Pano ? " - Pano" : "");
            for (var index = 0; index < 10000; index++)
            {
                var destination = Path.Combine(folder, stem + suffix + (index == 0 ? "" : $" ({index + 1})") + ".png");
                try { File.Move(temporary, destination); return destination; }
                catch (IOException) when (File.Exists(destination)) { }
            }
            throw new IOException("Too many files already have this output name.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
