using Compositor.Core.Format;
using Compositor.Core.Document;
using Compositor.Core.Model;
using Compositor.Core.Pixels;
using SkiaSharp;

namespace Compositor.Core.Rendering;

/// <summary>
/// Flattens a document to one bitmap, the way the Mac build's export path does.
/// <para>
/// Folders are pass-through and never get a surface of their own: a folder's opacity multiplies into each
/// descendant, and a folder's mask clips each descendant. A layer's mask and its clip's coverage are both
/// clips, and a clip bounds the whole layer, not just the part the clip's image happens to cover — so
/// restricting to one builds a fresh bitmap rather than multiplying in place. A clipping stack (a base
/// plus the contiguous siblings clipped to it) is composited once, with the base's blend mode and
/// effective opacity, after its members have blended into it with their own.
/// </para>
/// </summary>
public static class DocumentRenderer
{
    /// <summary>
    /// Renders the whole canvas in one buffer. The result is premultiplied sRGB on a transparent
    /// background. A canvas too big for one buffer has to go through <see cref="RenderRegion"/> in pieces.
    /// </summary>
    public static SKBitmap Render(CanvasDocument document) =>
        RenderRegion(document, SKRectI.Create(0, 0, document.Width, document.Height));

    /// <summary>
    /// Renders one rectangle of the canvas, in the rectangle's own coordinates, so a canvas larger than any
    /// one buffer can be drawn in pieces.
    /// <para>
    /// A blur-type adjustment reads its neighbours, so the rectangle is rendered with a halo around it and
    /// cut back afterwards: everything that could reach inside is rendered too, which is what makes a piece
    /// identical to the same piece of a whole-canvas render.
    /// </para>
    /// </summary>
    public static SKBitmap RenderRegion(CanvasDocument document, SKRectI region)
    {
        var result = Allocate(region.Width, region.Height);
        if (region.Width <= 0 || region.Height <= 0) return result;
        var canvas = SKRectI.Create(0, 0, document.Width, document.Height);
        var wanted = SKRectI.Intersect(region, canvas);
        if (wanted.Width <= 0 || wanted.Height <= 0) return result;
        var grown = SKRectI.Intersect(Inflate(wanted, Halo(document)), canvas);
        using var bitmap = Allocate(grown.Width, grown.Height);
        new Renderer(document, bitmap, grown).Draw();
        using var surface = new SKCanvas(result);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        // The grown render's inner rectangle, into the piece's own corner.
        surface.DrawBitmap(bitmap,
            SKRect.Create(wanted.Left - grown.Left, wanted.Top - grown.Top, wanted.Width, wanted.Height),
            SKRect.Create(wanted.Left - region.Left, wanted.Top - region.Top, wanted.Width, wanted.Height),
            OneToOne, paint);
        return result;
    }

    /// <summary>
    /// The halo the document needs: what every visible blur-type adjustment can reach, summed, because one
    /// blur's output can feed the next. Hidden layers are left out since they change nothing.
    /// </summary>
    private static int Halo(CanvasDocument document)
    {
        var halo = 0L;
        foreach (var layer in document.Layers)
        {
            if (!layer.IsVisible) continue;
            halo += layer.Adjustment?.SamplingMargin ?? 0;
        }
        return (int)Math.Min(halo, DocumentLimits.MaxSide);
    }

    private static SKRectI Inflate(SKRectI rect, int by) =>
        SKRectI.Create(rect.Left - by, rect.Top - by, rect.Width + by * 2, rect.Height + by * 2);

    /// <summary>A rendered layer is already at its final size, so resampling it would only blur it.</summary>
    private static readonly SKSamplingOptions OneToOne = new(SKFilterMode.Nearest, SKMipmapMode.None);

    internal static SKBitmap Allocate(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var bitmap = new SKBitmap(info);
        if (!bitmap.ReadyToDraw) throw new InvalidOperationException($"Could not allocate a {width}x{height} bitmap.");
        bitmap.Erase(SKColors.Transparent);
        return bitmap;
    }

    /// <summary>
    /// One layer's own pixels drawn at its transform, whole, with no mask, no opacity and no blend — how a
    /// command-click reads them, and what the sample a selection tool matches against holds.
    /// </summary>
    public static void DrawLayerPixels(SKCanvas canvas, ImageLayer layer)
    {
        if (layer.Asset is not { } asset) return;
        using var paint = new SKPaint { IsAntialias = true };
        DrawTransformed(canvas, asset.Image, layer.Transform, SKBlendMode.SrcOver, paint);
    }

    /// <summary>
    /// A layer whose content is not a raster is not drawn yet, and drawing it as nothing would quietly
    /// change the picture. This says so instead.
    /// </summary>
    internal static NotSupportedException Unsupported(ImageLayer layer) =>
        new($"Rendering layer effects is not implemented yet (layer '{layer.Name}').");

