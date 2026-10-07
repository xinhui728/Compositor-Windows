using SkiaSharp;

namespace Compositor.Core.Model;

/// <summary>
/// Immutable, normalized layer-local coverage. Regular grayscale images use white for reveal, black for
/// hide, and intermediate gray for soft coverage.
/// </summary>
public sealed class LayerMask : IDisposable
{
    public LayerMask(ImportedImage asset, bool isEnabled = true, LayerTransform? placement = null, bool isLinked = true)
    {
        Asset = asset;
        IsEnabled = isEnabled;
        Placement = placement;
        IsLinked = isLinked;
    }

    public ImportedImage Asset { get; private set; }
    public bool IsEnabled { get; set; }

    /// <summary>Where the mask sits once it has been moved apart from its layer.</summary>
    public LayerTransform? Placement { get; set; }

    /// <summary>Linked, layer and mask move together; unlinked, each transforms on its own.</summary>
    public bool IsLinked { get; set; }

    public SKBitmap? EnabledImage => IsEnabled ? Asset.Image : null;

    public static bool IsValid(SKBitmap image) => Bitmaps.IsValidMask(image);

    public static LayerMask? Solid(bool revealing)
    {
        var pixels = Bitmaps.SolidMask(revealing);
        return new LayerMask(new ImportedImage(pixels, pixels.Copy(SKColorType.Gray8), "Layer Mask"));
    }

    public static LayerMask AssetFrom(SKBitmap image)
    {
        if (!IsValid(image)) throw new IO.ProjectException(IO.ProjectError.Invalid);
        var (width, height) = ImportedImage.ThumbnailSize(image.Width, image.Height);
        return new LayerMask(new ImportedImage(image, Bitmaps.DrawAsMask(image, width, height), "Layer Mask"));
    }

    /// <summary>
    /// The tone a mask has beyond its own pixels once it is placed on the document. A mask made from a selection
    /// can be smaller than the canvas, so this keeps a reveal mask revealing and a hide mask hiding around its edge.
    /// </summary>
    public static byte Background(SKBitmap image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.ColorType != SKColorType.Gray8) return 255;
        var pixels = image.GetPixelSpan();
        long total = 0;
        var count = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (y is not 0 && y != image.Height - 1 && x is not 0 && x != image.Width - 1) continue;
                total += pixels[y * image.RowBytes + x];
                count++;
            }
        }
        return count > 0 && total * 2 < (long)count * 255 ? (byte)0 : (byte)255;
    }

    /// <summary>The same mask with new pixels, still enabled or not, linked or not, and where it sits.</summary>
    public LayerMask Replacing(ImportedImage asset) => new(asset, IsEnabled, Placement, IsLinked);

    /// <summary>
    /// Where the mask sits once its layer moves: carried along when linked, left where it was when
    /// unlinked. A uniform mask looks the same wherever it sits, so it stays attached (nil).
    /// </summary>
    public LayerTransform? PlacementMoving(LayerTransform from, LayerTransform to)
    {
        if (Asset.Width <= 1 && Asset.Height <= 1) return null;
        var moved = IsLinked ? Placement : Placement ?? from;
        return moved is { } placement && placement.SamePlacement(to) ? null : moved;
    }

    public void Dispose() => Asset.Dispose();
}
