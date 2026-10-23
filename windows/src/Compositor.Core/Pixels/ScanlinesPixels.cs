using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Pixels;

/// <summary>The CRT branch of upstream DitherPixels.c at cd2f998, over premultiplied RGBA.</summary>
internal static class ScanlinesPixels
{
    internal const long MaxWorkingBytes = 512L * 1024 * 1024;

    private static int Workers(int height, int spacing) => Math.Min(32,
        Math.Min(Math.Max(1, Environment.ProcessorCount), (height + spacing - 1) / spacing));

    internal static long WorkingBytes(int width, int height, double spacing, double glow, bool selection)
    {
        if (width <= 0 || height <= 0 || width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide
            || (long)width * height > DocumentLimits.MaxSurfacePixels) return long.MaxValue;
        var shrink = Math.Max(1, (int)Math.Floor((spacing * 3 + 3) / 4));
        var smallPixels = (long)((width + shrink - 1) / shrink) * ((height + shrink - 1) / shrink);
        // Working surface, final straight-alpha asset and thumbnail preparation, plus optional selection copy.
        // Glow holds a small RGBA bitmap and two Gaussian float planes; each worker holds three scan planes.
        return (long)width * height * (selection ? 20 : 16) + (glow > 0 ? smallPixels * 12 : 0)
            + (long)Workers(height, Math.Clamp((int)spacing, 2, 32)) * width * 3 * sizeof(float);
    }

    internal static bool CanPrepare(int width, int height, double spacing, double glow, bool selection,
        long budget = MaxWorkingBytes, long selectionBytes = 0)
    {
        var baseEstimate = WorkingBytes(width, height, spacing, glow, selection);
        if (baseEstimate == long.MaxValue || selectionBytes < 0 || selectionBytes > long.MaxValue - baseEstimate) return false;
        var estimate = baseEstimate + selectionBytes;
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return estimate > 0 && estimate != long.MaxValue && estimate <= budget
            && (available <= 0 || estimate <= available / 4);
    }

    private static bool Valid(int width, int height, int stride, int length, DitherParams p) =>
        width > 0 && height > 0 && (long)width * 4 <= stride && (long)stride * height <= length
        && (long)width * height <= DocumentLimits.MaxSurfacePixels && p.Dark.Length >= 3 && p.Light.Length >= 3
        && float.IsFinite(p.Density) && float.IsFinite(p.Contrast) && float.IsFinite(p.Dots) && float.IsFinite(p.Wobble);

    public static bool Apply(Span<byte> rgba, int width, int height, int stride, DitherParams p)
    {
        if (!Valid(width, height, stride, rgba.Length, p)) return false;
        try
        {
            var scan = new float[checked(width * (p.OriginalColors ? 3 : 1))];
            Bands(rgba, width, height, stride, p, 0, (height + Math.Clamp(p.Cell, 2, 32) - 1) / Math.Clamp(p.Cell, 2, 32), scan);
            return true;
        }
        catch (OutOfMemoryException) { return false; }
    }

    public static bool Apply(SKBitmap image, DitherParams p)
    {
        if (!Valid(image.Width, image.Height, image.RowBytes, image.ByteCount, p)) return false;
        var spacing = Math.Clamp(p.Cell, 2, 32);
        var lines = (image.Height + spacing - 1) / spacing;
        var workers = Workers(image.Height, spacing);
        // Allocate every worker before writing the first line. Each band reads only its own original rows;
        // averaging all columns before writing any row keeps parallel and sequential results identical.
        try
        {
            var scans = new float[workers][];
            for (var i = 0; i < workers; i++) scans[i] = new float[checked(image.Width * (p.OriginalColors ? 3 : 1))];
            Parallel.For(0, workers, worker => Bands(image.GetPixelSpan(), image.Width, image.Height, image.RowBytes,
                p, worker * lines / workers, (worker + 1) * lines / workers, scans[worker]));
            return true;
        }
        catch (OutOfMemoryException) { return false; }
    }

    private static float Clamp(float value) => Math.Clamp(value, 0, 1);
    private static int Round(float value) => (int)MathF.Round(value, MidpointRounding.AwayFromZero);
    private static float Adjust(float value, float gamma, float contrast) =>
        Clamp((MathF.Pow(Clamp(value), gamma) - .5f) * contrast + .5f);