    /// <summary>
    /// Draws one image at a layer transform: rotation clockwise about the rect's center, with the flips
    /// applied before the rotation reads it.
    /// </summary>
    private static void DrawTransformed(SKCanvas canvas, SKBitmap image, Model.LayerTransform transform,
        SKBlendMode blend, SKPaint paint)
    {
        canvas.Save();
        canvas.Translate((float)(transform.X + transform.Width / 2), (float)(transform.Y + transform.Height / 2));
        canvas.RotateDegrees((float)transform.Rotation);
        canvas.Scale(transform.FlipX ? -1 : 1, transform.FlipY ? -1 : 1);
        canvas.Translate((float)(-transform.Width / 2), (float)(-transform.Height / 2));
        paint.BlendMode = blend;
        using var source = SKImage.FromBitmap(image);
        canvas.DrawImage(source, SKRect.Create(0, 0, (float)transform.Width, (float)transform.Height),
            Sampling(transform, image.Width, image.Height), paint);
        canvas.Restore();
    }

    /// <summary>
    /// How an image is resampled into its rectangle. The Mac build overrides the requested quality: an
    /// upright image that lands exactly on its own pixels is drawn with none at all, and anything being
    /// shrunk is drawn low, because a filter over a reduction only blurs it further.
    /// </summary>
    private static SKSamplingOptions Sampling(Model.LayerTransform transform, int sourceWidth, int sourceHeight)
    {
        if (transform.Rotation == 0 && transform.Width == sourceWidth && transform.Height == sourceHeight) return OneToOne;
        if (transform.Width <= sourceWidth && transform.Height <= sourceHeight)
        {
            return new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        }
        return Sampling(transform.Sampling);
    }

    /// <summary>The filter a layer was imported or placed with.</summary>
    private static SKSamplingOptions Sampling(LayerSampling sampling) => sampling switch
    {
        LayerSampling.Nearest => new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
        LayerSampling.Smooth => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
        _ => new SKSamplingOptions(SKCubicResampler.Mitchell),
    };

    private sealed class Renderer
    {
        private readonly CanvasDocument _document;
        private readonly SKBitmap _canvas;
        private readonly SKRectI _region;
        private readonly Dictionary<Guid, ImageLayer> _byID = [];
        private readonly Dictionary<Guid, List<ImageLayer>> _children = [];

        public Renderer(CanvasDocument document, SKBitmap canvas, SKRectI region)
        {
            _document = document;
            _canvas = canvas;
            _region = region;
            foreach (var layer in document.Layers)
            {
                _byID[layer.ID] = layer;
                if (layer.ParentID is { } parent)
                {
                    if (!_children.TryGetValue(parent, out var siblings)) _children[parent] = siblings = [];
                    siblings.Add(layer);
                }
            }
        }

        public void Draw()
        {
            using var canvas = new SKCanvas(_canvas);
            // Every surface is drawn in document coordinates, so a piece's own corner is a translation.
            canvas.Translate(-_region.Left, -_region.Top);
            DrawSiblings(_document.Layers.Where(layer => layer.ParentID is null).ToList(),
                new Target(_canvas, canvas, _region.Location));
        }

        /// <summary>A rectangle held to the piece of the canvas being rendered.</summary>
        private SKRectI Clip(SKRectI rect) => SKRectI.Intersect(rect, _region);

        private List<ImageLayer> ChildrenOf(ImageLayer folder) =>
            _children.TryGetValue(folder.ID, out var children) ? children : [];

        /// <summary>
        /// One sibling list, bottom to top. A visible base with no clipping source and no adjustment claims the
        /// siblings that immediately follow it and are clipped to it; they composite as one. A hidden source still
        /// supplies coverage, but its dependants use the ordinary live-mask route rather than forming a visual stack.
        /// </summary>
        private void DrawSiblings(List<ImageLayer> siblings, Target target)
        {
            for (var index = 0; index < siblings.Count;)
            {
                var layer = siblings[index];
                var end = index + 1;
                var hasVisibleClippedMember = false;
                // A hidden base does still supply live-mask coverage, but it is not a visible clipping-stack
                // base. Its visible dependants are drawn through their ordinary live mask instead, just as the
                // macOS renderer does after filtering the visible layer list.
                if (layer.IsVisible && layer.MaskSourceID is null && layer.Adjustment is null && !layer.IsGroup)
                {
                    // Stack discovery happens after visibility filtering upstream. Consume invisible siblings
                    // without letting one unrelated hidden layer split two adjacent visible stack members.
                    while (end < siblings.Count)
                    {
                        var candidate = siblings[end];
                        if (!candidate.IsVisible)
                        {
                            end++;
                            continue;
                        }
                        if (candidate.MaskSourceID != layer.ID) break;
                        hasVisibleClippedMember = true;
                        end++;
                    }
                }
                if (hasVisibleClippedMember) DrawStack(siblings, index, end, target);
                else if (layer.IsVisible) DrawLayer(layer, target);
                index = end;
            }
        }

