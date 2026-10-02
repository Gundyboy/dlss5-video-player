namespace NeuralBatch;

internal static class PixelMath
{
    private static readonly double[] Linear = Enumerable.Range(0, 256).Select(value =>
    {
        var encoded = value / 255.0;
        return encoded <= .04045 ? encoded / 12.92 : Math.Pow((encoded + .055) / 1.055, 2.4);
    }).ToArray();

    public static void ValidateDimensions(int width, int height)
    {
        if (width < 64 || height < 64 || width > 8192 || height > 8192 || (long)width * height > 64_000_000)
            throw new InvalidDataException($"{width} × {height}: images must be 64–8192 pixels per side and at most 64 megapixels.");
    }

    public static byte[] ToRgb(ImagePixels image)
    {
        var rgb = new byte[checked(image.Width * image.Height * 3)];
        Parallel.For(0, image.Height, y =>
        {
            for (int x = 0, s = y * image.Width * 4, d = y * image.Width * 3; x < image.Width; x++, s += 4, d += 3)
            { rgb[d] = image.Bgra[s + 2]; rgb[d + 1] = image.Bgra[s + 1]; rgb[d + 2] = image.Bgra[s]; }
        });
        return rgb;
    }

    public static (int Width, int Height) OutputSize(int width, int height, int scale)
    {
        if (scale is not (1 or 2)) throw new InvalidDataException("Upscaler must be Off or 2×.");
        var outputWidth = checked(width * scale); var outputHeight = checked(height * scale);
        ValidateDimensions(outputWidth, outputHeight);
        return (outputWidth, outputHeight);
    }

    // DLSS supplies all RGB pixels. Only transparency needs a spatial resize.
    internal static ImagePixels UpscaledAlpha(ImagePixels source, bool pano)
    {
        var (width, height) = OutputSize(source.Width, source.Height, 2);
        var output = new byte[checked(width * height * 4)];
        Parallel.For(0, height, y =>
        {
            double sy = (y + .5) / 2 - .5; var top = (int)Math.Floor(sy); var fy = sy - top;
            int y0 = Math.Clamp(top, 0, source.Height - 1), y1 = Math.Clamp(top + 1, 0, source.Height - 1);
            for (var x = 0; x < width; x++)
            {
                double sx = (x + .5) / 2 - .5; var left = (int)Math.Floor(sx); var fx = sx - left;
                int X(int column) => pano ? (column + source.Width) % source.Width : Math.Clamp(column, 0, source.Width - 1);
                double A(int column, int row) => source.Bgra[(row * source.Width + column) * 4 + 3];
                var x0 = X(left); var x1 = X(left + 1);
                var above = A(x0, y0) * (1 - fx) + A(x1, y0) * fx;
                var below = A(x0, y1) * (1 - fx) + A(x1, y1) * fx;
                output[(y * width + x) * 4 + 3] = Round(above * (1 - fy) + below * fy);
            }
        });
        return new(width, height, output, source.DpiX, source.DpiY);
    }

    internal static void SetRgb(ImagePixels target, byte[] rgb)
    {
        if (rgb.Length != target.Width * target.Height * 3) throw new InvalidDataException("Incorrect upscaled frame size.");
        Parallel.For(0, target.Height, y =>
        {
            for (int x = 0, s = y * target.Width * 4, n = y * target.Width * 3; x < target.Width; x++, s += 4, n += 3)
            { target.Bgra[s] = rgb[n + 2]; target.Bgra[s + 1] = rgb[n + 1]; target.Bgra[s + 2] = rgb[n]; }
        });
    }

    public static void Offset(byte[] rgb, int width, int height, int offset)
    {
        var rowBytes = width * 3;
        var split = offset * 3;
        var row = new byte[rowBytes];
        for (var y = 0; y < height; y++)
        {
            var start = y * rowBytes;
            rgb.AsSpan(start, rowBytes).CopyTo(row);
            row.AsSpan(split).CopyTo(rgb.AsSpan(start));
            row.AsSpan(0, split).CopyTo(rgb.AsSpan(start + rowBytes - split));
        }
    }

    public static void BlendSeam(byte[] original, byte[] shifted, int width, int height, int offset)
    {
        var core = Math.Max(1, (int)(width * .02));
        var feather = Math.Max(core + 1, (int)Math.Round(width / 8.0, MidpointRounding.AwayFromZero));
        var weights = Enumerable.Range(0, feather).Select(d => d <= core ? 1 :
            (1 + Math.Cos(Math.PI * (d - core) / (feather - core))) / 2).ToArray();
        Parallel.For(0, height, y =>
        {
            for (var side = 0; side < 2; side++) for (var d = 0; d < feather; d++)
            {
                var x = side == 0 ? d : width - 1 - d;
                var p = (y * width + x) * 3;
                var q = (y * width + (x - offset + width) % width) * 3;
                for (var c = 0; c < 3; c++) original[p + c] = Round(original[p + c] * (1 - weights[d]) + shifted[q + c] * weights[d]);
            }
        });
    }

    public static byte[] Compose(ImagePixels source, byte[] neural, int mix)
    {
        if (mix < 0 || mix > 200 || neural.Length != source.Width * source.Height * 3)
            throw new InvalidDataException("Invalid Mix or neural frame length.");
        var output = new byte[source.Bgra.Length];
        Parallel.For(0, source.Height, y =>
        {
            for (int x = 0, s = y * source.Width * 4, n = y * source.Width * 3; x < source.Width; x++, s += 4, n += 3)
            {
                if (mix <= 100)
                {
                    for (var c = 0; c < 3; c++) output[s + 2 - c] = Round((source.Bgra[s + 2 - c] * (100 - mix) + neural[n + c] * mix) / 100.0);
                }
                else
                {
                    double nr = Linear[neural[n]], ng = Linear[neural[n + 1]], nb = Linear[neural[n + 2]];
                    var reference = .2126 * Linear[source.Bgra[s + 2]] + .7152 * Linear[source.Bgra[s + 1]] + .0722 * Linear[source.Bgra[s]];
                    var luminance = .2126 * nr + .7152 * ng + .0722 * nb;
                    var ratio = Math.Clamp((luminance + 1.0 / 512) / (reference + 1.0 / 512), .5, 2);
                    var scale = Math.Pow(ratio, mix / 100.0 - 1);
                    nr *= scale; ng *= scale; nb *= scale;
                    var peak = Math.Max(nr, Math.Max(ng, nb));
                    if (peak > 1) { nr /= peak; ng /= peak; nb /= peak; }
                    output[s + 2] = Encode(nr); output[s + 1] = Encode(ng); output[s] = Encode(nb);
                }
                output[s + 3] = source.Bgra[s + 3];
            }
        });
        return output;
    }

    private static byte Encode(double value) => Round((value <= .0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - .055) * 255);
    private static byte Round(double value) => (byte)Math.Clamp(Math.Floor(value + .5), 0, 255);
}
