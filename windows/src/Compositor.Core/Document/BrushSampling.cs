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
    /// <summary>The native-grid size of a lazy Blur piece, matching the macOS brush tile.</summary>
    public const int BlurTileSize = 256;

    // A tile owns an RGBA/Gray8 bitmap while GaussianBlur owns two float planes. Keeping an individual
    // evaluation below this ceiling means one unusually scaled-down layer cannot turn a single dab into a
    // giant allocation. The sample cache is deliberately small and is disposed with the stroke.
    public const long MaxBlurTilePixels = 1_000_000;
    public const int MaxCachedBlurTiles = 8;

    public static double Fit(SKRectI region) => Fit(region, MaxWorkingPixels);

    /// <summary>Testable sample-plan calculation; no raster is allocated here.</summary>
    internal static double Fit(SKRectI region, long maximumPixels)
    {
        if (region.Width <= 0 || region.Height <= 0 || maximumPixels <= 0) return 0;
        var pixels = (double)region.Width * region.Height;
        return pixels <= maximumPixels ? 1 : Math.Sqrt(maximumPixels / pixels);
    }

    /// <summary>
    /// The macOS Blur sample budget: enough for four canvases where practical, never beyond the largest
    /// supported surface, and at least the legacy 16 MP floor. The lazy Windows version plans against this
    /// budget but makes only tiles which the stroke actually reaches.
    /// </summary>
    public static double StrokeFit(SKRectI region, long canvasPixels)
    {
        var budget = Math.Min((long)DocumentLimits.MaxSurfacePixels,
            Math.Max(MaxWorkingPixels, Math.Max(0, canvasPixels) * 4));
        return Fit(region, budget);
    }

    /// <summary>
    /// Keeps every lazy tile bounded even when a tiny displayed layer turns a canvas-radius blur into a
    /// very large native-grid sigma. One scale is selected for the whole stroke so adjacent tiles share a
    /// sampling grid and cannot form seams.
    /// </summary>
    public static double TileFit(double sigma, double strokeFit)
    {
        if (!(sigma >= 0) || !double.IsFinite(sigma) || !(strokeFit > 0)) return 0;
        var side = BlurTileSize + 6 * sigma + 4;
        var available = Math.Max(1, Math.Floor(Math.Sqrt(MaxBlurTilePixels)) - 2);
        return Math.Min(strokeFit, available / side);
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

    public static bool IsSane(SKRectI region) => region.Width > 0 && region.Height > 0;
}

/// <summary>
/// Test-only, allocation-free observability for the lazy Blur path. It deliberately records deterministic
/// planning facts rather than elapsed time, so large-canvas regression tests do not become timing-flaky.
/// </summary>
internal readonly record struct NativeBrushSamplingStats(int TilesRequested, int TilesBuilt, int BlurComputations,
    long PeakWorkingPixels, long PeakWorkingBytes, int TileSize, bool UsedCoarsening);

internal static class NativeBrushSamplingDiagnostics
{
    private sealed class Collector
    {
        public int TilesRequested;
        public int TilesBuilt;
        public int BlurComputations;
        public long CachedBytes;
        public long PeakWorkingPixels;
        public long PeakWorkingBytes;
        public bool UsedCoarsening;

        public NativeBrushSamplingStats Snapshot() => new(TilesRequested, TilesBuilt, BlurComputations,
            PeakWorkingPixels, PeakWorkingBytes, NativeBrushSampleLimits.BlurTileSize, UsedCoarsening);
    }

    private static readonly AsyncLocal<Collector?> Current = new();

    public static NativeBrushSamplingStats Last => (Current.Value ??= new Collector()).Snapshot();

    public static void Reset() => Current.Value = new Collector();

    internal static void Coarsening(double fit)
    {
        var collector = Current.Value ??= new Collector();
        collector.UsedCoarsening |= fit < 0.999999;
    }

    internal static void Requested() => (Current.Value ??= new Collector()).TilesRequested++;

    internal static void Built(int width, int height, int channels)
    {
        var collector = Current.Value ??= new Collector();
        var pixels = (long)width * height;
        var bitmapBytes = pixels * channels;
        // GaussianBlur has two float work planes, allocated while this tile's bitmap remains live.
        var workingBytes = collector.CachedBytes + bitmapBytes + pixels * sizeof(float) * 2;
        collector.TilesBuilt++;
        collector.BlurComputations++;
        collector.CachedBytes += bitmapBytes;
        collector.PeakWorkingPixels = Math.Max(collector.PeakWorkingPixels, pixels);
        collector.PeakWorkingBytes = Math.Max(collector.PeakWorkingBytes, workingBytes);
    }

