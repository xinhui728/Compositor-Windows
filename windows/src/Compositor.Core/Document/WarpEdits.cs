using System.Buffers;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>The Blur tool's modes that push pixels: Liquify shoves them; Smudge drags their colour.</summary>
public enum WarpMode
{
    Liquify,
    Smudge,
}

/// <summary>Deterministic diagnostics for the sparse Warp path; tests use this instead of timing assertions.</summary>
internal readonly record struct WarpEditsStats(int DabCount, int RetainedDabCount, long WorkingPixels,
    long WritePixels, long PeakScratchBytes, bool UsedDocumentSurface);

internal static class WarpEditsDiagnostics
{
    private static readonly AsyncLocal<WarpEditsStats> Current = new();

    public static WarpEditsStats Last => Current.Value;
    public static void Reset() => Current.Value = default;

    internal static void Plan(int dabs, long workingPixels, long writePixels) =>
        Current.Value = new WarpEditsStats(dabs, dabs, workingPixels, writePixels, 0, UsedDocumentSurface: false);

    internal static void Scratch(long bytes)
    {
        var current = Current.Value;
        Current.Value = current with { PeakScratchBytes = Math.Max(current.PeakScratchBytes, bytes) };
    }
}

/// <summary>
/// Smudge and Liquify stay in document coordinates so transformed layers retain their visual behaviour. Their
/// temporary surface is the stroke's own document ROI, however, and write-back is limited to the inverse-mapped
/// native rectangle rather than scanning every layer pixel against every dab.
/// </summary>
public static class WarpEdits
{
    // The macOS BrushStroke permits a little over the UI's 2,000px maximum for generated strokes.
    private const double MaxDiameter = 2_100;
    private const int MaxDabs = 250_000;
    private const long MaxWorkingPixels = 16_000_000;
    private const long MaxScratchBytes = 96L * 1024 * 1024;

