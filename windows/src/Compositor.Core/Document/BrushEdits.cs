using Compositor.Core.Model;
using Compositor.Core.Pixels;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Document;

/// <summary>What the brush does to the pixels under it.</summary>
public enum BrushMode
{
    /// <summary>Paints the foreground colour.</summary>
    Paint,

    /// <summary>Paints what the layer shows elsewhere, offset from the brush: the Clone Stamp.</summary>
    Clone,

    /// <summary>Paints a softened copy of the layer, so going over an area again softens it further.</summary>
    Blur,

    /// <summary>Rebuilds the area from its surroundings: Spot Healing.</summary>
    Heal,
}

/// <summary>How Spot Healing works out what to put in the painted area.</summary>
public enum HealingMode
{
    ContentAware,
    CreateTexture,
    ProximityMatch,
}

/// <summary>
/// The brush as the tool header sets it. The diameter is in document pixels.
/// <para>
/// A class rather than a struct on purpose: <c>new BrushSettings()</c> on a record struct zero-initializes
/// it instead of applying the parameter defaults, which would quietly turn the brush into a zero-diameter
/// one that paints nothing.
/// </para>
/// </summary>
public sealed record BrushSettings(
    double Diameter = 40,
    double Hardness = 1,
    double Red = 0,
    double Green = 0,
    double Blue = 0,
    /// <summary>Caps the whole stroke, as in Photoshop: overlapping dabs never exceed it.</summary>
    double Opacity = 1,
    bool Erasing = false,
    BrushMode Mode = BrushMode.Paint,
    /// <summary>What the Clone Stamp copies from, as a whole-pixel offset in document pixels.</summary>
    SKPointI? CloneFrom = null,
    /// <summary>The Clone Stamp reads every visible layer as shown, rather than the active layer alone.</summary>
    bool CloneAllLayers = false,
    /// <summary>How a Spot Healing stroke rebuilds the area.</summary>
    HealingMode Healing = HealingMode.ContentAware,
    /// <summary>The grain Spot Healing adds when it fills smoothly. Zero picks a new pattern each stroke.</summary>
    uint Seed = 0,
    /// <summary>How far the tip lags the pointer, as a fraction of the way to it: nothing at zero, and the
    /// stroke trails the pointer more the higher it goes. The Mac build's own smoothing is the same idea.</summary>
    double Smoothing = 0,
    /// <summary>Whether a Clone Stamp stroke keeps copying from where the last one did, or takes the place it
    /// starts from as the new source each time.</summary>
    bool CloneAligned = true,
    /// <summary>
    /// How far a Blur stroke softens, in document pixels — the Radius the Mac build's options bar has beside the
    /// brush's own size, and its own default of 5. It is a setting of its own rather than something worked out
    /// from the diameter, so a wide brush can still be a gentle one.
    /// </summary>
    double BlurRadius = 5);

/// <summary>
/// Painting a stroke into a layer's own pixels. Mouse samples arrive in document coordinates, so they are
/// carried back through the layer's transform; the tip is stamped along the path at the spacing the Mac
/// build uses, and the whole stroke is capped by one opacity.
/// </summary>
public static class BrushEdits
{
    // A brush normally touches a small part of even a very large native layer. Coverage is therefore held in
    // that native-grid ROI, not as one float for every layer pixel. Apart from avoiding a document-sized
    // allocation for Clone/Blur, this gives an explicit, recoverable ceiling for a malformed or impractical
    // stroke instead of allowing an allocation to take the editor down.
    private const long MaxCoveragePixels = 8_000_000;

    private sealed class StrokeCoverage
    {
        private int _inkLeft;
        private int _inkTop;
        private int _inkRight;
        private int _inkBottom;

        public StrokeCoverage(SKRectI bounds, float[] values)
        {
            Bounds = bounds;
            Values = values;
            _inkLeft = Width;
            _inkTop = Height;
        }

        public SKRectI Bounds { get; }
        public float[] Values { get; }
        public int Width => Bounds.Width;
        public int Height => Bounds.Height;