    internal static void Evicted(int width, int height, int channels)
    {
        var collector = Current.Value ??= new Collector();
        collector.CachedBytes = Math.Max(0, collector.CachedBytes - (long)width * height * channels);
    }
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
    /// <summary>
    /// A lazy sample may discover that it cannot allocate a later tile. That is materially different from a
    /// transparent source pixel: callers must abandon the whole edit rather than leave a partially blurred
    /// layer behind.
    /// </summary>
    public virtual bool Failed => false;

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
/// A bounded, native-grid Blur source. It holds the pre-stroke bitmap by reference and evaluates only the
/// 256px source tiles reached by the coverage. A tile includes a three-sigma halo, so its centre is the same
/// result as one blurred whole source without introducing a seam at the tile boundary.
/// </summary>
internal abstract class NativeBlurTileSample : BrushColorSample
{
    private sealed class Tile(SKBitmap image, int left, int top)
    {
        public SKBitmap Image { get; } = image;
        public int Left { get; } = left;
        public int Top { get; } = top;
    }

    private readonly Dictionary<long, Tile> _tiles = [];
    private readonly Queue<long> _tileOrder = [];
    private readonly SKBitmap _original;
    private readonly SKRectI _region;
    private readonly int _globalWidth;
    private readonly int _globalHeight;
    private readonly int _halo;
    private readonly double _fit;
    private readonly double _sigma;
    private readonly int _channels;
    private bool _failed;
    private bool _disposed;

    protected NativeBlurTileSample(SKBitmap original, SKRectI covered, double sigma, double fit, int channels)
    {
        _original = original;
        _sigma = sigma;
        _fit = fit;
        _channels = channels;
        var margin = (int)Math.Ceiling(sigma * 3);
        _region = Inflate(covered, margin);
        _globalWidth = ScaledLength(_region.Width, fit);
        _globalHeight = ScaledLength(_region.Height, fit);
        // The extra two pixels are the same interpolation room the macOS clone-piece path keeps around a
        // cropped Core Image result. It makes a tile's source-to-destination conversion independent of
        // which neighbouring tile happened to be built first.
        _halo = Math.Max(1, (int)Math.Ceiling(sigma * fit * 3)) + 2;
        NativeBrushSamplingDiagnostics.Coarsening(fit);
    }

    public override bool Failed => _failed;

    protected bool TryReadTile(int x, int y, out SKBitmap image, out int sampleX, out int sampleY)
    {
        image = null!;
        sampleX = sampleY = 0;
        if (_disposed || _failed || (uint)x >= (uint)_original.Width || (uint)y >= (uint)_original.Height)
        {
            return false;
        }
        var key = TileKey(x / NativeBrushSampleLimits.BlurTileSize, y / NativeBrushSampleLimits.BlurTileSize);
        if (!_tiles.TryGetValue(key, out var tile))
        {
            NativeBrushSamplingDiagnostics.Requested();
            tile = Build(x, y);
            if (tile is null)
            {
                _failed = true;
                return false;
            }
            Remember(key, tile);
        }

        var globalX = OutputIndex(x, _region.Left, _fit, _globalWidth);
        var globalY = OutputIndex(y, _region.Top, _fit, _globalHeight);
        sampleX = globalX - tile.Left;
        sampleY = globalY - tile.Top;
        if ((uint)sampleX >= (uint)tile.Image.Width || (uint)sampleY >= (uint)tile.Image.Height)
        {
            _failed = true;
            return false;
        }
        image = tile.Image;
        return true;
    }

