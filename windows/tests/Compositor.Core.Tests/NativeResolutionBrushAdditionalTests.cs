using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using SkiaSharp;
using LayerMask = Compositor.Core.Model.LayerMask;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// Behavioural fixtures around the native-grid Clone Stamp and Blur paths.  These use only the public
/// brush API so they also protect the ownership boundary between a layer's stored pixels and a temporary
/// document composite.
/// </summary>
public sealed class NativeResolutionBrushAdditionalTests
{
    [Fact]
    public void CloneCurrentLayerAndSampleAllLayersReadDifferentSources()
    {
        using var currentDocument = CloneFixture(out var currentLayer);
        using var allDocument = CloneFixture(out var allLayer);
        var destination = new SKPoint(25.5f, 10.5f);
        var current = new BrushSettings(Diameter: 2, Hardness: 1, Opacity: 1, Mode: BrushMode.Clone,
            CloneFrom: new SKPointI(-20, 0));
        var all = current with { CloneAllLayers = true };

        Assert.True(BrushEdits.Paint(currentDocument, currentLayer.ID, [destination], current));
        Assert.True(BrushEdits.Paint(allDocument, allLayer.ID, [destination], all));

        // The active layer has red source pixels at x=5.  An opaque green layer above it is part of the
        // document composite, so Sample All Layers must see green instead of silently using the active raster.
        Assert.Equal(SKColors.Red, currentLayer.Asset!.Image.GetPixel(25, 10));
        Assert.Equal(SKColors.Green, allLayer.Asset!.Image.GetPixel(25, 10));
    }