        private void DrawLayer(ImageLayer layer, Target target)
        {
            if (layer.IsGroup)
            {
                DrawSiblings(ChildrenOf(layer), target);
                return;
            }
            if (layer.Effects is not null && layer.Asset is null) throw Unsupported(layer);
            if (layer.Adjustment is not null)
            {
                // Upstream only draws a clipped adjustment as a contiguous member of a visible clipping stack.
                // A detached dependency has no layer pixels of its own to show through its source, so it is skipped.
                if (layer.MaskSourceID is null) DrawAdjustment(layer, target);
                return;
            }

            var opacity = LayerOpacity.Effective(layer, _byID);
            if (opacity <= 0) return;
            var content = Content(layer, out var bounds);
            if (content is null) return;
            using (content)
            {
                SKBitmap? clipped = null;
                try
                {
                    if (layer.MaskSourceID is { } source)
                    {
                        clipped = RestrictToLiveSource(content, bounds, source);
                        if (clipped is null) return;
                    }
                    Composite(target, clipped ?? content, bounds, layer.BlendMode, opacity);
                }
                finally
                {
                    clipped?.Dispose();
                }
            }
        }

        /// <summary>
        /// An adjustment changes what is already on the surface, so it reads the target back, adjusts the
        /// whole of it, mixes the result in at the layer's effective opacity, and holds it to the layer's
        /// mask. It is not a layer of its own: it affects everything composited below it, inside its folder
        /// and out, because a folder is pass-through.
        /// </summary>
        private void DrawAdjustment(ImageLayer layer, Target target, bool includeFolderMasks = true)
        {
            if (layer.Adjustment is not { } adjustment) return;
            var opacity = LayerOpacity.Effective(layer, _byID);
            if (opacity <= 0) return;
            var width = target.Bitmap.Width;
            var height = target.Bitmap.Height;
            if (width <= 0 || height <= 0) return;

            using var original = Copy(target.Bitmap);
            using var adjusted = Copy(target.Bitmap);
            AdjustmentOperators.Apply(adjustment, 1, adjusted.GetPixelSpan(), width, height, width * 4,
                target.Origin.X, target.Origin.Y);
            if (layer.BlendMode != LayerBlendMode.Normal) Reblend(original, adjusted, layer.BlendMode);

            var painted = Mix(original, adjusted, (float)opacity, null, default, Coverage.Gray);
            try
            {
                var region = TargetBounds(target);
                foreach (var (mask, transform) in Masks(layer, includeOwn: true,
                    includeFolderMasks: includeFolderMasks))
                {
                    using var coverage = MaskCoverage(mask, transform, region);
                    var next = Mix(original, painted, 1f, coverage,
                        SKRectI.Create(0, 0, width, height), Coverage.Gray);
                    painted.Dispose();
                    painted = next;
                }
                using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
                target.Canvas.DrawBitmap(painted, region, OneToOne, paint);
            }
            finally
            {
                painted.Dispose();
            }
        }

        /// <summary>
        /// Blends an adjustment's colours over the original at full coverage and puts the original's alpha
        /// back, which is what the Mac build does for an adjustment that is not Normal. Source-over of two
        /// translucent copies would thicken soft edges.
        /// </summary>
        private static void Reblend(SKBitmap original, SKBitmap adjusted, LayerBlendMode mode)
        {
            var width = original.Width;
            var height = original.Height;
            var stride = width * 4;
            var coverage = new byte[width * height];
            BrushPixels.ExtractAlpha(original.GetPixelSpan(), stride, coverage, width, width, height);
            BrushPixels.UnpremultiplyOpaque(original.GetPixelSpan(), stride, width, height);
            BrushPixels.UnpremultiplyOpaque(adjusted.GetPixelSpan(), stride, width, height);

            var composite = BlendModes.From(mode);
            if (BlendModes.Skia(composite) is { } native)
            {
                using var canvas = new SKCanvas(original);
                using var source = SKImage.FromBitmap(adjusted);
                using var paint = new SKPaint { BlendMode = native };
                canvas.DrawImage(source, SKRect.Create(0, 0, width, height), OneToOne, paint);
            }
            else
            {
                BlendModes.Composite(composite, adjusted.GetPixelSpan(), original.GetPixelSpan(),
                    original.GetPixelSpan(), width, height, stride);
            }
            BrushPixels.RestoreAlpha(original.GetPixelSpan(), stride, coverage, width, width, height);
        }