        public float At(int x, int y) => (uint)(x - Bounds.Left) < (uint)Width
            && (uint)(y - Bounds.Top) < (uint)Height
            ? Values[(y - Bounds.Top) * Width + x - Bounds.Left]
            : 0;

        public void Combine(int x, int y, float value, bool hard)
        {
            var index = (y - Bounds.Top) * Width + x - Bounds.Left;
            Values[index] = hard ? Math.Max(Values[index], value) : Values[index] + value - Values[index] * value;
            if (Values[index] <= 0) return;
            var localX = x - Bounds.Left;
            var localY = y - Bounds.Top;
            _inkLeft = Math.Min(_inkLeft, localX);
            _inkTop = Math.Min(_inkTop, localY);
            _inkRight = Math.Max(_inkRight, localX + 1);
            _inkBottom = Math.Max(_inkBottom, localY + 1);
        }

        /// <summary>The tight half-open native-grid bounds of real coverage, not just the generous dab box.</summary>
        public SKRectI InkBounds()
        {
            return _inkRight <= _inkLeft || _inkBottom <= _inkTop ? default
                : SKRectI.Create(Bounds.Left + _inkLeft, Bounds.Top + _inkTop, _inkRight - _inkLeft, _inkBottom - _inkTop);
        }
    }

    /// <summary>Soft-brush falloff across the region between the hardness radius and the rim.</summary>
    public static double Falloff(double u)
    {
        const double k = 2.5;
        return Math.Max(0, (Math.Exp(-k * u * u) - Math.Exp(-k)) / (1 - Math.Exp(-k)));
    }

    /// <summary>The tip's coverage at <paramref name="distance"/> from its middle.</summary>
    public static double Tip(double distance, double radius, double hardness)
    {
        if (radius <= 0) return 0;
        var solid = radius * Math.Clamp(hardness, 0, 1);
        if (distance <= solid) return 1;
        if (distance >= radius || hardness >= 1) return 0;
        return Falloff((distance - solid) / (radius - solid));
    }

    /// <summary>How far apart dabs sit: a fraction of the diameter, so a soft tip is stamped closer.</summary>
    public static double Spacing(double diameter, double hardness) =>
        Math.Max(0.25, diameter * (hardness >= 1 ? 0.015 : 0.025));