    [Fact]
    public void CloneTransparentSourceLeavesDestinationUnchanged()
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(40, 20));
        bitmap.Erase(SKColors.Blue);
        bitmap.SetPixel(4, 10, SKColors.Transparent);
        using var document = DocumentWith(bitmap, out var layer);

        Assert.True(BrushEdits.Paint(document, layer.ID, [new SKPoint(24.5f, 10.5f)],
            new BrushSettings(Diameter: 2, Hardness: 1, Opacity: 1, Mode: BrushMode.Clone,
                CloneFrom: new SKPointI(-20, 0))));

        // A transparent Clone source is source-over with zero source alpha, not an instruction to erase.
        Assert.Equal(SKColors.Blue, layer.Asset!.Image.GetPixel(24, 10));
    }

    [Fact]
    public void CloneUsesStraightAlphaSourceOverWhenTheSourceIsTranslucent()
    {
        var source = new SKColor(255, 0, 0, 128);
        var backdrop = new SKColor(0, 0, 255, 128);
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(40, 20));
        bitmap.Erase(backdrop);
        bitmap.SetPixel(4, 10, source);
        using var document = DocumentWith(bitmap, out var layer);

        Assert.True(BrushEdits.Paint(document, layer.ID, [new SKPoint(24.5f, 10.5f)],
            new BrushSettings(Diameter: 2, Hardness: 1, Opacity: 1, Mode: BrushMode.Clone,
                CloneFrom: new SKPointI(-20, 0))));

        // This is an independent straight-alpha source-over calculation.  The layer raster itself is
        // unpremultiplied, while a temporary Sample All Layers composite is premultiplied.
        Assert.Equal(SourceOver(backdrop, source), layer.Asset!.Image.GetPixel(24, 10));
    }

    [Fact]
    public void BlurOnAnUnlinkedPlacedMaskUsesItsOwnGridAndKeepsItsPlacement()
    {
        var layerPixels = new SKBitmap(Bitmaps.ColorInfo(60, 40));
        layerPixels.Erase(new SKColor(25, 50, 100, 200));
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(layerPixels, "Layer"),
            new LayerTransform(0, 0, 60, 40), "Layer");

        var maskPixels = new SKBitmap(Bitmaps.MaskInfo(20, 10));
        for (var y = 0; y < maskPixels.Height; y++)
        {
            for (var x = 0; x < maskPixels.Width; x++)
            {
                // The edge is overwhelmingly white, making the placed mask's outside reveal rather than
                // transparent/black.  The narrow interior stripe gives the blur a native-grid detail to mix.
                var value = x == 9 ? (byte)0 : (byte)255;
                maskPixels.SetPixel(x, y, new SKColor(value, value, value));
            }
        }
        var placement = new LayerTransform(20, 10, 40, 20, 0, false, false, LayerSampling.Nearest);
        var mask = LayerMask.AssetFrom(maskPixels);
        mask.IsLinked = false;
        mask.Placement = placement;
        layer.Mask = mask;
        var beforeLayerAsset = layer.Asset;
        var beforeLayerPixel = layer.Asset!.Image.GetPixel(30, 20);

        using var document = new CanvasDocument(Guid.NewGuid(), 80, 60);
        document.Layers.Add(layer);
        var blur = new BrushSettings(Diameter: 6, Hardness: 1, Opacity: 1, Mode: BrushMode.Blur,
            BlurRadius: 2);

        // Native mask pixel (9, 5) maps to document (39, 21).  Its white neighbours are inside the hard tip.
        Assert.True(BrushEdits.PaintMask(document, layer.ID, [new SKPoint(39, 21)], blur));

        Assert.False(layer.Mask!.IsLinked);
        Assert.Equal(placement, layer.Mask.Placement);
        Assert.Equal(placement, layer.MaskTransform);
        Assert.Equal(20, layer.Mask.Asset.Width);
        Assert.Equal(10, layer.Mask.Asset.Height);
        Assert.Same(beforeLayerAsset, layer.Asset);
        Assert.Equal(beforeLayerPixel, layer.Asset.Image.GetPixel(30, 20));

        var mixed = MaskAt(layer.Mask.Asset.Image, 8, 5);
        Assert.InRange(mixed, (byte)1, (byte)254);

        // A second dab at the placed mask's left edge has only white source pixels around it.  Its value must
        // remain white because the mask's edge background is white too; a transparent/black outside would darken it.
        Assert.True(BrushEdits.PaintMask(document, layer.ID, [new SKPoint(21, 21)], blur));
        Assert.Equal(255, MaskAt(layer.Mask.Asset.Image, 0, 5));
    }

    [Fact]
    public void ALinkedSolidMaskGrowsToTheNativeLayerGridWhenTheLayerIsScaledDown()
    {
        var pixels = new SKBitmap(Bitmaps.ColorInfo(1000, 1000));
        pixels.Erase(SKColors.Red);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(pixels, "Scaled"),
            new LayerTransform(0, 0, 100, 100), "Scaled")
        {
            Mask = LayerMask.Solid(revealing: false),
        };
        using var document = new CanvasDocument(Guid.NewGuid(), 100, 100);
        document.Layers.Add(layer);

        Assert.True(BrushEdits.GrowMask(document, layer.ID));
        Assert.Equal(1000, layer.Mask!.Asset.Width);
        Assert.Equal(1000, layer.Mask.Asset.Height);
    }

    [Fact]
    public void CloneTakesOneSnapshotPerStrokeAndRefreshesItForTheNextStroke()
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(40, 20));
        bitmap.Erase(SKColors.White);
        bitmap.SetPixel(10, 10, SKColors.Red);
        bitmap.SetPixel(20, 10, SKColors.Blue);
        using var document = DocumentWith(bitmap, out var layer);
        var settings = new BrushSettings(Diameter: 1, Hardness: 1, Opacity: 1, Mode: BrushMode.Clone,
            CloneFrom: new SKPointI(-10, 0));

        Assert.True(BrushEdits.Paint(document, layer.ID,
            [new SKPoint(20.5f, 10.5f), new SKPoint(30.5f, 10.5f)], settings));

        // The first dab writes red over native x=20.  The same stroke must still read its original blue
        // x=20 source when it reaches x=30 instead of feeding newly written pixels back into itself.
        Assert.Equal(SKColors.Red, layer.Asset!.Image.GetPixel(20, 10));
        Assert.Equal(SKColors.Blue, layer.Asset.Image.GetPixel(30, 10));

        Assert.True(BrushEdits.Paint(document, layer.ID, [new SKPoint(30.5f, 10.5f)], settings));
        // A later stroke deliberately captures a new source snapshot, so it now sees the red x=20 pixel.
        Assert.Equal(SKColors.Red, layer.Asset.Image.GetPixel(30, 10));
    }

    private static CanvasDocument CloneFixture(out ImageLayer target)
    {
        var targetPixels = new SKBitmap(Bitmaps.ColorInfo(40, 20));
        targetPixels.Erase(SKColors.Blue);
        using (var canvas = new SKCanvas(targetPixels))
        using (var paint = new SKPaint { Color = SKColors.Red })
        {
            canvas.DrawRect(SKRect.Create(0, 0, 10, 20), paint);
        }
        target = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(targetPixels, "Target"),
            new LayerTransform(0, 0, 40, 20), "Target");

        var overlayPixels = new SKBitmap(Bitmaps.ColorInfo(40, 20));
        overlayPixels.Erase(SKColors.Green);
        var overlay = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(overlayPixels, "Overlay"),
            new LayerTransform(0, 0, 40, 20), "Overlay");

        var document = new CanvasDocument(Guid.NewGuid(), 40, 20);
        document.Layers.Add(target);
        document.Layers.Add(overlay);
        return document;
    }

    private static CanvasDocument DocumentWith(SKBitmap bitmap, out ImageLayer layer)
    {
        layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Fixture"),
            new LayerTransform(0, 0, bitmap.Width, bitmap.Height), "Fixture");
        var document = new CanvasDocument(Guid.NewGuid(), bitmap.Width, bitmap.Height);
        document.Layers.Add(layer);
        return document;
    }

    private static byte MaskAt(SKBitmap bitmap, int x, int y) => bitmap.GetPixelSpan()[y * bitmap.RowBytes + x];

    /// <summary>Independent straight-alpha source-over oracle, matching the documented layer pixel format.</summary>
    private static SKColor SourceOver(SKColor backdrop, SKColor source)
    {
        var sourceAlpha = source.Alpha / 255d;
        var backdropAlpha = backdrop.Alpha / 255d;
        var outputAlpha = sourceAlpha + backdropAlpha * (1 - sourceAlpha);
        if (outputAlpha <= 0) return SKColors.Transparent;

        byte Channel(byte foreground, byte background) => (byte)Math.Clamp(Math.Round(
            ((foreground / 255d * sourceAlpha + background / 255d * backdropAlpha * (1 - sourceAlpha))
             / outputAlpha) * 255), 0, 255);

        return new SKColor(Channel(source.Red, backdrop.Red), Channel(source.Green, backdrop.Green),
            Channel(source.Blue, backdrop.Blue), (byte)Math.Round(outputAlpha * 255));
    }
}