        /// <summary>
        /// A base and everything clipped to it. The base's coverage clips each member, the members blend
        /// into a surface of their own with their own modes and opacities, and the result composites once,
        /// carrying the base's mode and opacity.
        /// </summary>
        private void DrawStack(List<ImageLayer> siblings, int start, int end, Target target)
        {
            var baseLayer = siblings[start];
            if (baseLayer.Effects is not null && baseLayer.Asset is null) throw Unsupported(baseLayer);
            var opacity = LayerOpacity.Effective(baseLayer, _byID);
            if (opacity <= 0) return;

            // The coverage comes from the base's pixels whether or not the base is drawn: it is the shape
            // the layers above it sit inside. No pixels, no coverage, and nothing above shows.
            // A folder mask clips the completed stack, not every member separately. Own masks remain part of
            // each member's content and therefore still participate in its local/effect rendering.
            var baseContent = Content(baseLayer, out var baseBounds, includeFolderMasks: false);
            if (baseContent is null) return;

            // Every member is painted before the surface is made, because a layer's effects make it bigger
            // than its transform says and the surface has to hold all of them.
            var painted = new Dictionary<int, (SKBitmap? Content, SKRectI Bounds)>();
            var bounds = baseBounds;
            for (var index = start + 1; index < end; index++)
            {
                var child = siblings[index];
                if (!child.IsVisible || child.Adjustment is not null) continue;
                if (child.Effects is not null && child.Asset is null) throw Unsupported(child);
                var content = Content(child, out var childBounds, includeFolderMasks: false);
                painted[index] = (content, childBounds);
                if (content is not null) bounds = SKRectI.Union(bounds, childBounds);
            }

            using (baseContent)
            {
                using var surface = Allocate(bounds.Width, bounds.Height);
                var coverage = BaseCoverage(baseContent, baseBounds, bounds);
                using (var canvas = new SKCanvas(surface))
                {
                    canvas.Translate(-bounds.Left, -bounds.Top);
                    if (baseLayer.IsVisible)
                    {
                        using var paint = new SKPaint { BlendMode = SKBlendMode.SrcOver };
                        canvas.DrawBitmap(baseContent, baseBounds, OneToOne, paint);
                    }
                    canvas.Flush();
                    // A clipping stack carries the base's alpha. Blend its children over an opaque copy of the
                    // base, then put that original coverage back once: otherwise every opaque clipped child
                    // thickens a half-transparent base from alpha .5 to .75.
                    BrushPixels.UnpremultiplyOpaque(surface.GetPixelSpan(), surface.RowBytes, surface.Width, surface.Height);
                    var group = new Target(surface, canvas, bounds.Location);
                    for (var index = start + 1; index < end; index++)
                    {
                        var child = siblings[index];
                        if (!child.IsVisible) continue;
                        if (child.Adjustment is not null)
                        {
                            DrawAdjustment(child, group, includeFolderMasks: false);
                            continue;
                        }
                        if (!painted.TryGetValue(index, out var member) || member.Content is null) continue;
                        using (member.Content)
                        {
                            Composite(group, member.Content, member.Bounds, child.BlendMode,
                                LayerOpacity.Effective(child, _byID));
                        }
                    }
                    canvas.Flush();
                }
                BrushPixels.RestoreAlpha(surface.GetPixelSpan(), surface.RowBytes, coverage, surface.Width,
                    surface.Width, surface.Height);
                SKBitmap? folderMasked = null;
                try
                {
                    var completed = surface;
                    foreach (var (mask, transform) in Masks(baseLayer, includeOwn: false))
                    {
                        var restricted = Restrict(completed, bounds, mask, transform);
                        folderMasked?.Dispose();
                        folderMasked = restricted;
                        completed = restricted;
                    }
                    Composite(target, completed, bounds, baseLayer.BlendMode, opacity);
                }
                finally
                {
                    folderMasked?.Dispose();
                }
            }
        }

        /// <summary>
        /// The layer's own pixels with its mask and every enclosing folder mask applied, in a bitmap whose
        /// top-left corner is the layer's rotated bounds. Null when the layer holds no pixels. A layer with
        /// effects goes the other way round: its mask hides part of its pixels first and the effects are
        /// drawn around what is left, so its own mask must not be applied to the result a second time.
        /// </summary>
        private SKBitmap? Content(ImageLayer layer, out SKRectI bounds, bool includeFolderMasks = true)
        {
            bounds = Clip(Bounds(layer.Transform));
            if (layer.Asset is not { } asset || bounds.Width <= 0 || bounds.Height <= 0) return null;
            var ownMasked = OwnMaskedPixels(layer, asset);
            var source = ownMasked ?? asset.Image;
            try
            {
                if (layer.Effects is { } effects)
                {
                    // Effects operate on premultiplied pixels. An owned mask already made a premultiplied copy;
                    // an unmasked asset is kept straight for ordinary drawing, so give effects their own copy.
                    if (ownMasked is not null && EffectRasterizer.Render(ownMasked, effects) is { } maskedRaster)
                    {
                        using (maskedRaster) return Raster(layer, maskedRaster, out bounds, includeFolderMasks);
                    }
                    if (ownMasked is null)
                    {
                        using var shown = PremultipliedPixels(asset);
                        if (EffectRasterizer.Render(shown, effects) is { } raster)
                        {
                            using (raster) return Raster(layer, raster, out bounds, includeFolderMasks);
                        }
                    }
                }
                var content = Allocate(bounds.Width, bounds.Height);
                using (var canvas = new SKCanvas(content))
                {
                    canvas.Translate(-bounds.Left, -bounds.Top);
                    using var paint = new SKPaint { IsAntialias = true };
                    DrawTransformed(canvas, source, layer.Transform, SKBlendMode.SrcOver, paint);
                }
                if (includeFolderMasks)
                {
                    foreach (var (mask, transform) in Masks(layer, includeOwn: false))
                    {
                        var restricted = Restrict(content, bounds, mask, transform);
                        content.Dispose();
                        content = restricted;
                    }
                }
                return content;
            }
            finally
            {
                ownMasked?.Dispose();
            }
        }