    private Tile? Build(int x, int y)
    {
        var left = x / NativeBrushSampleLimits.BlurTileSize * NativeBrushSampleLimits.BlurTileSize;
        var top = y / NativeBrushSampleLimits.BlurTileSize * NativeBrushSampleLimits.BlurTileSize;
        var inner = SKRectI.Create(left, top,
            Math.Min(NativeBrushSampleLimits.BlurTileSize, _original.Width - left),
            Math.Min(NativeBrushSampleLimits.BlurTileSize, _original.Height - top));
        if (inner.Width <= 0 || inner.Height <= 0) return null;

        var firstX = OutputIndex(inner.Left, _region.Left, _fit, _globalWidth);
        var lastX = OutputIndex(inner.Right - 1, _region.Left, _fit, _globalWidth);
        var firstY = OutputIndex(inner.Top, _region.Top, _fit, _globalHeight);
        var lastY = OutputIndex(inner.Bottom - 1, _region.Top, _fit, _globalHeight);
        var outputLeft = Math.Max(0, firstX - _halo);
        var outputTop = Math.Max(0, firstY - _halo);
        var outputRight = Math.Min(_globalWidth, lastX + _halo + 1);
        var outputBottom = Math.Min(_globalHeight, lastY + _halo + 1);
        var width = outputRight - outputLeft;
        var height = outputBottom - outputTop;
        if (width <= 0 || height <= 0 || (long)width * height > NativeBrushSampleLimits.MaxBlurTilePixels
            || !NativeBrushSampleLimits.CanAllocate(width, height, _channels))
        {
            return null;
        }

        SKBitmap? bitmap = null;
        try
        {
            bitmap = Allocate(width, height);
            Initialize(bitmap);
            var destination = bitmap.GetPixelSpan();
            for (var localY = 0; localY < height; localY++)
            {
                var sourceY = _region.Top + (int)Math.Floor((outputTop + localY + 0.5) / _fit);
                for (var localX = 0; localX < width; localX++)
                {
                    var sourceX = _region.Left + (int)Math.Floor((outputLeft + localX + 0.5) / _fit);
                    CopySource(destination, bitmap.RowBytes, localX, localY, sourceX, sourceY);
                }
            }
            if (!GaussianBlur.TryClamped(destination, width, height, _channels, bitmap.RowBytes, _sigma * _fit))
            {
                return null;
            }
            NativeBrushSamplingDiagnostics.Built(width, height, _channels);
            var result = new Tile(bitmap, outputLeft, outputTop);
            bitmap = null;
            return result;
        }
        catch (Exception error) when (error is InvalidOperationException or OutOfMemoryException or ArgumentOutOfRangeException)
        {
            return null;
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    private void Remember(long key, Tile tile)
    {
        while (_tiles.Count >= NativeBrushSampleLimits.MaxCachedBlurTiles && _tileOrder.TryDequeue(out var oldKey))
        {
            if (!_tiles.Remove(oldKey, out var old)) continue;
            NativeBrushSamplingDiagnostics.Evicted(old.Image.Width, old.Image.Height, _channels);
            old.Image.Dispose();
        }
        _tiles.Add(key, tile);
        _tileOrder.Enqueue(key);
    }

    protected abstract SKBitmap Allocate(int width, int height);
    protected abstract void Initialize(SKBitmap bitmap);
    protected abstract void CopySource(Span<byte> destination, int stride, int x, int y, int sourceX, int sourceY);

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var tile in _tiles.Values)
        {
            NativeBrushSamplingDiagnostics.Evicted(tile.Image.Width, tile.Image.Height, _channels);
            tile.Image.Dispose();
        }
        _tiles.Clear();
        _tileOrder.Clear();
    }

    protected static SKRectI Inflate(SKRectI rect, int by)
    {
        var left = (long)rect.Left - by;
        var top = (long)rect.Top - by;
        var right = (long)rect.Right + by;
        var bottom = (long)rect.Bottom + by;
        if (left < int.MinValue || top < int.MinValue || right > int.MaxValue || bottom > int.MaxValue) return default;
        return SKRectI.Create((int)left, (int)top, (int)(right - left), (int)(bottom - top));
    }

    private static int ScaledLength(int length, double fit)
    {
        if (length <= 0 || !(fit > 0) || !double.IsFinite(fit)) return 0;
        var scaled = Math.Ceiling(length * fit);
        return scaled is < 1 or > int.MaxValue ? 0 : (int)scaled;
    }

    private static int OutputIndex(int coordinate, int origin, double fit, int maximum)
    {
        var index = (int)Math.Floor((coordinate - origin) * fit);
        return Math.Clamp(index, 0, maximum - 1);
    }

    private static long TileKey(int x, int y) => ((long)x << 32) | (uint)y;
}

/// <summary>Lazy colour Blur source for an image layer. The original layer image is immutable until Paint commits.</summary>
internal sealed class NativeBlurColorSample : NativeBlurTileSample
{
    private readonly SKBitmap _original;

    private NativeBlurColorSample(SKBitmap original, SKRectI covered, double sigma, double fit)
        : base(original, covered, sigma, fit, channels: 4) => _original = original;

