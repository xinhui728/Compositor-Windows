using Compositor.Core.Model;
using Compositor.Core.Pixels;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>
/// The bounded working surface policy for a native-resolution brush sample. A colour Gaussian keeps one
/// byte surface and two float planes; reserving no more than a quarter of the runtime's available memory
/// makes an impractical stroke fail without risking an OOM or a partial, silently unblurred edit.
/// </summary>
internal static class NativeBrushSampleLimits
{
    public const long MaxWorkingPixels = 16_000_000;

    public static double Fit(SKRectI region) => Fit(region, MaxWorkingPixels);

    /// <summary>Testable sample-plan calculation; no raster is allocated here.</summary>
    internal static double Fit(SKRectI region, long maximumPixels)
    {
        if (region.Width <= 0 || region.Height <= 0 || maximumPixels <= 0) return 0;
        var pixels = (double)region.Width * region.Height;
        return pixels <= maximumPixels ? 1 : Math.Sqrt(maximumPixels / pixels);
    }

    public static bool CanAllocate(int width, int height, int channels)
    {
        if (width <= 0 || height <= 0 || channels <= 0) return false;
        var pixels = (long)width * height;
        // One byte source/sample channel plus two float work planes. This is conservative for RGBA and
        // intentionally also protects Gray8 masks from a giant blur plane allocation.
        var bytes = pixels * (channels + sizeof(float) * 2);
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return available <= 0 || bytes <= available / 4;
    }

    public static bool IsSane(SKRectI region) => region.Width > 0 && region.Height > 0
        && (long)region.Width * region.Height <= (long)DocumentLimits.MaxSurfacePixels * 16;
}

/// <summary>
/// The two coordinate systems a raster brush crosses. Pointer positions and Clone Stamp offsets belong to
/// the document; pixels and blur kernels belong to the layer (or mask) raster. Keeping this conversion in
/// one place prevents a scaled, turned or mirrored layer from accidentally being sampled on a document-sized
/// intermediate surface.
/// </summary>
internal readonly struct LayerPixelSpace
{
    public LayerPixelSpace(int width, int height, SKMatrix toDocument, SKMatrix toPixel)
    {
        Width = width;
        Height = height;
        ToDocument = toDocument;
        ToPixel = toPixel;
    }

    public int Width { get; }
    public int Height { get; }
    public SKMatrix ToDocument { get; }
    public SKMatrix ToPixel { get; }

    public static bool TryCreate(LayerTransform transform, int width, int height, out LayerPixelSpace space)
    {
        space = default;
        if (width <= 0 || height <= 0) return false;
        var toDocument = BrushEdits.PixelToDocument(transform, width, height);
        if (!toDocument.TryInvert(out var toPixel)) return false;
        space = new LayerPixelSpace(width, height, toDocument, toPixel);
        return true;
    }

    /// <summary>Maps a native-grid pixel centre to the document.</summary>
    public SKPoint DocumentAt(int x, int y) => ToDocument.MapPoint(x + 0.5f, y + 0.5f);

    /// <summary>Maps a document point back to the native pixel grid.</summary>
    public SKPoint PixelAt(SKPoint point) => ToPixel.MapPoint(point);

    /// <summary>
    /// Maps a document-space vector into the native grid. Mapping both ends, rather than inspecting matrix
    /// fields, deliberately keeps rotation, both flips and non-uniform scale in the calculation.
    /// </summary>
    public SKPoint PixelVector(SKPoint vector)
    {
        var origin = ToPixel.MapPoint(0, 0);
        var end = ToPixel.MapPoint(vector);
        return new SKPoint(end.X - origin.X, end.Y - origin.Y);
    }

    /// <summary>The geometric-mean document length represented by one native pixel.</summary>
    public double DocumentPixelsPerGridPixel => Math.Max(1e-6,
        Math.Sqrt(Math.Abs(ToDocument.ScaleX * ToDocument.ScaleY - ToDocument.SkewX * ToDocument.SkewY)));
}

/// <summary>A straight-alpha colour read from a brush sample.</summary>
internal readonly record struct BrushSampleColor(byte Red, byte Green, byte Blue, byte Alpha)
{
    public static readonly BrushSampleColor Transparent = new(0, 0, 0, 0);
}

/// <summary>
/// A source captured before a stroke writes its destination pixels. A source may live in document space
/// (Sample All Layers) or native layer-pixel space (Current Layer and Blur), but callers always ask it for
/// a destination pixel in the target grid.
/// </summary>
internal abstract class BrushColorSample : IDisposable
{
    public abstract bool TryRead(LayerPixelSpace target, int x, int y, SKPoint documentOffset,
        out BrushSampleColor color);

    public virtual void Dispose()
    {
    }