        /// <summary>
        /// A rendered effects raster, placed by the transform the effects grew: the raster is
        /// <c>inset</c> pixels bigger on every side, so its transform is scaled up by the same proportion to
        /// land on the same centre.
        /// </summary>
        private SKBitmap? Raster(ImageLayer layer, EffectRaster raster, out SKRectI bounds, bool includeFolderMasks)
        {
            var transform = Grown(layer.Transform, raster);
            bounds = Clip(Bounds(transform));
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;
            var content = Allocate(bounds.Width, bounds.Height);
            using (var canvas = new SKCanvas(content))
            {
                canvas.Translate(-bounds.Left, -bounds.Top);
                using var paint = new SKPaint { IsAntialias = true };
                DrawTransformed(canvas, raster.Pixels, transform, SKBlendMode.SrcOver, paint);
            }
            if (includeFolderMasks)
            {
                foreach (var (mask, maskTransform) in Masks(layer, includeOwn: false))
                {
                    var restricted = Restrict(content, bounds, mask, maskTransform);
                    content.Dispose();
                    content = restricted;
                }
            }
            return content;
        }

        private static Model.LayerTransform Grown(Model.LayerTransform transform, EffectRaster raster)
        {
            var width = raster.Pixels.Width;
            var height = raster.Pixels.Height;
            var inset = -raster.OffsetX;
            var innerWidth = width - inset * 2;
            var innerHeight = height - inset * 2;
            if (innerWidth <= 0 || innerHeight <= 0) return transform;
            var grownWidth = transform.Width * width / innerWidth;
            var grownHeight = transform.Height * height / innerHeight;
            var centreX = transform.X + transform.Width / 2;
            var centreY = transform.Y + transform.Height / 2;
            return new Model.LayerTransform(centreX - grownWidth / 2, centreY - grownHeight / 2, grownWidth, grownHeight,
                transform.Rotation, transform.FlipX, transform.FlipY, transform.Sampling);
        }

        /// <summary>
        /// The layer's local pixels with its own mask applied before either travels through the layer transform.
        /// A moved mask is first resampled through document space into this local grid, so an unlinked mask stays
        /// put while its layer moves, rotates or scales.
        /// </summary>
        private static SKBitmap? OwnMaskedPixels(ImageLayer layer, ImportedImage asset)
        {
            if (layer.Mask is not { } mask || mask.EnabledImage is null) return null;
            var image = PremultipliedPixels(asset);
            using var coverage = MaskCoverage(mask, layer.MaskTransform, layer.Transform, asset.Width, asset.Height);
            ApplyCoverage(image, coverage);
            return image;
        }

        /// <summary>A layer asset copied into the premultiplied form pixel effects and coverage multiplication require.</summary>
        private static SKBitmap PremultipliedPixels(ImportedImage asset)
        {
            var image = Allocate(asset.Width, asset.Height);
            using var canvas = new SKCanvas(image);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            using var source = SKImage.FromBitmap(asset.Image);
            canvas.DrawImage(source, SKRect.Create(0, 0, asset.Width, asset.Height), OneToOne, paint);
            return image;
        }

        /// <summary>The layer's own enabled mask, then each enclosing folder's, innermost first.</summary>
        private IEnumerable<(Model.LayerMask Mask, Model.LayerTransform Transform)> Masks(ImageLayer layer, bool includeOwn,
            bool includeFolderMasks = true)
        {
            if (includeOwn && layer.Mask is { } mask && mask.EnabledImage is not null) yield return (mask, layer.MaskTransform);
            if (!includeFolderMasks) yield break;
            var parent = layer.ParentID;
            for (var depth = 0; parent is { } id && depth < 64; depth++)
            {
                if (!_byID.TryGetValue(id, out var folder)) break;
                if (folder.Mask is { } folderMask && folderMask.EnabledImage is not null)
                {
                    yield return (folderMask, folderMask.Placement ?? folder.Transform);
                }
                parent = folder.ParentID;
            }
        }