    public override bool TryRead(LayerPixelSpace target, int x, int y, SKPoint documentOffset,
        out BrushSampleColor color)
    {
        color = BrushSampleColor.Transparent;
        if (!TryReadTile(x, y, out var image, out var sampleX, out var sampleY)) return false;
        return TryReadStraight(image, sampleX, sampleY, out color);
    }

    public static NativeBlurColorSample? Create(SKBitmap original, LayerPixelSpace space, SKRectI covered,
        double documentRadius, long canvasPixels)
    {
        if (!NativeBrushSampleLimits.IsSane(covered)) return null;
        var sigma = Math.Min(Math.Clamp(documentRadius, 0.5, 50) / space.DocumentPixelsPerGridPixel,
            Math.Max(space.Width, space.Height) / 2.0);
        var region = Inflate(covered, (int)Math.Ceiling(sigma * 3));
        var fit = NativeBrushSampleLimits.TileFit(sigma, NativeBrushSampleLimits.StrokeFit(region, canvasPixels));
        return fit is > 0 and <= 1 && double.IsFinite(fit)
            ? new NativeBlurColorSample(original, covered, sigma, fit)
            : null;
    }

    protected override SKBitmap Allocate(int width, int height) => DocumentRenderer.Allocate(width, height);

    protected override void Initialize(SKBitmap bitmap)
    {
        // DocumentRenderer.Allocate already clears a premultiplied colour surface to transparent black.
    }

    protected override void CopySource(Span<byte> destination, int stride, int x, int y, int sourceX, int sourceY)
    {
        if (!TryReadStraight(_original, sourceX, sourceY, out var colour)) return;
        var at = y * stride + x * 4;
        destination[at] = (byte)((colour.Red * colour.Alpha + 127) / 255);
        destination[at + 1] = (byte)((colour.Green * colour.Alpha + 127) / 255);
        destination[at + 2] = (byte)((colour.Blue * colour.Alpha + 127) / 255);
        destination[at + 3] = colour.Alpha;
    }
}

/// <summary>Lazy Gray8 Blur source. Pixels outside a mask are the mask's edge background, never transparent.</summary>
internal sealed class NativeBlurMaskSample : NativeBlurTileSample
{
    private readonly SKBitmap _original;
    private readonly byte _background;

    private NativeBlurMaskSample(SKBitmap original, byte background, SKRectI covered, double sigma, double fit)
        : base(original, covered, sigma, fit, channels: 1)
    {
        _original = original;
        _background = background;
    }

    public bool TryRead(int x, int y, out byte value)
    {
        value = 0;
        if (!TryReadTile(x, y, out var image, out var sampleX, out var sampleY)) return false;
        value = image.GetPixelSpan()[sampleY * image.RowBytes + sampleX];
        return true;
    }

    // A mask sample is consumed through its Gray8 overload above. Keeping this override makes the shared
    // lifetime/cache implementation usable without pretending mask pixels are layer colours.
    public override bool TryRead(LayerPixelSpace target, int x, int y, SKPoint documentOffset,
        out BrushSampleColor color)
    {
        color = BrushSampleColor.Transparent;
        return false;
    }

    public static NativeBlurMaskSample? Create(SKBitmap original, byte background, LayerPixelSpace space,
        SKRectI covered, double documentRadius, long canvasPixels)
    {
        if (!NativeBrushSampleLimits.IsSane(covered)) return null;
        var sigma = Math.Min(Math.Clamp(documentRadius, 0.5, 50) / space.DocumentPixelsPerGridPixel,
            Math.Max(space.Width, space.Height) / 2.0);
        var region = Inflate(covered, (int)Math.Ceiling(sigma * 3));
        var fit = NativeBrushSampleLimits.TileFit(sigma, NativeBrushSampleLimits.StrokeFit(region, canvasPixels));
        return fit is > 0 and <= 1 && double.IsFinite(fit)
            ? new NativeBlurMaskSample(original, background, covered, sigma, fit)
            : null;
    }

    protected override SKBitmap Allocate(int width, int height) => Bitmaps.Allocate(Bitmaps.MaskInfo(width, height));

    protected override void Initialize(SKBitmap bitmap) =>
        bitmap.Erase(new SKColor(_background, _background, _background));

    protected override void CopySource(Span<byte> destination, int stride, int x, int y, int sourceX, int sourceY)
    {
        if ((uint)sourceX >= (uint)_original.Width || (uint)sourceY >= (uint)_original.Height) return;
        destination[y * stride + x] = _original.GetPixelSpan()[sourceY * _original.RowBytes + sourceX];
    }
}