    protected static bool TryReadStraight(SKBitmap bitmap, int x, int y, out BrushSampleColor color)
    {
        color = BrushSampleColor.Transparent;
        if ((uint)x >= (uint)bitmap.Width || (uint)y >= (uint)bitmap.Height) return false;
        var pixels = bitmap.GetPixelSpan();
        var at = y * bitmap.RowBytes + x * 4;
        var alpha = pixels[at + 3];
        if (bitmap.AlphaType == SKAlphaType.Premul && alpha > 0)
        {
            color = new BrushSampleColor(
                (byte)Math.Min(255, (pixels[at] * 255 + alpha / 2) / alpha),
                (byte)Math.Min(255, (pixels[at + 1] * 255 + alpha / 2) / alpha),
                (byte)Math.Min(255, (pixels[at + 2] * 255 + alpha / 2) / alpha), alpha);
            return true;
        }
        color = alpha == 0
            ? BrushSampleColor.Transparent
            : new BrushSampleColor(pixels[at], pixels[at + 1], pixels[at + 2], alpha);
        return true;
    }
}

/// <summary>Current Layer Clone Stamp source. It borrows the immutable pre-stroke layer bitmap.</summary>
internal sealed class NativeLayerColorSample(SKBitmap source, LayerPixelSpace sourceSpace) : BrushColorSample
{
    public override bool TryRead(LayerPixelSpace target, int x, int y, SKPoint documentOffset,
        out BrushSampleColor color)
    {
        var destination = target.DocumentAt(x, y);
        var sourcePoint = sourceSpace.PixelAt(new SKPoint(destination.X + documentOffset.X,
            destination.Y + documentOffset.Y));
        return TryReadStraight(source, (int)Math.Floor(sourcePoint.X), (int)Math.Floor(sourcePoint.Y), out color);
    }
}

/// <summary>Sample All Layers source. It owns one premultiplied composite snapshot in document space.</summary>
internal sealed class DocumentColorSample(SKBitmap source) : BrushColorSample
{
    public override bool TryRead(LayerPixelSpace target, int x, int y, SKPoint documentOffset,
        out BrushSampleColor color)
    {
        var at = target.DocumentAt(x, y);
        return TryReadStraight(source, (int)Math.Floor(at.X + documentOffset.X),
            (int)Math.Floor(at.Y + documentOffset.Y), out color);
    }

    public override void Dispose() => source.Dispose();
}

/// <summary>
/// A blurred native layer region. The region includes its three-sigma halo and can be reduced to a bounded
/// working raster on exceptionally large layers; it is never a document-sized current-layer sample.
/// </summary>
internal sealed class NativeBlurColorSample(SKBitmap source, SKRectI region, double fit) : BrushColorSample
{
    public override bool TryRead(LayerPixelSpace target, int x, int y, SKPoint documentOffset,
        out BrushSampleColor color)
    {
        var sampleX = (int)Math.Floor((x - region.Left) * fit);
        var sampleY = (int)Math.Floor((y - region.Top) * fit);
        return TryReadStraight(source, sampleX, sampleY, out color);
    }

    public override void Dispose() => source.Dispose();

    public static NativeBlurColorSample? Create(SKBitmap original, LayerPixelSpace space, SKRectI covered,
        double documentRadius)
    {
        if (covered.Width <= 0 || covered.Height <= 0) return null;
        var sigma = Math.Min(Math.Clamp(documentRadius, 0.5, 50) / space.DocumentPixelsPerGridPixel,
            Math.Max(space.Width, space.Height) / 2.0);
        var margin = (int)Math.Ceiling(sigma * 3);
        var region = Inflate(covered, margin);
        return CreateColor(original, region, sigma);
    }

    private static NativeBlurColorSample? CreateColor(SKBitmap original, SKRectI region, double sigma)
    {
        if (!NativeBrushSampleLimits.IsSane(region)) return null;
        var fit = NativeBrushSampleLimits.Fit(region);
        var width = Math.Max(1, (int)Math.Ceiling(region.Width * fit));
        var height = Math.Max(1, (int)Math.Ceiling(region.Height * fit));
        if (!NativeBrushSampleLimits.CanAllocate(width, height, 4)) return null;
        SKBitmap? sample = null;
        try
        {
            sample = DocumentRenderer.Allocate(width, height);
            var destination = sample.GetPixelSpan();
            for (var y = 0; y < height; y++)
            {
                var sourceY = region.Top + (int)Math.Floor((y + 0.5) / fit);
                for (var x = 0; x < width; x++)
                {
                    var sourceX = region.Left + (int)Math.Floor((x + 0.5) / fit);
                    if (!BrushColorSample.TryReadStraight(original, sourceX, sourceY, out var colour)) continue;
                    var at = y * sample.RowBytes + x * 4;
                    destination[at] = (byte)((colour.Red * colour.Alpha + 127) / 255);
                    destination[at + 1] = (byte)((colour.Green * colour.Alpha + 127) / 255);
                    destination[at + 2] = (byte)((colour.Blue * colour.Alpha + 127) / 255);
                    destination[at + 3] = colour.Alpha;
                }
            }
            if (!GaussianBlur.TryClamped(destination, width, height, 4, sample.RowBytes, sigma * fit)) return null;
            var result = new NativeBlurColorSample(sample, region, fit);
            sample = null;
            return result;
        }
        catch (Exception error) when (error is InvalidOperationException or OutOfMemoryException or ArgumentOutOfRangeException)
        {
            return null;
        }
        finally
        {
            sample?.Dispose();
        }
    }