        /// <summary>
        /// The alpha a live-mask source supplies over one document-space rectangle. This deliberately does not ask
        /// whether the source is visible: a hidden layer can still be a clipping source. Its own mask and any source
        /// chain apply, but a folder's display mask belongs to the layer being drawn, not to a detached dependency.
        /// </summary>
        private SKBitmap? LiveCoverage(Guid sourceID, SKRectI region, HashSet<Guid> visiting)
        {
            if (region.Width <= 0 || region.Height <= 0 || !_byID.TryGetValue(sourceID, out var source)
                || source.IsGroup || source.Adjustment is not null || !visiting.Add(sourceID))
            {
                return null;
            }

            try
            {
                var coverage = Bitmaps.Allocate(Bitmaps.MaskInfo(region.Width, region.Height));
                coverage.Erase(SKColors.Black);
                try
                {
                    var content = Content(source, out var bounds, includeFolderMasks: false);
                    if (content is null) return coverage;
                    using (content)
                    {
                        SKBitmap? constrained = null;
                        try
                        {
                            if (source.MaskSourceID is { } upstream)
                            {
                                var upstreamCoverage = LiveCoverage(upstream, bounds, visiting);
                                if (upstreamCoverage is null) return coverage;
                                using (upstreamCoverage)
                                {
                                    constrained = Restrict(content, bounds, upstreamCoverage, bounds, luminance: true);
                                }
                            }
                            CopyAlpha(coverage, region, constrained ?? content, bounds,
                                LayerOpacity.Effective(source, _byID));
                        }
                        finally
                        {
                            constrained?.Dispose();
                        }
                    }
                    return coverage;
                }
                catch
                {
                    coverage.Dispose();
                    throw;
                }
            }
            finally
            {
                visiting.Remove(sourceID);
            }
        }

        /// <summary>Holds <paramref name="content"/> to the alpha of its source layer, in the content's own rectangle.</summary>
        private SKBitmap? RestrictToLiveSource(SKBitmap content, SKRectI bounds, Guid source)
        {
            using var coverage = LiveCoverage(source, bounds, []);
            return coverage is null ? null : Restrict(content, bounds, coverage, bounds, luminance: true);
        }

        /// <summary>Copies one source's alpha (and its effective opacity) into an empty grayscale coverage surface.</summary>
        private static void CopyAlpha(SKBitmap target, SKRectI targetBounds, SKBitmap source, SKRectI sourceBounds,
            double opacity)
        {
            var overlap = SKRectI.Intersect(targetBounds, sourceBounds);
            if (overlap.Width <= 0 || overlap.Height <= 0) return;
            var amount = (float)Math.Clamp(opacity, 0, 1);
            var into = target.GetPixelSpan();
            var from = source.GetPixelSpan();
            for (var y = overlap.Top; y < overlap.Bottom; y++)
            {
                var targetRow = (y - targetBounds.Top) * target.RowBytes;
                var sourceRow = (y - sourceBounds.Top) * source.RowBytes;
                for (var x = overlap.Left; x < overlap.Right; x++)
                {
                    var alpha = from[sourceRow + (x - sourceBounds.Left) * 4 + 3] * amount;
                    into[targetRow + x - targetBounds.Left] = (byte)MathF.Round(alpha, MidpointRounding.AwayFromZero);
                }
            }
        }

        /// <summary>
        /// A mask resampled to a layer or document grid. The transform is always composed through document space, so
        /// an unlinked mask stays where it was placed while the layer it covers moves, rotates, or scales.
        /// </summary>
        private static SKBitmap MaskCoverage(Model.LayerMask mask, Model.LayerTransform placement,
            Model.LayerTransform grid, int width, int height)
        {
            var coverage = Bitmaps.Allocate(Bitmaps.MaskInfo(width, height));
            var background = Model.LayerMask.Background(mask.Asset.Thumbnail);
            coverage.Erase(new SKColor(background, background, background));
            var source = mask.Asset.Image;
            // A new all-reveal/all-hide mask is deliberately one pixel. It covers its complete matching grid
            // without filtering against an edge beyond that pixel. A separately placed one-pixel gray mask still
            // has a real placement, though, so it must take the ordinary document-space sampling path below.
            if (source.Width == 1 && source.Height == 1 && placement.SamePlacement(grid))
            {
                var value = source.GetPixelSpan()[0];
                coverage.Erase(new SKColor(value, value, value));
                return coverage;
            }

            var maskToDocument = BrushEdits.PixelToDocument(placement, source.Width, source.Height);
            if (!BrushEdits.PixelToDocument(grid, width, height).TryInvert(out var documentToGrid)) return coverage;
            using var canvas = new SKCanvas(coverage);
            canvas.Concat(SKMatrix.Concat(documentToGrid, maskToDocument));
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = true };
            var sampling = placement.SamePlacement(grid)
                ? Sampling(placement, source.Width, source.Height)
                : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
            canvas.DrawBitmap(source, SKRect.Create(0, 0, source.Width, source.Height), sampling, paint);
            canvas.Flush();
            return coverage;
        }

