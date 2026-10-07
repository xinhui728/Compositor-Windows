using Compositor.Core.Model;
using SkiaSharp;
using LayerSampling = Compositor.Core.Format.LayerSampling;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Rendering;

/// <summary>
/// Renders one layer mask alone for an editor view. It deliberately takes no document ownership and never changes
/// the document, history, mask pixels, or serialization state.
/// </summary>
public static class MaskPreviewRenderer
{
    /// <summary>
    /// The requested document region as an opaque grayscale view of a layer's mask, including disabled masks. Null
    /// means the named layer does not currently have a mask to show.
    /// </summary>
    public static SKBitmap? RenderRegion(CanvasDocument document, Guid layerID, SKRectI region)
    {
        if (region.Width <= 0 || region.Height <= 0) return null;
        if (document.Layers.FirstOrDefault(layer => layer.ID == layerID) is not { Mask: { } mask } layer) return null;

        var rendered = DocumentRenderer.Allocate(region.Width, region.Height);
        // The thumbnail is the stable, cheap edge sample used by the upstream canvas too. It makes an isolated
        // selection-derived mask keep its reveal/hide meaning beyond its own transformed pixels.
        var background = LayerMask.Background(mask.Asset.Thumbnail);
        rendered.Erase(new SKColor(background, background, background));
        using var canvas = new SKCanvas(rendered);
        canvas.Translate(-region.Left, -region.Top);
        Draw(canvas, mask.Asset.Image, layer.MaskTransform);
        return rendered;
    }

    /// <summary>Draws a mask in the same document placement a normal layer renderer uses, but as opaque grayscale.</summary>
    private static void Draw(SKCanvas canvas, SKBitmap image, LayerTransform transform)
    {
        canvas.Save();
        canvas.Translate((float)(transform.X + transform.Width / 2), (float)(transform.Y + transform.Height / 2));
        canvas.RotateDegrees((float)transform.Rotation);
        canvas.Scale(transform.FlipX ? -1 : 1, transform.FlipY ? -1 : 1);
        canvas.Translate((float)(-transform.Width / 2), (float)(-transform.Height / 2));
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = true };
        using var source = SKImage.FromBitmap(image);
        canvas.DrawImage(source, SKRect.Create(0, 0, (float)transform.Width, (float)transform.Height),
            Sampling(transform, image.Width, image.Height), paint);
        canvas.Restore();
    }

    private static SKSamplingOptions Sampling(LayerTransform transform, int width, int height)
    {
        if (transform.Rotation == 0 && transform.Width == width && transform.Height == height)
        {
            return new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        }
        if (transform.Width <= width && transform.Height <= height)
        {
            return new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        }
        return transform.Sampling switch
        {
            LayerSampling.Nearest => new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
            LayerSampling.Smooth => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
            _ => new SKSamplingOptions(SKCubicResampler.Mitchell),
        };
    }
}
