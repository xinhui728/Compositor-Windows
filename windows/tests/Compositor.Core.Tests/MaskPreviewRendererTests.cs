using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>The isolated mask view is grayscale display work, never a document edit.</summary>
public class MaskPreviewRendererTests
{
    private static CanvasDocument DocumentWithMask(bool enabled = true)
    {
        var document = new CanvasDocument(Guid.NewGuid(), 8, 8);
        var pixels = new SKBitmap(Bitmaps.ColorInfo(8, 8));
        pixels.Erase(SKColors.Red);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(pixels, "Layer"),
            new LayerTransform(0, 0, 8, 8), "Layer");
        var mask = new SKBitmap(Bitmaps.MaskInfo(8, 8));
        mask.Erase(SKColors.Black);
        mask.SetPixel(4, 4, SKColors.White);
        layer.Mask = new LayerMask(ImportedImage.Create(mask, "Layer Mask"), isEnabled: enabled);
        document.Layers.Add(layer);
        return document;
    }

    [Fact]
    public void PreviewShowsMaskValuesInsteadOfTheCompositeAndDoesNotEditTheDocument()
    {
        using var document = DocumentWithMask();
        var layer = document.Layers.Single();
        var before = layer.Mask!.Asset.Image.GetPixel(4, 4);

        using var preview = MaskPreviewRenderer.RenderRegion(document, layer.ID, SKRectI.Create(0, 0, 8, 8));

        Assert.NotNull(preview);
        Assert.Equal(new SKColor(255, 255, 255, 255), preview!.GetPixel(4, 4));
        Assert.Equal(new SKColor(0, 0, 0, 255), preview.GetPixel(0, 0));
        Assert.Equal(before, layer.Mask.Asset.Image.GetPixel(4, 4));
        Assert.Equal(SKColors.Red, layer.Asset!.Image.GetPixel(4, 4));
    }

    [Fact]
    public void PreviewCanShowADisabledMaskWithoutEnablingIt()
    {
        using var document = DocumentWithMask(enabled: false);
        var layer = document.Layers.Single();

        using var preview = MaskPreviewRenderer.RenderRegion(document, layer.ID, SKRectI.Create(0, 0, 8, 8));

        Assert.NotNull(preview);
        Assert.Equal(new SKColor(255, 255, 255, 255), preview!.GetPixel(4, 4));
        Assert.False(layer.Mask!.IsEnabled);
    }

    [Fact]
    public void PreviewRefusesALayerWithoutAMask()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 8, 8);
        var layer = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 8, 8), "Layer");
        document.Layers.Add(layer);

        Assert.Null(MaskPreviewRenderer.RenderRegion(document, layer.ID, SKRectI.Create(0, 0, 8, 8)));
    }
}