    private static void Bands(Span<byte> rgba, int width, int height, int stride, DitherParams p,
        int first, int last, float[] scan)
    {
        var spacing = Math.Clamp(p.Cell, 2, 32);
        var middle = spacing / 2f;
        var dots = Clamp(p.Dots);
        var gamma = MathF.Pow(2, p.Density * 1.5f);
        var contrast = p.Contrast >= 0 ? 1 / (1 - .95f * p.Contrast) : 1 + p.Contrast;
        var planes = p.OriginalColors ? 3 : 1;
        for (var line = first; line < last; line++)
        {
            var top = line * spacing;
            var bottom = Math.Min(height, top + spacing);
            var wave = MathF.Sin(line * .45f) * .7f + MathF.Sin(line * 1.7f + 1.3f) * .3f;
            var shift = Round(Math.Clamp(p.Wobble, 0, 64) * wave);
            for (var x = 0; x < width; x++)
            {
                float red = 0, green = 0, blue = 0;
                var n = 0;
                var sx = x - shift;
                if ((uint)sx < (uint)width)
                {
                    for (var y = top; y < bottom; y++)
                    {
                        var at = y * stride + sx * 4;
                        if (rgba[at + 3] == 0) continue;
                        var scale = 1f / rgba[at + 3];
                        var r = rgba[at] * scale;
                        var g = rgba[at + 1] * scale;
                        var b = rgba[at + 2] * scale;
                        if (p.OriginalColors)
                        {
                            red += Adjust(r, gamma, contrast);
                            green += Adjust(g, gamma, contrast);
                            blue += Adjust(b, gamma, contrast);
                        }
                        else red += Adjust(.2126f * r + .7152f * g + .0722f * b, gamma, contrast);
                        n++;
                    }
                }
                scan[x] = n > 0 ? red / n : 0;
                if (planes == 3)
                {
                    scan[width + x] = n > 0 ? green / n : 0;
                    scan[2 * width + x] = n > 0 ? blue / n : 0;
                }
            }
            for (var y = top; y < bottom; y++)
            {
                var offset = MathF.Abs(y - top + .5f - middle);
                for (var x = 0; x < width; x++)
                {
                    var destination = y * stride + x * 4;
                    if (rgba[destination + 3] == 0) continue;
                    var along = (x + .5f) % spacing - middle;
                    var at = Math.Clamp(Round(x - along * dots), 0, width - 1);
                    var r = scan[at];
                    var g = p.OriginalColors ? scan[width + at] : r;
                    var b = p.OriginalColors ? scan[2 * width + at] : r;
                    var tone = p.OriginalColors ? .2126f * r + .7152f * g + .0722f * b : r;
                    if (!p.OriginalColors)
                    {
                        r = p.Dark[0] / 255f + (p.Light[0] / 255f - p.Dark[0] / 255f) * tone;
                        g = p.Dark[1] / 255f + (p.Light[1] / 255f - p.Dark[1] / 255f) * tone;
                        b = p.Dark[2] / 255f + (p.Light[2] / 255f - p.Dark[2] / 255f) * tone;
                    }
                    var beam = middle * (.2f + .5f * MathF.Sqrt(Clamp(tone)));
                    var across = along * dots;
                    var cover = Clamp(beam - MathF.Sqrt(offset * offset + across * across) + .5f);
                    Write(rgba, destination, r, cover, p.OriginalColors ? 0 : p.Dark[0] / 255f);
                    Write(rgba, destination + 1, g, cover, p.OriginalColors ? 0 : p.Dark[1] / 255f, rgba[destination + 3]);
                    Write(rgba, destination + 2, b, cover, p.OriginalColors ? 0 : p.Dark[2] / 255f, rgba[destination + 3]);
                }
            }
        }
    }

    private static void Write(Span<byte> pixels, int at, float color, float cover, float background, int alpha = -1)
    {
        if (alpha < 0) alpha = pixels[at + 3];
        pixels[at] = (byte)Round(Clamp(background + (color * 1.35f - background) * cover) * (alpha / 255f) * 255f);
    }

    public static bool Glow(SKBitmap image, double spacing, double percent)
    {
        if (percent <= 0) return true;
        var sigma = spacing * 3 + 3;
        var shrink = Math.Max(1, (int)Math.Floor(sigma / 4));
        var width = (image.Width + shrink - 1) / shrink;
        var height = (image.Height + shrink - 1) / shrink;
        using var small = Bitmaps.Allocate(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        // Clamp the source at its actual edge and use a fixed shrink grid, including a partial last cell.
        // Area averaging avoids dropping a narrow beam between sample centers during downsampling.
        var source = image.GetPixelSpan();
        var light = small.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var right = Math.Min(image.Width, (x + 1) * shrink);
                var bottom = Math.Min(image.Height, (y + 1) * shrink);
                var n = (right - x * shrink) * (bottom - y * shrink);
                for (var c = 0; c < 4; c++)
                {
                    var sum = 0;
                    for (var yy = y * shrink; yy < bottom; yy++)
                        for (var xx = x * shrink; xx < right; xx++) sum += source[yy * image.RowBytes + xx * 4 + c];
                    light[y * small.RowBytes + x * 4 + c] = (byte)((sum + n / 2) / n);
                }
            }
        }
        if (!GaussianBlur.TryClamped(light, width, height, 4, small.RowBytes, sigma / shrink)) return false;
        var amount = (float)(percent / 100 * 2.5);
        for (var y = 0; y < image.Height; y++)
        {
            var sy = Math.Clamp((y + .5f) / shrink - .5f, 0, height - 1);
            var y0 = (int)sy;
            var y1 = Math.Min(y0 + 1, height - 1);
            var fy = sy - y0;
            for (var x = 0; x < image.Width; x++)
            {
                var at = y * image.RowBytes + x * 4;
                var alpha = source[at + 3];
                if (alpha == 0) continue;
                var sx = Math.Clamp((x + .5f) / shrink - .5f, 0, width - 1);
                var x0 = (int)sx;
                var x1 = Math.Min(x0 + 1, width - 1);
                var fx = sx - x0;
                for (var c = 0; c < 3; c++)
                {
                    var top = light[y0 * small.RowBytes + x0 * 4 + c] * (1 - fx) + light[y0 * small.RowBytes + x1 * 4 + c] * fx;
                    var bottom = light[y1 * small.RowBytes + x0 * 4 + c] * (1 - fx) + light[y1 * small.RowBytes + x1 * 4 + c] * fx;
                    // cd2f998 dither_glow: additive premultiplied light, bounded by the destination's alpha.
                    source[at + c] = (byte)Round(Math.Min(alpha, source[at + c] + (top + (bottom - top) * fy) * amount * alpha / 255f));
                }
            }
        }
        return true;
    }
}