    private static SKRectI Inflate(SKRectI rect, int by)
    {
        var left = (long)rect.Left - by;
        var top = (long)rect.Top - by;
        var right = (long)rect.Right + by;
        var bottom = (long)rect.Bottom + by;
        if (left < int.MinValue || top < int.MinValue || right > int.MaxValue || bottom > int.MaxValue) return default;
        return SKRectI.Create((int)left, (int)top, (int)(right - left), (int)(bottom - top));
    }

}

/// <summary>Blur source for a Gray8 mask. Unlike image pixels, the region beyond a mask is its edge background.</summary>
internal sealed class NativeBlurMaskSample(SKBitmap source, SKRectI region, double fit) : IDisposable
{
    public bool TryRead(int x, int y, out byte value)
    {
        var sampleX = (int)Math.Floor((x - region.Left) * fit);
        var sampleY = (int)Math.Floor((y - region.Top) * fit);
        if ((uint)sampleX >= (uint)source.Width || (uint)sampleY >= (uint)source.Height)
        {
            value = 0;
            return false;
        }
        value = source.GetPixelSpan()[sampleY * source.RowBytes + sampleX];
        return true;
    }

    public void Dispose() => source.Dispose();

    public static NativeBlurMaskSample? Create(SKBitmap original, byte background, LayerPixelSpace space,
        SKRectI covered, double documentRadius)
    {
        if (covered.Width <= 0 || covered.Height <= 0) return null;
        var sigma = Math.Min(Math.Clamp(documentRadius, 0.5, 50) / space.DocumentPixelsPerGridPixel,
            Math.Max(space.Width, space.Height) / 2.0);
        var margin = (int)Math.Ceiling(sigma * 3);
        var region = Inflate(covered, margin);
        if (!NativeBrushSampleLimits.IsSane(region)) return null;
        var fit = NativeBrushSampleLimits.Fit(region);
        var width = Math.Max(1, (int)Math.Ceiling(region.Width * fit));
        var height = Math.Max(1, (int)Math.Ceiling(region.Height * fit));
        if (!NativeBrushSampleLimits.CanAllocate(width, height, 1)) return null;
        SKBitmap? sample = null;
        try
        {
            sample = Bitmaps.Allocate(Bitmaps.MaskInfo(width, height));
            sample.Erase(new SKColor(background, background, background));
            var destination = sample.GetPixelSpan();
            var sourcePixels = original.GetPixelSpan();
            for (var y = 0; y < height; y++)
            {
                var sourceY = region.Top + (int)Math.Floor((y + 0.5) / fit);
                if ((uint)sourceY >= (uint)original.Height) continue;
                for (var x = 0; x < width; x++)
                {
                    var sourceX = region.Left + (int)Math.Floor((x + 0.5) / fit);
                    if ((uint)sourceX >= (uint)original.Width) continue;
                    destination[y * sample.RowBytes + x] = sourcePixels[sourceY * original.RowBytes + sourceX];
                }
            }
            if (!GaussianBlur.TryClamped(destination, width, height, 1, sample.RowBytes, sigma * fit)) return null;
            var result = new NativeBlurMaskSample(sample, region, fit);
            sample = null;
            return result;
        }
        catch (Exception error) when (error is InvalidOperationException or OutOfMemoryException or ArgumentOutOfRangeException)
        {
            return null;
        }
        finally
        {
            sample?.Dispose();
        }
    }

    private static SKRectI Inflate(SKRectI rect, int by)
    {
        var left = (long)rect.Left - by;
        var top = (long)rect.Top - by;
        var right = (long)rect.Right + by;
        var bottom = (long)rect.Bottom + by;
        if (left < int.MinValue || top < int.MinValue || right > int.MaxValue || bottom > int.MaxValue) return default;
        return SKRectI.Create((int)left, (int)top, (int)(right - left), (int)(bottom - top));
    }

}