        /// <summary>A mask placed over a document-space rectangle, for folder and adjustment masks.</summary>
        private static SKBitmap MaskCoverage(Model.LayerMask mask, Model.LayerTransform placement, SKRectI region) =>
            MaskCoverage(mask, placement,
                new Model.LayerTransform(region.Left, region.Top, region.Width, region.Height), region.Width, region.Height);

        /// <summary>Multiplies every premultiplied channel by one matching Gray8 coverage bitmap.</summary>
        private static void ApplyCoverage(SKBitmap image, SKBitmap coverage)
        {
            var pixels = image.GetPixelSpan();
            var mask = coverage.GetPixelSpan();
            for (var y = 0; y < image.Height; y++)
            {
                var pixelRow = y * image.RowBytes;
                var maskRow = y * coverage.RowBytes;
                for (var x = 0; x < image.Width; x++)
                {
                    var amount = mask[maskRow + x];
                    var at = pixelRow + x * 4;
                    for (var channel = 0; channel < 4; channel++)
                    {
                        pixels[at + channel] = (byte)((pixels[at + channel] * amount + 127) / 255);
                    }
                }
            }
        }

        /// <summary>The rectangle one rendering target covers in document coordinates.</summary>
        private static SKRectI TargetBounds(Target target) =>
            SKRectI.Create(target.Origin.X, target.Origin.Y, target.Bitmap.Width, target.Bitmap.Height);

        /// <summary>The base alpha fitted into a clipping stack surface, before children draw over opaque color.</summary>
        private static byte[] BaseCoverage(SKBitmap content, SKRectI contentBounds, SKRectI stackBounds)
        {
            var coverage = new byte[stackBounds.Width * stackBounds.Height];
            var overlap = SKRectI.Intersect(contentBounds, stackBounds);
            if (overlap.Width <= 0 || overlap.Height <= 0) return coverage;
            var pixels = content.GetPixelSpan();
            for (var y = overlap.Top; y < overlap.Bottom; y++)
            {
                var sourceRow = (y - contentBounds.Top) * content.RowBytes;
                var targetRow = (y - stackBounds.Top) * stackBounds.Width;
                for (var x = overlap.Left; x < overlap.Right; x++)
                {
                    coverage[targetRow + x - stackBounds.Left] = pixels[sourceRow + (x - contentBounds.Left) * 4 + 3];
                }
            }
            return coverage;
        }

        /// <summary>Holds content to a placed Gray8 mask without treating its document bounds as axis-aligned pixels.</summary>
        private static SKBitmap Restrict(SKBitmap content, SKRectI bounds, Model.LayerMask mask,
            Model.LayerTransform placement)
        {
            using var coverage = MaskCoverage(mask, placement, bounds);
            return Restrict(content, bounds, coverage, bounds, luminance: true);
        }

        /// <summary>
        /// A copy of a rendered layer held to one clip: nothing outside the clip's rectangle, and nothing
        /// the clip's image does not cover inside it. It has to be a fresh bitmap — a canvas clip bounds
        /// only what is drawn after it, and a blend mode bounds only the rectangle it is drawn into, so
        /// neither can take away pixels that are already there.
        /// </summary>
        private static SKBitmap Restrict(SKBitmap content, SKRectI bounds, SKBitmap clip, SKRectI clipBounds, bool luminance)
        {
            using var empty = Allocate(bounds.Width, bounds.Height);
            var local = Shift(clipBounds, bounds.Location);
            return Mix(empty, content, 1f, clip, local, luminance ? Coverage.Gray : Coverage.Alpha);
        }

