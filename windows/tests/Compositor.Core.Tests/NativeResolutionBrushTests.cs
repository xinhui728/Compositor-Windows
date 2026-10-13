using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// Regression fixtures for brushes whose source and destination live on a transformed layer's native
/// pixel grid. These deliberately inspect the stored layer image, rather than its document-sized render:
/// a document proxy can hide (or invent) native detail when a layer is scaled down.
/// </summary>
public class NativeResolutionBrushTests
{
    [Fact]
    public void CurrentLayerCloneKeepsNativeCheckerDetailWhenLayerIsScaledDown()
    {
        const int nativeSide = 1000;
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(nativeSide, nativeSide));
        Fill(bitmap, (x, y) => x < nativeSide / 2
            ? ((x + y) & 1) == 0 ? SKColors.White : SKColors.Black
            : SKColors.Blue);

        using var document = new CanvasDocument(Guid.NewGuid(), 100, 100);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Checker"),
            new LayerTransform(0, 0, 100, 100), "Checker");
        document.Layers.Add(layer);

        // Destination native pixels around x=750 map to the checker around x=250. A document-sized
        // current-layer sample would give every native pixel in this ten-pixel document cell the same
        // sampled value; the native source must preserve the alternating pixels.
        Assert.True(BrushEdits.Paint(document, layer.ID, [new SKPoint(75, 50)],
            new BrushSettings(Diameter: 10, Hardness: 1, Opacity: 1, Mode: BrushMode.Clone,
                CloneFrom: new SKPointI(-50, 0))));

        var first = layer.Asset!.Image.GetPixel(750, 500);
        var second = layer.Asset.Image.GetPixel(751, 500);
        Assert.Equal(SKColors.White, first);
        Assert.Equal(SKColors.Black, second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void BlurMapsCanvasRadiusToTheNativeGridOfAScaledDownLayer()
    {
        const int nativeSide = 1000;
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(nativeSide, nativeSide));
        bitmap.Erase(SKColors.White);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Black })
        {
            // Ten native pixels are one document pixel. The blur should retain the shape of this strip
            // inside its native grid, rather than painting a one-document-pixel result back in ten-pixel
            // blocks.
            canvas.DrawRect(SKRect.Create(500, 0, 10, nativeSide), paint);
        }

        using var document = new CanvasDocument(Guid.NewGuid(), 100, 100);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Fine strip"),
            new LayerTransform(0, 0, 100, 100), "Fine strip");
        document.Layers.Add(layer);

        Assert.True(BrushEdits.Paint(document, layer.ID, [new SKPoint(50, 50)],
            new BrushSettings(Diameter: 20, Hardness: 1, Opacity: 1, Mode: BrushMode.Blur,
                BlurRadius: 0.5)));

        // At native resolution the middle of the strip remains more influenced by black than its edge.
        // The old document-sized sample read the same source pixel for both x=500 and x=505.
        var edge = layer.Asset!.Image.GetPixel(500, 500).Red;
        var middle = layer.Asset.Image.GetPixel(505, 500).Red;
        Assert.True(middle < edge, $"native blur made strip edge {edge} and middle {middle}");

        // The dab did not reach this part of the native image.
        Assert.Equal(SKColors.White, layer.Asset.Image.GetPixel(100, 500));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CurrentLayerCloneMapsDocumentOffsetThroughRotationFlipsAndNonuniformScale(bool flipX, bool flipY)
    {
        const int width = 200;
        const int height = 100;
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        Fill(bitmap, (x, y) => new SKColor((byte)x, (byte)y, (byte)((x * 17 + y * 31) & 0xff), 255));

        var transform = new LayerTransform(70, 65, 160, 70, Rotation: 30, FlipX: flipX, FlipY: flipY);
        using var document = new CanvasDocument(Guid.NewGuid(), 300, 240);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Transformed"), transform,
            "Transformed");
        document.Layers.Add(layer);

        const int targetX = 130;
        const int targetY = 50;
        var cloneOffset = new SKPointI(-12, 7);
        var destination = DocumentPoint(transform, width, height, targetX, targetY);
        var expectedPoint = NativePoint(transform, width, height,
            new SKPoint(destination.X + cloneOffset.X, destination.Y + cloneOffset.Y));
        var sourceX = (int)Math.Floor(expectedPoint.X);
        var sourceY = (int)Math.Floor(expectedPoint.Y);
        Assert.InRange(sourceX, 0, width - 1);
        Assert.InRange(sourceY, 0, height - 1);
        var expected = layer.Asset!.Image.GetPixel(sourceX, sourceY);
        var before = layer.Asset.Image.GetPixel(targetX, targetY);
        Assert.NotEqual(before, expected);

        Assert.True(BrushEdits.Paint(document, layer.ID, [destination],
            new BrushSettings(Diameter: 8, Hardness: 1, Opacity: 1, Mode: BrushMode.Clone,
                CloneFrom: cloneOffset)));

        Assert.Equal(expected, layer.Asset!.Image.GetPixel(targetX, targetY));
    }

    private static void Fill(SKBitmap bitmap, Func<int, int, SKColor> color)
    {
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var at = y * bitmap.RowBytes + x * 4;
                var value = color(x, y);
                pixels[at] = value.Red;
                pixels[at + 1] = value.Green;
                pixels[at + 2] = value.Blue;
                pixels[at + 3] = value.Alpha;
            }
        }
    }

    // These two helpers intentionally repeat the affine maths instead of calling BrushEdits.PixelToDocument
    // or LayerTransform.InBox. The assertion is an independent coordinate oracle for rotation, mirrors and
    // separate horizontal/vertical scaling.
    private static SKPoint DocumentPoint(LayerTransform transform, int pixelWidth, int pixelHeight, int x, int y)
    {
        var scaleX = transform.Width / pixelWidth * (transform.FlipX ? -1 : 1);
        var scaleY = transform.Height / pixelHeight * (transform.FlipY ? -1 : 1);
        var localX = (x + 0.5 - pixelWidth / 2.0) * scaleX;
        var localY = (y + 0.5 - pixelHeight / 2.0) * scaleY;
        var cosine = Math.Cos(transform.Radians);
        var sine = Math.Sin(transform.Radians);
        return new SKPoint((float)(transform.CenterX + localX * cosine - localY * sine),
            (float)(transform.CenterY + localX * sine + localY * cosine));
    }

    private static SKPoint NativePoint(LayerTransform transform, int pixelWidth, int pixelHeight, SKPoint point)
    {
        var scaleX = transform.Width / pixelWidth * (transform.FlipX ? -1 : 1);
        var scaleY = transform.Height / pixelHeight * (transform.FlipY ? -1 : 1);
        var x = point.X - transform.CenterX;
        var y = point.Y - transform.CenterY;
        var cosine = Math.Cos(transform.Radians);
        var sine = Math.Sin(transform.Radians);
        var localX = x * cosine + y * sine;
        var localY = -x * sine + y * cosine;
        return new SKPoint((float)(localX / scaleX + pixelWidth / 2.0),
            (float)(localY / scaleY + pixelHeight / 2.0));
    }
}