    /// <summary>
    /// Gives a blank layer pixels the size of its rectangle, which is when the Mac build allocates them: on
    /// the first paint rather than when the layer is made. A folder has no pixels of its own, so it is left
    /// alone: giving it some would make a document that cannot be saved or loaded back.
    /// </summary>
    public static bool EnsurePixels(CanvasDocument document, Guid layerID)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Asset: null } layer) return false;
        if (layer.IsGroup) return false;
        var width = Math.Max(1, (int)Math.Round(layer.Transform.Width));
        var height = Math.Max(1, (int)Math.Round(layer.Transform.Height));
        if (width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide
            || (long)width * height > DocumentLimits.MaxSurfacePixels)
        {
            return false;
        }
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(SKColors.Transparent);
        layer.Asset = ImportedImage.Create(bitmap, layer.Name);
        return true;
    }

    /// <summary>
    /// Paints a stroke through the given document points. The layer is given a new picture — the old one
    /// stays as it was, which is what lets undo bring it back — and an edit of any kind drops whatever made
    /// the layer a live shape or a live text. A folder is not paintable, as the Mac build's tool refuses it.
    /// </summary>
    public static bool Paint(CanvasDocument document, Guid layerID, IReadOnlyList<SKPoint> points,
        BrushSettings settings)
    {
        if (points.Count == 0 || settings.Diameter <= 0 || settings.Opacity <= 0) return false;
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Asset: { } asset } layer) return false;
        if (layer.IsGroup) return false;
        if (settings.Mode == BrushMode.Clone && settings.CloneFrom is null) return false;
        var width = asset.Width;
        var height = asset.Height;
        if (width <= 0 || height <= 0) return false;

        if (!LayerPixelSpace.TryCreate(layer.Transform, width, height, out var pixelSpace)) return false;
        if (!Stroke(document, points, settings, pixelSpace, out var coverage)) return false;

        // Current Layer samples borrow the immutable pre-stroke image. Blur and Sample All Layers own their
        // snapshots and are disposed below. Either way the sample is fixed before the first destination pixel
        // is written, so one stroke never reads an earlier dab from itself.
        using var sample = CreateColorSample(document, layer, settings, pixelSpace, coverage);
        if (settings.Mode is BrushMode.Clone or BrushMode.Blur && sample is null) return false;

        SKBitmap? painted = null;
        try
        {
            painted = Bitmaps.Allocate(Bitmaps.ColorInfo(width, height));
            asset.Image.CopyTo(painted);
            var paintedNow = sample is not null
                ? ApplySampled(painted, coverage, settings, sample, pixelSpace)
                : settings.Mode == BrushMode.Heal ? Heal(painted, coverage, settings) : Apply(painted, coverage, settings);
            if (!paintedNow) return false;
            layer.Asset = ImportedImage.Create(painted, asset.Name);
            painted = null;
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            painted?.Dispose();
        }
    }

    /// <summary>
    /// Spot Healing: the area the stroke covered is rebuilt from what surrounds it, by the ported kernel,
    /// in one go. The Mac build shows a dark wash while the stroke is being drawn and does this when it
    /// ends; this port does the same work without the wash, so the picture changes on release.
    /// </summary>
    private static bool Heal(SKBitmap painted, StrokeCoverage coverage, BrushSettings settings)
    {
        var width = painted.Width;
        var height = painted.Height;
        var covered = coverage.InkBounds();
        if (covered.Width <= 0 || covered.Height <= 0) return true;

        // Room for the kernel's patch search, which looks up to about three spot-widths away.
        var portrait = Math.Max(covered.Width, covered.Height);
        var reach = (int)Math.Ceiling((portrait + 32) * 3.2);
        var left = Math.Max(0, covered.Left - reach);
        var top = Math.Max(0, covered.Top - reach);
        var right = Math.Min(width, covered.Right + reach);
        var bottom = Math.Min(height, covered.Bottom + reach);
        var w = right - left;
        var h = bottom - top;
        if (w <= 0 || h <= 0) return true;

        // The kernel works on premultiplied pixels, which is what a render is held in; a layer's own
        // pixels are straight, so the region is premultiplied on the way in and unpremultiplied on the way
        // back out.
        var room = new byte[w * h * 4];
        var gray = new byte[w * h];
        var pixels = painted.GetPixelSpan();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var from = (top + y) * painted.RowBytes + (left + x) * 4;
                var to = (y * w + x) * 4;
                var alpha = pixels[from + 3];
                for (var channel = 0; channel < 3; channel++)
                {
                    room[to + channel] = (byte)((pixels[from + channel] * alpha + 127) / 255);
                }
                room[to + 3] = alpha;
                gray[y * w + x] = (byte)Math.Clamp(Math.Round(coverage.At(left + x, top + y) * 255), 0, 255);
            }
        }

        var seed = settings.Seed != 0 ? settings.Seed : (uint)Random.Shared.Next(1, int.MaxValue);
        var mode = (int)settings.Healing;
        if (HealPixels.SpotHeal(room, gray, w, h, w * 4, (float)settings.Opacity, mode, seed) != 0) return false;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var from = (y * w + x) * 4;
                var to = (top + y) * painted.RowBytes + (left + x) * 4;
                var alpha = room[from + 3];
                for (var channel = 0; channel < 3; channel++)
                {
                    pixels[to + channel] = alpha == 0 ? (byte)0 : (byte)Math.Min(255, (room[from + channel] * 255 + alpha / 2) / alpha);
                }
                pixels[to + 3] = alpha;
            }
        }
        return true;
    }

    /// <summary>
    /// The coverage of a stroke over one pixel grid: the tip stamped along the path at the spacing the Mac
    /// build uses, scaled by the selection so a dab that straddles a feathered edge is painted in part
    /// rather than all or nothing. False when the selection's coverage will not fit in memory, since
    /// painting without it would paint outside the selection.
    /// </summary>
    private static bool Stroke(CanvasDocument document, IReadOnlyList<SKPoint> points, BrushSettings settings,
        LayerPixelSpace pixelSpace, out StrokeCoverage coverage)
    {
        coverage = null!;
        var radius = settings.Diameter / 2;
        var bounds = StrokeBounds(points, radius, pixelSpace);
        var pixels = (long)bounds.Width * bounds.Height;
        if (bounds.Width <= 0 || bounds.Height <= 0 || pixels > MaxCoveragePixels) return false;
        try
        {
            coverage = new StrokeCoverage(bounds, new float[checked((int)pixels)]);
        }
        catch (OutOfMemoryException)
        {
            return false;
        }
        var spacing = Spacing(settings.Diameter, settings.Hardness);
        var hard = settings.Hardness >= 1;
        var region = document.Selection.CoverageRect(document.Width, document.Height);
        SKBitmap? clip;
        try
        {
            clip = document.Selection.Coverage(region);
        }
        catch (Exception error) when (error is InvalidOperationException or OutOfMemoryException)
        {
            return false;
        }
        using var _ = clip;
        ReadOnlySpan<byte> clipped = clip is null ? default : clip.GetPixelSpan();
        var selection = new Clip(clipped, clip?.RowBytes ?? 0, region);
        // The first dab sits on the first sample; the rest follow it at even spacing, carrying whatever
        // distance is left over the end of one run into the next so a fast pointer leaves no gaps.
        Stamp(coverage, points[0], radius, pixelSpace, hard, settings.Hardness, selection);
        var next = spacing;
        for (var index = 1; index < points.Count; index++)
        {
            var from = points[index - 1];
            var to = points[index];
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var length = Math.Sqrt((double)dx * dx + (double)dy * dy);
            if (length <= 0) continue;
            while (next <= length)
            {
                var at = new SKPoint((float)(from.X + dx * next / length), (float)(from.Y + dy * next / length));
                Stamp(coverage, at, radius, pixelSpace, hard, settings.Hardness, selection);
                next += spacing;
            }
            next -= length;
        }
        return true;
    }

    /// <summary>
    /// The smallest practical native-grid rectangle that can contain every dab. A document-space line stays
    /// inside the union of its endpoint boxes after an affine transform, so calculating this before the
    /// coverage allocation avoids one float for every pixel of a large layer.
    /// </summary>
    private static SKRectI StrokeBounds(IReadOnlyList<SKPoint> points, double radius, LayerPixelSpace pixelSpace)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var point in points)
        {
            DabExtents(point, radius, pixelSpace, out var dabMinX, out var dabMinY, out var dabMaxX, out var dabMaxY);
            minX = Math.Min(minX, dabMinX);
            minY = Math.Min(minY, dabMinY);
            maxX = Math.Max(maxX, dabMaxX);
            maxY = Math.Max(maxY, dabMaxY);
        }
        var left = GridFloor(minX - 1, pixelSpace.Width);
        var top = GridFloor(minY - 1, pixelSpace.Height);
        var right = GridCeiling(maxX + 1, pixelSpace.Width);
        var bottom = GridCeiling(maxY + 1, pixelSpace.Height);
        return right <= left || bottom <= top ? default : SKRectI.Create(left, top, right - left, bottom - top);
    }

    private static SKRectI DabBounds(SKPoint centre, double radius, LayerPixelSpace pixelSpace)
    {
        DabExtents(centre, radius, pixelSpace, out var minX, out var minY, out var maxX, out var maxY);
        var left = GridFloor(minX - 1, pixelSpace.Width);
        var top = GridFloor(minY - 1, pixelSpace.Height);
        var right = GridCeiling(maxX + 1, pixelSpace.Width);
        var bottom = GridCeiling(maxY + 1, pixelSpace.Height);
        return right <= left || bottom <= top ? default : SKRectI.Create(left, top, right - left, bottom - top);
    }

    private static void DabExtents(SKPoint centre, double radius, LayerPixelSpace pixelSpace,
        out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = double.MaxValue;
        minY = double.MaxValue;
        maxX = double.MinValue;
        maxY = double.MinValue;
        foreach (var (dx, dy) in new[] { (-radius, -radius), (radius, -radius), (radius, radius), (-radius, radius) })
        {
            var corner = pixelSpace.ToPixel.MapPoint(centre.X + (float)dx, centre.Y + (float)dy);
            minX = Math.Min(minX, corner.X);
            minY = Math.Min(minY, corner.Y);
            maxX = Math.Max(maxX, corner.X);
            maxY = Math.Max(maxY, corner.Y);
        }
    }

    private static int GridFloor(double value, int maximum) => !double.IsFinite(value) ? value > 0 ? maximum : 0
        : (int)Math.Clamp(Math.Floor(value), 0, maximum);

    private static int GridCeiling(double value, int maximum) => !double.IsFinite(value) ? value > 0 ? maximum : 0
        : (int)Math.Clamp(Math.Ceiling(value), 0, maximum);

    /// <summary>
    /// Gives a uniform mask the pixels of the layer it covers, which is what the Mac build does when a brush
    /// first paints on one: until then the mask is one value stretched over the layer, and painting on it
    /// would write to that single pixel.
    /// </summary>
    public static bool GrowMask(CanvasDocument document, Guid layerID)
    {
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Mask: { } mask } layer) return false;
        if (mask.Asset.Width > 1 || mask.Asset.Height > 1) return false;
        // A linked solid mask adopts the active layer's native grid. An unlinked placed solid mask has no
        // layer-grid relationship, so it adopts one pixel per document pixel of its own placement instead.
        // In particular, a 1000px layer shown at 100px must not acquire a 100px linked mask.
        var width = mask.Placement is { } placement
            ? Math.Max(1, (int)Math.Round(placement.Width))
            : layer.Asset?.Width ?? Math.Max(1, (int)Math.Round(layer.Transform.Width));
        var height = mask.Placement is { } placed
            ? Math.Max(1, (int)Math.Round(placed.Height))
            : layer.Asset?.Height ?? Math.Max(1, (int)Math.Round(layer.Transform.Height));
        if (width > DocumentLimits.MaxSide || height > DocumentLimits.MaxSide
            || (long)width * height > DocumentLimits.MaxSurfacePixels)
        {
            return false;
        }
        var grew = Bitmaps.Allocate(Bitmaps.MaskInfo(width, height));
        grew.Erase(mask.Asset.Image.GetPixel(0, 0));
        layer.Mask = mask.Replacing(ImportedImage.Create(grew, mask.Asset.Name));
        return true;
    }

    /// <summary>
    /// Paints a stroke into a layer's mask instead of its pixels: white reveals what the mask hides and
    /// black hides what it shows, at the brush's opacity. The mask keeps its placement, whether it is
    /// enabled and whether it is linked, and the layer's own pixels are not touched.
    /// </summary>
    public static bool PaintMask(CanvasDocument document, Guid layerID, IReadOnlyList<SKPoint> points,
        BrushSettings settings)
    {
        if (points.Count == 0 || settings.Diameter <= 0 || settings.Opacity <= 0) return false;
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Mask: { } initialMask }) return false;
        // A mask the menu made is one pixel stretched over the layer, which a dab cannot land in: it takes
        // the layer's own pixels first, as the Mac build's brush does.
        if (initialMask.Asset.Width <= 1 && initialMask.Asset.Height <= 1 && !GrowMask(document, layerID)) return false;
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Mask: { } mask } layer) return false;
        var width = mask.Asset.Width;
        var height = mask.Asset.Height;
        if (width <= 0 || height <= 0) return false;
        if (!LayerPixelSpace.TryCreate(layer.MaskTransform, width, height, out var pixelSpace)) return false;
        if (!Stroke(document, points, settings, pixelSpace, out var coverage)) return false;

        SKBitmap? painted = null;
        try
        {
            painted = Bitmaps.Allocate(Bitmaps.MaskInfo(width, height));
            mask.Asset.Image.CopyTo(painted);
            if (settings.Mode == BrushMode.Blur)
            {
                // A placed mask's outside is the mask's edge majority, not transparent. This deliberately
                // runs on the mask's own pixel grid and preserves an unlinked mask placement.
                using var sample = NativeBlurMaskSample.Create(mask.Asset.Image,
                    LayerMask.Background(mask.Asset.Thumbnail), pixelSpace, coverage.InkBounds(), settings.BlurRadius,
                    (long)document.Width * document.Height);
                if (sample is null || !ApplyBlurredMask(painted, coverage, settings, sample)) return false;
            }
            else
            {
                ApplyToMask(painted.GetPixelSpan(), painted.RowBytes, coverage, settings);
            }
            layer.Mask = mask.Replacing(ImportedImage.Create(painted, mask.Asset.Name));
            painted = null;
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            painted?.Dispose();
        }
    }

    /// <summary>Moves a mask's gray level towards the brush's, so a mask stroke sets coverage rather than painting a colour.</summary>
    private static void ApplyToMask(Span<byte> mask, int stride, StrokeCoverage coverage, BrushSettings settings)
    {
        var value = (byte)Math.Clamp(Math.Round((settings.Red + settings.Green + settings.Blue) / 3 * 255), 0, 255);
        for (var y = 0; y < coverage.Height; y++)
        {
            for (var x = 0; x < coverage.Width; x++)
            {
                var alpha = Math.Clamp(coverage.Values[y * coverage.Width + x] * settings.Opacity, 0, 1);
                if (alpha <= 0) continue;
                var at = (coverage.Bounds.Top + y) * stride + coverage.Bounds.Left + x;
                mask[at] = (byte)Math.Round(mask[at] + (value - mask[at]) * alpha);
            }
        }
    }

    private static bool ApplyBlurredMask(SKBitmap painted, StrokeCoverage coverage, BrushSettings settings,
        NativeBlurMaskSample sample)
    {
        // Process coverage tile by tile. The lazy source can then release old tiles without ever rebuilding
        // one during a long, wide stroke, while still only evaluating source pieces that contain real ink.
        var ink = coverage.InkBounds();
        for (var top = TileStart(ink.Top); top < ink.Bottom; top += NativeBrushSampleLimits.BlurTileSize)
        {
            for (var left = TileStart(ink.Left); left < ink.Right; left += NativeBrushSampleLimits.BlurTileSize)
            {
                var tile = SKRectI.Intersect(coverage.Bounds, SKRectI.Create(left, top,
                    NativeBrushSampleLimits.BlurTileSize, NativeBrushSampleLimits.BlurTileSize));
                if (tile.Width <= 0 || tile.Height <= 0 || !HasCoverage(coverage, tile)) continue;
                if (!ApplyBlurredMaskRange(painted, coverage, settings, sample, tile)) return false;
            }
        }
        return !sample.Failed;
    }

    private static bool ApplyBlurredMaskRange(SKBitmap painted, StrokeCoverage coverage, BrushSettings settings,
        NativeBlurMaskSample sample, SKRectI range)
    {
        var pixels = painted.GetPixelSpan();
        for (var y = range.Top; y < range.Bottom; y++)
        {
            for (var x = range.Left; x < range.Right; x++)
            {
                var alpha = Math.Clamp(coverage.At(x, y) * settings.Opacity, 0, 1);
                if (alpha <= 0) continue;
                if (!sample.TryRead(x, y, out var value)) return !sample.Failed;
                var at = y * painted.RowBytes + x;
                pixels[at] = (byte)Math.Round(pixels[at] + (value - pixels[at]) * alpha);
            }
        }
        return true;
    }

    /// <summary>
    /// Captures a source before the stroke changes the destination. Current-layer Clone and Blur remain in
    /// the layer's native grid; only Clone's Sample All Layers option owns a document-space composite.
    /// </summary>
    private static BrushColorSample? CreateColorSample(CanvasDocument document, ImageLayer layer,
        BrushSettings settings, LayerPixelSpace pixelSpace, StrokeCoverage coverage)
    {
        try
        {
            if (settings.Mode == BrushMode.Clone)
            {
                if (!settings.CloneAllLayers) return new NativeLayerColorSample(layer.Asset!.Image, pixelSpace);
                return new DocumentColorSample(DocumentRenderer.Render(document));
            }
            return settings.Mode == BrushMode.Blur
                ? NativeBlurColorSample.Create(layer.Asset!.Image, pixelSpace, coverage.InkBounds(), settings.BlurRadius,
                    (long)document.Width * document.Height)
                : null;
        }
        catch (Exception error) when (error is InvalidOperationException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Paints what the fixed source holds through native-grid coverage: the Clone Stamp's copy, or Blur's
    /// softened snapshot. The source maps each target pixel through its declared coordinate space, and its
    /// alpha scales the dab — so a transparent source leaves the layer as it was, as drawing it would.
    /// </summary>
    private static bool ApplySampled(SKBitmap painted, StrokeCoverage coverage, BrushSettings settings,
        BrushColorSample sample, LayerPixelSpace pixelSpace)
    {
        if (sample is NativeBlurColorSample)
        {
            var ink = coverage.InkBounds();
            for (var top = TileStart(ink.Top); top < ink.Bottom; top += NativeBrushSampleLimits.BlurTileSize)
            {
                for (var left = TileStart(ink.Left); left < ink.Right; left += NativeBrushSampleLimits.BlurTileSize)
                {
                    var tile = SKRectI.Intersect(coverage.Bounds, SKRectI.Create(left, top,
                        NativeBrushSampleLimits.BlurTileSize, NativeBrushSampleLimits.BlurTileSize));
                    if (tile.Width <= 0 || tile.Height <= 0 || !HasCoverage(coverage, tile)) continue;
                    if (!ApplySampledRange(painted, coverage, settings, sample, pixelSpace, tile)) return false;
                }
            }
            return !sample.Failed;
        }
        return ApplySampledRange(painted, coverage, settings, sample, pixelSpace, coverage.Bounds);
    }

    private static bool ApplySampledRange(SKBitmap painted, StrokeCoverage coverage, BrushSettings settings,
        BrushColorSample sample, LayerPixelSpace pixelSpace, SKRectI range)
    {
        var offset = settings.Mode == BrushMode.Clone ? settings.CloneFrom ?? default : default;
        var destination = painted.GetPixelSpan();
        for (var gridY = range.Top; gridY < range.Bottom; gridY++)
        {
            for (var gridX = range.Left; gridX < range.Right; gridX++)
            {
                var amount = coverage.At(gridX, gridY);
                if (amount <= 0) continue;
                if (!sample.TryRead(pixelSpace, gridX, gridY, offset, out var colour))
                {
                    if (sample.Failed) return false;
                    continue;
                }
                if (colour.Alpha == 0) continue;
                var alpha = Math.Clamp(amount * settings.Opacity * (colour.Alpha / 255.0), 0, 1);
                if (alpha <= 0) continue;
                var at = gridY * painted.RowBytes + gridX * 4;
                Blend(destination, at, alpha, colour.Red / 255.0, colour.Green / 255.0, colour.Blue / 255.0);
            }
        }
        return true;
    }

    private static int TileStart(int coordinate) => coordinate / NativeBrushSampleLimits.BlurTileSize
        * NativeBrushSampleLimits.BlurTileSize;

    private static bool HasCoverage(StrokeCoverage coverage, SKRectI range)
    {
        for (var y = range.Top; y < range.Bottom; y++)
        {
            for (var x = range.Left; x < range.Right; x++)
            {
                if (coverage.At(x, y) > 0) return true;
            }
        }
        return false;
    }

    /// <summary>Straight (unpremultiplied) source-over, which is the form a layer's pixels are held in.</summary>
    internal static void Blend(Span<byte> pixels, int at, double alpha, double red, double green, double blue)
    {
        var under = pixels[at + 3] / 255.0;
        var outAlpha = alpha + under * (1 - alpha);
        if (outAlpha <= 0) return;
        Span<double> colour = stackalloc double[3];
        colour[0] = red;
        colour[1] = green;
        colour[2] = blue;
        for (var channel = 0; channel < 3; channel++)
        {
            var behind = pixels[at + channel] / 255.0 * under * (1 - alpha);
            pixels[at + channel] = (byte)Math.Clamp(Math.Round((colour[channel] * alpha + behind) / outAlpha * 255), 0, 255);
        }
        pixels[at + 3] = (byte)Math.Round(outAlpha * 255);
    }

    /// <summary>The transform that places a layer's pixels on the document.</summary>
    public static SKMatrix PixelToDocument(Model.LayerTransform transform, int width, int height)
    {
        var scaleX = transform.Width / width * (transform.FlipX ? -1 : 1);
        var scaleY = transform.Height / height * (transform.FlipY ? -1 : 1);
        var radians = transform.Radians;
        var a = scaleX * Math.Cos(radians);
        var c = -scaleY * Math.Sin(radians);
        var b = scaleX * Math.Sin(radians);
        var d = scaleY * Math.Cos(radians);
        return new SKMatrix
        {
            ScaleX = (float)a,
            SkewX = (float)c,
            TransX = (float)(transform.CenterX - (a * width + c * height) / 2),
            SkewY = (float)b,
            ScaleY = (float)d,
            TransY = (float)(transform.CenterY - (b * width + d * height) / 2),
            Persp2 = 1,
        };
    }

    /// <summary>
    /// One dab, measured in document pixels so a rotated or stretched layer still gets a round brush: every
    /// pixel near the dab is carried back to the document and asked how far it is from the middle. The
    /// selection scales each pixel, so a dab that straddles a feathered edge is painted in part.
    /// </summary>
    private static void Stamp(StrokeCoverage coverage, SKPoint centre, double radius, LayerPixelSpace pixelSpace,
        bool hard, double hardness, Clip selection)
    {
        var box = DabBounds(centre, radius, pixelSpace);
        for (var y = box.Top; y < box.Bottom; y++)
        {
            for (var x = box.Left; x < box.Right; x++)
            {
                var at = pixelSpace.DocumentAt(x, y);
                var distance = Math.Sqrt(Math.Pow(at.X - centre.X, 2) + Math.Pow(at.Y - centre.Y, 2));
                var tip = Tip(distance, radius, hardness) * selection.At(at.X, at.Y);
                if (tip > 0) coverage.Combine(x, y, (float)tip, hard);
            }
        }
    }

    /// <summary>
    /// The selection as gray coverage over one rectangle of the document. Empty pixels mean there is no
    /// selection at all, so everything is painted; a rectangle with no size means nothing is.
    /// </summary>
    private readonly ref struct Clip
    {
        private readonly ReadOnlySpan<byte> _pixels;
        private readonly int _stride;
        private readonly SKRectI _region;

        public Clip(ReadOnlySpan<byte> pixels, int stride, SKRectI region)
        {
            _pixels = pixels;
            _stride = stride;
            _region = region;
        }

        /// <summary>How much of a document pixel the selection lets through, from 0 to 1.</summary>
        public float At(float x, float y)
        {
            if (_pixels.IsEmpty) return 1;
            var column = (int)Math.Floor(x) - _region.Left;
            var row = (int)Math.Floor(y) - _region.Top;
            if (column < 0 || row < 0 || column >= _region.Width || row >= _region.Height) return 0;
            return _pixels[row * _stride + column] / 255f;
        }
    }

    /// <summary>Paints the colour through the coverage, or takes the alpha away when erasing.</summary>
    private static bool Apply(SKBitmap pixels, StrokeCoverage coverage, BrushSettings settings)
    {
        var span = pixels.GetPixelSpan();
        for (var y = 0; y < coverage.Height; y++)
        {
            for (var x = 0; x < coverage.Width; x++)
            {
                var alpha = Math.Clamp(coverage.Values[y * coverage.Width + x] * settings.Opacity, 0, 1);
                if (alpha <= 0) continue;
                var at = (coverage.Bounds.Top + y) * pixels.RowBytes + (coverage.Bounds.Left + x) * 4;
                if (settings.Erasing)
                {
                    span[at + 3] = (byte)Math.Round(span[at + 3] / 255.0 * (1 - alpha) * 255);
                    continue;
                }
                Blend(span, at, alpha, settings.Red, settings.Green, settings.Blue);
            }
        }
        return true;
    }

}