        /// <summary>
        /// Per channel, <c>under + (over − under) · strength · coverage</c> on premultiplied bytes. This is
        /// the one shape a mask, a clip and an adjustment's opacity all take: a clip is this with nothing
        /// under it, and an adjustment is this between the surface and its adjusted self.
        /// <para>
        /// A gray clip is a mask, and a mask is stretched to the rectangle it is placed in — that is what
        /// lets a uniform 1×1 mask cover a whole layer. A clip's own alpha is the base's coverage, which is
        /// already the same size as the rectangle, so it is read straight across.
        /// </para>
        /// </summary>
        private static SKBitmap Mix(SKBitmap under, SKBitmap over, float strength, SKBitmap? clip,
            SKRectI clipInBitmaps, Coverage kind)
        {
            var width = under.Width;
            var height = under.Height;
            var result = Allocate(width, height);
            var underPixels = under.GetPixelSpan();
            var overPixels = over.GetPixelSpan();
            var outPixels = result.GetPixelSpan();
            var clipPixels = clip is null ? Span<byte>.Empty : clip.GetPixelSpan();
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var coverage = strength;
                    if (clip is not null)
                    {
                        var cx = x - clipInBitmaps.Left;
                        var cy = y - clipInBitmaps.Top;
                        if (cx < 0 || cy < 0 || cx >= clipInBitmaps.Width || cy >= clipInBitmaps.Height)
                        {
                            coverage = 0f;
                        }
                        else if (kind == Coverage.Gray)
                        {
                            var sx = Math.Clamp((cx * 2 + 1) * clip.Width / (clipInBitmaps.Width * 2), 0, clip.Width - 1);
                            var sy = Math.Clamp((cy * 2 + 1) * clip.Height / (clipInBitmaps.Height * 2), 0, clip.Height - 1);
                            coverage *= clipPixels[sy * clip.Width + sx] / 255f;
                        }
                        else
                        {
                            coverage *= clipPixels[(cy * clip.Width + cx) * 4 + 3] / 255f;
                        }
                    }
                    var at = (y * width + x) * 4;
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var value = underPixels[at + channel]
                            + (overPixels[at + channel] - underPixels[at + channel]) * coverage;
                        outPixels[at + channel] = (byte)MathF.Round(value, MidpointRounding.AwayFromZero);
                    }
                }
            }
            return result;
        }

        /// <summary>A rectangle moved into a bitmap whose top-left corner is <paramref name="origin"/>.</summary>
        private static SKRectI Shift(SKRectI rect, SKPointI origin) =>
            SKRectI.Create(rect.Left - origin.X, rect.Top - origin.Y, rect.Width, rect.Height);

        private static SKBitmap Copy(SKBitmap source)
        {
            var copy = Allocate(source.Width, source.Height);
            using var canvas = new SKCanvas(copy);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(source, SKRectI.Create(0, 0, source.Width, source.Height), OneToOne, paint);
            return copy;
        }

        /// <summary>Whether a clip image's coverage is its gray or its alpha.</summary>
        private enum Coverage
        {
            Gray,
            Alpha,
        }

        /// <summary>Places a rendered layer on the target: Skia draws the sixteen modes it has, the rest are done by hand.</summary>
        private static void Composite(Target target, SKBitmap content, SKRectI bounds, LayerBlendMode mode, double opacity)
        {
            if (opacity < 1) ScaleChannels(content, opacity);
            var composite = BlendModes.From(mode);
            if (BlendModes.Skia(composite) is { } native)
            {
                using var paint = new SKPaint { BlendMode = native };
                target.Canvas.DrawBitmap(content, bounds, OneToOne, paint);
                return;
            }
            var canvasBounds = SKRectI.Create(target.Origin.X, target.Origin.Y, target.Bitmap.Width, target.Bitmap.Height);
            var overlap = SKRectI.Intersect(bounds, canvasBounds);
            if (overlap.Width <= 0 || overlap.Height <= 0) return;
            var from = Shift(overlap, bounds.Location);
            var into = Shift(overlap, target.Origin);
            var row = content.Width * 4;
            var stride = target.Bitmap.Width * 4;
            var source = (from.Top * content.Width + from.Left) * 4;
            var destination = (into.Top * target.Bitmap.Width + into.Left) * 4;
            BlendModes.Composite(composite,
                content.GetPixelSpan()[source..], row,
                target.Bitmap.GetPixelSpan()[destination..], stride,
                target.Bitmap.GetPixelSpan()[destination..], stride,
                overlap.Width, overlap.Height);
        }

        /// <summary>
        /// Scales every channel of a premultiplied bitmap, which is how opacity reaches it: scaling alpha
        /// and the colours it was premultiplied into together leaves the colour untouched.
        /// </summary>
        private static void ScaleChannels(SKBitmap bitmap, double opacity)
        {
            var scale = (float)opacity;
            var pixels = bitmap.GetPixelSpan();
            for (var index = 0; index < pixels.Length; index++)
            {
                pixels[index] = (byte)MathF.Round(pixels[index] * scale, MidpointRounding.AwayFromZero);
            }
        }

        /// <summary>
        /// The rounded-out box a rotated rectangle covers, plus <paramref name="padding"/> pixels for the
        /// edges. A mask is placed on the rectangle itself, so it is asked for with no padding.
        /// </summary>
        private static SKRectI Bounds(Model.LayerTransform transform, int padding = 1)
        {
            var radians = transform.Rotation * Math.PI / 180;
            var cos = MathF.Abs((float)Math.Cos(radians));
            var sin = MathF.Abs((float)Math.Sin(radians));
            var width = (float)transform.Width * cos + (float)transform.Height * sin;
            var height = (float)transform.Width * sin + (float)transform.Height * cos;
            var centreX = transform.X + transform.Width / 2;
            var centreY = transform.Y + transform.Height / 2;
            var left = (float)Math.Floor(centreX - width / 2) - padding;
            var top = (float)Math.Floor(centreY - height / 2) - padding;
            var right = (float)Math.Ceiling(centreX + width / 2) + padding;
            var bottom = (float)Math.Ceiling(centreY + height / 2) + padding;
            return SKRectI.Create((int)left, (int)top, (int)(right - left), (int)(bottom - top));
        }

        private readonly record struct Target(SKBitmap Bitmap, SKCanvas Canvas, SKPointI Origin);
    }
}