    /// <summary>
    /// Pushes a layer's pixels along <paramref name="points"/>. The layer is only replaced after temporary
    /// work, carried colour and the output bitmap all succeed, so a rejected large operation is never partial.
    /// </summary>
    public static bool Warp(CanvasDocument document, Guid layerID, IReadOnlyList<SKPoint> points, WarpMode mode,
        BrushSettings settings)
    {
        WarpEditsDiagnostics.Reset();
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Asset: { } asset } layer) return false;
        if (layer.IsGroup || points.Count == 0 || !Valid(settings)) return false;
        if (!WarpPlan.TryCreate(points, mode, settings, out var plan)) return false;

        var canvas = SKRectI.Create(0, 0, document.Width, document.Height);
        var workBounds = SKRectI.Intersect(plan.WorkBounds, canvas);
        var affected = SKRectI.Intersect(plan.AffectedBounds, canvas);
        var workPixels = (long)workBounds.Width * workBounds.Height;
        if (workBounds.Width <= 0 || workBounds.Height <= 0 || affected.Width <= 0 || affected.Height <= 0
            || workPixels > MaxWorkingPixels || !CanReserve(workPixels * 4)) return false;
        if (!LayerPixelSpace.TryCreate(layer.Transform, asset.Width, asset.Height, out var pixelSpace)) return false;
        var writeBounds = NativeWriteBounds(pixelSpace, affected);
        var writePixels = (long)writeBounds.Width * writeBounds.Height;
        if (writeBounds.Width <= 0 || writeBounds.Height <= 0) return false;
        // Work, the replacement asset and the largest reusable carried/snapshot buffer coexist briefly.
        // Check that total before allocating the first bitmap: an allocation failure after a dab begins is
        // recoverable, but avoiding it altogether keeps a large-canvas drag responsive and deterministic.
        var outputBytes = (long)asset.Width * asset.Height * 4;
        if (!CanReserveTotal(workPixels * 4 + outputBytes + ScratchEstimate(mode, settings))) return false;
        WarpEditsDiagnostics.Plan(plan.Dabs.Count, workPixels, writePixels);

        SKBitmap? work = null;
        try
        {
            work = DocumentRenderer.Allocate(workBounds.Width, workBounds.Height);
            using (var surface = new SKCanvas(work))
            {
                surface.Translate(-workBounds.Left, -workBounds.Top);
                DocumentRenderer.DrawLayerPixels(surface, layer);
            }
            using var stroke = new Stroke(work, workBounds.Location, mode, settings, plan);
            if (!stroke.Apply()) return false;
            return StrokeInto(layer, work, workBounds, plan, asset, pixelSpace, writeBounds);
        }
        catch (Exception error) when (error is InvalidOperationException or OutOfMemoryException
                                     or ArgumentOutOfRangeException or OverflowException)
        {
            return false;
        }
        finally
        {
            work?.Dispose();
        }
    }

    private static bool Valid(BrushSettings settings) => settings.Diameter is >= 2 and <= MaxDiameter
        && double.IsFinite(settings.Diameter) && double.IsFinite(settings.Hardness) && double.IsFinite(settings.Opacity)
        && settings.Opacity > 0;

    /// <summary>Copies only the transformed native pixels that could be under the stroke back from the ROI.</summary>
    private static bool StrokeInto(ImageLayer layer, SKBitmap work, SKRectI workBounds, WarpPlan plan,
        ImportedImage asset, LayerPixelSpace pixelSpace, SKRectI writeBounds)
    {
        SKBitmap? painted = null;
        try
        {
            painted = Bitmaps.Allocate(Bitmaps.ColorInfo(asset.Width, asset.Height));
            asset.Image.CopyTo(painted);
            var source = work.GetPixelSpan();
            var target = painted.GetPixelSpan();
            var covered = 0;
            for (var y = writeBounds.Top; y < writeBounds.Bottom; y++)
            {
                for (var x = writeBounds.Left; x < writeBounds.Right; x++)
                {
                    var at = pixelSpace.DocumentAt(x, y);
                    if (!plan.Covered(at)) continue;
                    var sx = (int)Math.Floor(at.X) - workBounds.Left;
                    var sy = (int)Math.Floor(at.Y) - workBounds.Top;
                    if ((uint)sx >= (uint)work.Width || (uint)sy >= (uint)work.Height) continue;
                    var from = sy * work.RowBytes + sx * 4;
                    var to = y * painted.RowBytes + x * 4;
                    var alpha = source[from + 3];
                    target[to + 3] = alpha;
                    target[to] = Straight(source[from], alpha);
                    target[to + 1] = Straight(source[from + 1], alpha);
                    target[to + 2] = Straight(source[from + 2], alpha);
                    covered++;
                }
            }
            if (covered == 0) return false;
            layer.Asset = ImportedImage.Create(painted, asset.Name);
            painted = null;
            return true;
        }
        finally
        {
            painted?.Dispose();
        }
    }

    private static byte Straight(byte premultiplied, byte alpha) =>
        alpha == 0 ? (byte)0 : (byte)Math.Min(255, Math.Round(premultiplied * 255.0 / alpha));

    private static bool CanReserve(long bytes)
    {
        if (bytes <= 0 || bytes > int.MaxValue * 16L) return false;
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        // Leave enough room for the layer copy, Skia bookkeeping and one bounded scratch buffer.
        return available <= 0 || bytes <= available / 4;
    }

    private static bool CanReserveTotal(long bytes)
    {
        if (bytes <= 0) return false;
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return available <= 0 || bytes <= available / 2;
    }

    private static long ScratchEstimate(WarpMode mode, BrushSettings settings)
    {
        var radius = (long)Math.Ceiling(settings.Diameter / 2);
        if (radius <= 0 || radius > MaxDiameter) return long.MaxValue;
        if (mode == WarpMode.Smudge)
        {
            var side = radius * 2 + 1;
            return CheckedBytes(side, side, sizeof(float) * 4);
        }
        // A generated Liquify dab advances no farther than its 2.5%-diameter spacing. Its pre-write
        // snapshot carries that movement halo plus the two-pixel interpolation safety margin.
        var margin = (long)Math.Ceiling(settings.Diameter * 0.025 * Math.Clamp(settings.Opacity, 0.01, 1)) + 2;
        var sideWithHalo = radius * 2 + margin * 2 + 1;
        return CheckedBytes(sideWithHalo, sideWithHalo, 4);
    }

    private static long CheckedBytes(long width, long height, int bytesPerPixel)
    {
        try
        {
            return checked(width * height * bytesPerPixel);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static SKRectI NativeWriteBounds(LayerPixelSpace space, SKRectI documentBounds)
    {
        if (documentBounds.Width <= 0 || documentBounds.Height <= 0) return default;
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var point in new[]
                 {
                     new SKPoint(documentBounds.Left, documentBounds.Top),
                     new SKPoint(documentBounds.Right, documentBounds.Top),
                     new SKPoint(documentBounds.Right, documentBounds.Bottom),
                     new SKPoint(documentBounds.Left, documentBounds.Bottom),
                 })
        {
            var pixel = space.PixelAt(point);
            left = Math.Min(left, pixel.X);
            top = Math.Min(top, pixel.Y);
            right = Math.Max(right, pixel.X);
            bottom = Math.Max(bottom, pixel.Y);
        }
        var x0 = (int)Math.Clamp(Math.Floor(left) - 2, 0, space.Width);
        var y0 = (int)Math.Clamp(Math.Floor(top) - 2, 0, space.Height);
        var x1 = (int)Math.Clamp(Math.Ceiling(right) + 2, 0, space.Width);
        var y1 = (int)Math.Clamp(Math.Ceiling(bottom) + 2, 0, space.Height);
        return x1 <= x0 || y1 <= y0 ? default : SKRectI.Create(x0, y0, x1 - x0, y1 - y0);
    }

    private readonly record struct Dab(SKPoint Previous, SKPoint Centre);

    /// <summary>
    /// An immutable preflight plan. It is built before any bitmap is allocated so large/invalid strokes fail
    /// predictably, and it indexes dabs in document-space buckets for fast native write-back coverage tests.
    /// </summary>
    private sealed class WarpPlan
    {
        private readonly Dictionary<long, List<SKPoint>> _dabsByCell = [];
        private readonly int _cellSize;
        private readonly double _coverageRadius;

        private WarpPlan(SKPoint start, List<Dab> dabs, SKRectI workBounds, SKRectI affectedBounds,
            double coverageRadius)
        {
            Start = start;
            Dabs = dabs;
            WorkBounds = workBounds;
            AffectedBounds = affectedBounds;
            _coverageRadius = coverageRadius;
            _cellSize = Math.Max(32, (int)Math.Ceiling(coverageRadius));
            foreach (var dab in dabs)
            {
                var key = CellKey(FloorDiv(dab.Centre.X, _cellSize), FloorDiv(dab.Centre.Y, _cellSize));
                if (!_dabsByCell.TryGetValue(key, out var cell)) _dabsByCell[key] = cell = [];
                cell.Add(dab.Centre);
            }
        }

        public SKPoint Start { get; }
        public List<Dab> Dabs { get; }
        public SKRectI WorkBounds { get; }
        public SKRectI AffectedBounds { get; }

        public bool Covered(SKPoint point)
        {
            var minX = FloorDiv(point.X - (float)_coverageRadius, _cellSize);
            var maxX = FloorDiv(point.X + (float)_coverageRadius, _cellSize);
            var minY = FloorDiv(point.Y - (float)_coverageRadius, _cellSize);
            var maxY = FloorDiv(point.Y + (float)_coverageRadius, _cellSize);
            var radiusSquared = _coverageRadius * _coverageRadius;
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    if (!_dabsByCell.TryGetValue(CellKey(x, y), out var dabs)) continue;
                    foreach (var centre in dabs)
                    {
                        var dx = point.X - centre.X;
                        var dy = point.Y - centre.Y;
                        if (dx * dx + dy * dy <= radiusSquared) return true;
                    }
                }
            }
            return false;
        }

        public static bool TryCreate(IReadOnlyList<SKPoint> points, WarpMode mode, BrushSettings settings,
            out WarpPlan plan)
        {
            plan = null!;
            // Pointer input normally arrives inside the canvas. The public Core API is also used by tests and
            // automation, so keep malformed far-away coordinates from overflowing an integer ROI before the
            // canvas clip can reject them.
            var coordinateLimit = DocumentLimits.MaxSide * 4f;
            if (points.Count == 0 || points.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y)
                || Math.Abs(point.X) > coordinateLimit || Math.Abs(point.Y) > coordinateLimit)) return false;
            var diameter = Math.Max(2, settings.Diameter);
            var spacing = Math.Max(1, diameter * (mode == WarpMode.Smudge ? 0.08 : 0.025));
            var dabs = new List<Dab>();
            // Keep the last point which actually produced a dab. The former streaming Stroke did not advance
            // its anchor for pointer samples shorter than the spacing, so a sequence of small moves accumulated
            // into a continuous dab rather than disappearing. Preserve that interaction while planning ahead.
            var anchor = points[0];
            for (var index = 1; index < points.Count; index++)
            {
                var to = points[index];
                var dx = to.X - anchor.X;
                var dy = to.Y - anchor.Y;
                var distance = Math.Sqrt((double)dx * dx + (double)dy * dy);
                if (distance < spacing) continue;
                var steps = (int)Math.Ceiling(distance / spacing);
                var previous = anchor;
                for (var step = 1; step <= steps; step++)
                {
                    if (dabs.Count >= MaxDabs) return false;
                    var t = (double)step / steps;
                    var centre = new SKPoint((float)(anchor.X + dx * t), (float)(anchor.Y + dy * t));
                    dabs.Add(new Dab(previous, centre));
                    previous = centre;
                }
                anchor = to;
            }
            if (dabs.Count == 0) return false;

            var brushRadius = (int)Math.Ceiling(diameter / 2);
            var coverageRadius = (diameter + 4) / 2;
            var work = default(SKRectI);
            var affected = default(SKRectI);
            var haveWork = false;
            var haveAffected = false;
            void AddWork(int left, int top, int right, int bottom)
            {
                if (right <= left || bottom <= top) return;
                var rect = SKRectI.Create(left, top, right - left, bottom - top);
                work = haveWork ? Union(work, rect) : rect;
                haveWork = true;
            }
            void AddAffected(SKPoint centre)
            {
                var left = (int)Math.Floor(centre.X - coverageRadius) - 2;
                var top = (int)Math.Floor(centre.Y - coverageRadius) - 2;
                var right = (int)Math.Ceiling(centre.X + coverageRadius) + 2;
                var bottom = (int)Math.Ceiling(centre.Y + coverageRadius) + 2;
                var rect = SKRectI.Create(left, top, right - left, bottom - top);
                affected = haveAffected ? Union(affected, rect) : rect;
                haveAffected = true;
            }

            if (mode == WarpMode.Smudge) AddCircle(points[0], brushRadius, AddWork);
            foreach (var dab in dabs)
            {
                AddAffected(dab.Centre);
                if (mode == WarpMode.Smudge)
                {
                    AddCircle(dab.Centre, brushRadius, AddWork);
                    continue;
                }
                var moveX = (dab.Centre.X - dab.Previous.X) * (float)Math.Clamp(settings.Opacity, 0.01, 1);
                var moveY = (dab.Centre.Y - dab.Previous.Y) * (float)Math.Clamp(settings.Opacity, 0.01, 1);
                var margin = (int)Math.Ceiling(Math.Max(Math.Abs(moveX), Math.Abs(moveY))) + 2;
                AddCircle(dab.Centre, brushRadius + margin, AddWork);
            }
            if (!haveWork || !haveAffected) return false;
            // The no-op edge band the former full-document copy retained must also exist in this local work surface.
            work = Union(work, affected);
            plan = new WarpPlan(points[0], dabs, work, affected, coverageRadius);
            return true;
        }

        private static void AddCircle(SKPoint centre, int radius, Action<int, int, int, int> add)
        {
            var x = (int)Math.Round(centre.X);
            var y = (int)Math.Round(centre.Y);
            add(x - radius, y - radius, x + radius + 1, y + radius + 1);
        }

        private static SKRectI Union(SKRectI left, SKRectI right)
        {
            var x0 = Math.Min(left.Left, right.Left);
            var y0 = Math.Min(left.Top, right.Top);
            var x1 = Math.Max(left.Right, right.Right);
            var y1 = Math.Max(left.Bottom, right.Bottom);
            return SKRectI.Create(x0, y0, x1 - x0, y1 - y0);
        }

        private static int FloorDiv(float value, int divisor) => (int)Math.Floor(value / divisor);
        private static long CellKey(int x, int y) => ((long)x << 32) | (uint)y;
    }

    /// <summary>
    /// The ROI is mutated dab by dab. Smudge rents one carried-colour float buffer for the stroke; Liquify
    /// rents one byte snapshot buffer and reuses it for every dab, preserving its before-write read semantics.
    /// </summary>
    private sealed class Stroke : IDisposable
    {
        private readonly SKBitmap _work;
        private readonly SKPointI _origin;
        private readonly WarpMode _mode;
        private readonly double _diameter;
        private readonly double _hardness;
        private readonly double _strength;
        private readonly int _radius;
        private readonly WarpPlan _plan;
        private float[]? _carried;
        private byte[]? _scratch;

        public Stroke(SKBitmap work, SKPointI origin, WarpMode mode, BrushSettings settings, WarpPlan plan)
        {
            _work = work;
            _origin = origin;
            _mode = mode;
            _diameter = Math.Max(2, settings.Diameter);
            _hardness = Math.Clamp(settings.Hardness, 0, 0.98);
            _strength = Math.Clamp(settings.Opacity, 0.01, 1);
            _radius = (int)Math.Ceiling(_diameter / 2);
            _plan = plan;
        }

        public bool Apply()
        {
            if (_mode == WarpMode.Smudge && !PickUp(Local(_plan.Start))) return false;
            foreach (var dab in _plan.Dabs)
            {
                if (_mode == WarpMode.Smudge)
                {
                    if (!Smudge(Local(dab.Centre))) return false;
                }
                else if (!Push(Local(dab.Previous), Local(dab.Centre)))
                {
                    return false;
                }
            }
            return true;
        }

        private SKPoint Local(SKPoint point) => new(point.X - _origin.X, point.Y - _origin.Y);

        private float Weight(float u)
        {
            if (u >= 1) return 0;
            var hardness = (float)_hardness;
            if (u <= hardness) return 1;
            var t = (1 - u) / (1 - hardness);
            return t * t * (3 - 2 * t);
        }

        private Span<byte> Pixels => _work.GetPixelSpan();

        private bool PickUp(SKPoint centre)
        {
            var side = _radius * 2 + 1;
            var count = checked(side * side * 4);
            if (!EnsureCarried(count)) return false;
            Array.Clear(_carried!, 0, count);
            var pixels = Pixels;
            var cx = (int)Math.Round(centre.X);
            var cy = (int)Math.Round(centre.Y);
            for (var dy = -_radius; dy <= _radius; dy++)
            {
                var y = cy + dy;
                if (y < 0 || y >= _work.Height) continue;
                for (var dx = -_radius; dx <= _radius; dx++)
                {
                    var x = cx + dx;
                    if (x < 0 || x >= _work.Width) continue;
                    var p = y * _work.RowBytes + x * 4;
                    var c = ((dy + _radius) * side + dx + _radius) * 4;
                    for (var channel = 0; channel < 4; channel++) _carried![c + channel] = pixels[p + channel];
                }
            }
            return true;
        }

        private bool Smudge(SKPoint centre)
        {
            if (_carried is null && !PickUp(centre)) return false;
            var side = _radius * 2 + 1;
            var pixels = Pixels;
            var cx = (int)Math.Round(centre.X);
            var cy = (int)Math.Round(centre.Y);
            var keep = (float)_strength;
            var inverseR = (float)(1 / (_diameter / 2));
            for (var dy = -_radius; dy <= _radius; dy++)
            {
                var y = cy + dy;
                if (y < 0 || y >= _work.Height) continue;
                for (var dx = -_radius; dx <= _radius; dx++)
                {
                    var x = cx + dx;
                    if (x < 0 || x >= _work.Width) continue;
                    var weight = Weight((float)Math.Sqrt(dx * dx + dy * dy) * inverseR);
                    if (weight <= 0) continue;
                    var p = y * _work.RowBytes + x * 4;
                    var c = ((dy + _radius) * side + dx + _radius) * 4;
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var under = (float)pixels[p + channel];
                        var painted = under + (_carried![c + channel] - under) * weight;
                        pixels[p + channel] = Bounded(painted);
                        // Preserve the established Windows carried-colour behaviour; its formula differs
                        // from a separate upstream change and needs a dedicated pixel fixture before altering it.
                        _carried[c + channel] = painted + (_carried[c + channel] - painted) * keep;
                    }
                }
            }
            return true;
        }

        private bool Push(SKPoint a, SKPoint b)
        {
            var moveX = (b.X - a.X) * (float)_strength;
            var moveY = (b.Y - a.Y) * (float)_strength;
            var margin = (int)Math.Ceiling(Math.Max(Math.Abs(moveX), Math.Abs(moveY))) + 2;
            var cx = (int)Math.Round(b.X);
            var cy = (int)Math.Round(b.Y);
            var x0 = Math.Max(0, cx - _radius - margin);
            var x1 = Math.Min(_work.Width - 1, cx + _radius + margin);
            var y0 = Math.Max(0, cy - _radius - margin);
            var y1 = Math.Min(_work.Height - 1, cy + _radius + margin);
            if (x0 > x1 || y0 > y1) return true;
            var width = x1 - x0 + 1;
            var height = y1 - y0 + 1;
            var count = checked(width * height * 4);
            if (!EnsureScratch(count)) return false;
            var pixels = Pixels;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var from = (y + y0) * _work.RowBytes + (x + x0) * 4;
                    var to = (y * width + x) * 4;
                    pixels.Slice(from, 4).CopyTo(_scratch!.AsSpan(to, 4));
                }
            }
            var inverseR = (float)(1 / (_diameter / 2));
            for (var dy = -_radius; dy <= _radius; dy++)
            {
                var y = cy + dy;
                if (y < y0 || y > y1) continue;
                for (var dx = -_radius; dx <= _radius; dx++)
                {
                    var x = cx + dx;
                    if (x < x0 || x > x1) continue;
                    var weight = Weight((float)Math.Sqrt(dx * dx + dy * dy) * inverseR);
                    if (weight <= 0) continue;
                    var sx = Math.Min(width - 1f, Math.Max(0f, x - x0 - moveX * weight));
                    var sy = Math.Min(height - 1f, Math.Max(0f, y - y0 - moveY * weight));
                    var ix = Math.Min(width - 2, (int)sx);
                    var iy = Math.Min(height - 2, (int)sy);
                    if (ix < 0 || iy < 0) continue;
                    var fx = sx - ix;
                    var fy = sy - iy;
                    var p = y * _work.RowBytes + x * 4;
                    var s00 = (iy * width + ix) * 4;
                    var s10 = s00 + 4;
                    var s01 = s00 + width * 4;
                    var s11 = s01 + 4;
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var top = _scratch![s00 + channel] + (_scratch[s10 + channel] - _scratch[s00 + channel]) * fx;
                        var bottom = _scratch[s01 + channel] + (_scratch[s11 + channel] - _scratch[s01 + channel]) * fx;
                        pixels[p + channel] = Bounded(top + (bottom - top) * fy);
                    }
                }
            }
            return true;
        }

        private bool EnsureCarried(int count)
        {
            var bytes = (long)count * sizeof(float);
            if (bytes > MaxScratchBytes || !CanReserve(bytes)) return false;
            if (_carried is not null && _carried.Length >= count) return true;
            if (_carried is not null) ArrayPool<float>.Shared.Return(_carried);
            try
            {
                _carried = ArrayPool<float>.Shared.Rent(count);
                WarpEditsDiagnostics.Scratch(bytes);
                return true;
            }
            catch (OutOfMemoryException)
            {
                _carried = null;
                return false;
            }
        }

        private bool EnsureScratch(int count)
        {
            var bytes = (long)count;
            if (bytes > MaxScratchBytes || !CanReserve(bytes)) return false;
            if (_scratch is not null && _scratch.Length >= count) return true;
            if (_scratch is not null) ArrayPool<byte>.Shared.Return(_scratch);
            try
            {
                _scratch = ArrayPool<byte>.Shared.Rent(count);
                WarpEditsDiagnostics.Scratch(bytes);
                return true;
            }
            catch (OutOfMemoryException)
            {
                _scratch = null;
                return false;
            }
        }

        public void Dispose()
        {
            if (_carried is not null) ArrayPool<float>.Shared.Return(_carried);
            if (_scratch is not null) ArrayPool<byte>.Shared.Return(_scratch);
            _carried = null;
            _scratch = null;
        }

        private static byte Bounded(float value) =>
            (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
    }
}
