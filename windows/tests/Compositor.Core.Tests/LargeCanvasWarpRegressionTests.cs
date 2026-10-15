using Compositor.Core.Document;
using Compositor.Core.Model;
using Compositor.Core.Pixels;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// Regression coverage for the sparse large-canvas Smudge and Liquify path.  These deliberately assert
/// deterministic surface and scratch limits rather than elapsed time: the test host may be busy, but a
/// 5,000px document must never make a short brush stroke allocate a 5,000px working surface.
/// </summary>
public sealed class LargeCanvasWarpRegressionTests
{
    private const int LargeSide = 5_000;
    private const int LargestBrush = 2_000;
    private const long MaxScratchBytes = 96L * 1024 * 1024;

    [Theory]
    [InlineData(WarpMode.Smudge)]
    [InlineData(WarpMode.Liquify)]
    public void LargestBrushOnAFiveThousandPixelCanvasUsesABoundedRoiAndKeepsTheSourceImmutable(WarpMode mode)
    {
        using var document = SplitLargeCanvas(out var layer);
        var before = layer.Asset!;
        var oldPixel = before.Image.GetPixel(2_600, LargeSide / 2);

        // A short stroke over the red/blue boundary is enough to exercise the largest UI brush without
        // turning this normal CI fixture into a wall-clock benchmark.  It still needs millions of working
        // pixels, which distinguishes a stroke ROI from a document-sized temporary surface.
        WarpEditsDiagnostics.Reset();
        Assert.True(WarpEdits.Warp(document, layer.ID,
            [new SKPoint(2_400, LargeSide / 2), new SKPoint(2_600, LargeSide / 2)], mode,
            new BrushSettings(Diameter: LargestBrush, Hardness: 0.6, Opacity: 1)));

        var after = layer.Asset!;
        Assert.NotSame(before, after);
        // Assets are snapshots.  The replacement is allowed to change, but the source captured at stroke
        // start must not be mutated while the carried colour / dab replay runs.
        Assert.Equal(oldPixel, before.Image.GetPixel(2_600, LargeSide / 2));
        Assert.True(HasChanged(before.Image, after.Image, 2_475, 2_750, LargeSide / 2 - 64, LargeSide / 2 + 64),
            "the large warp reported success without changing pixels near the stroke");

        var stats = WarpEditsDiagnostics.Last;
        Assert.True(stats.DabCount > 0);
        Assert.Equal(stats.DabCount, stats.RetainedDabCount);
        Assert.InRange(stats.WorkingPixels, 1, (long)LargeSide * LargeSide - 1);
        Assert.InRange(stats.WritePixels, 1, (long)LargeSide * LargeSide - 1);
        Assert.False(stats.UsedDocumentSurface);
        Assert.InRange(stats.PeakScratchBytes, 1, MaxScratchBytes);

        // The live document owns only the new asset now; release the superseded large source promptly so
        // this focused stress fixture does not hold two 5,000px rasters until the test process exits.
        before.Dispose();
    }

    [Theory]
    [InlineData(WarpMode.Smudge)]
    [InlineData(WarpMode.Liquify)]
    public void SparseWarpWritesBackThroughRotatedFlippedNonuniformPlacementAndHistoryKeepsOldPixels(WarpMode mode)
    {
        const int nativeWidth = 800;
        const int nativeHeight = 400;
        var transform = new LayerTransform(250, 200, 1_120, 360, Rotation: 30, FlipX: true, FlipY: true);
        using var document = new CanvasDocument(Guid.NewGuid(), 1_800, 1_200);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(Gradient(nativeWidth, nativeHeight), "Warp"),
            transform, "Warp");
        document.Layers.Add(layer);
        var before = layer.Asset!;
        var sourceBytes = before.Image.GetPixelSpan().ToArray();
        var toDocument = BrushEdits.PixelToDocument(transform, nativeWidth, nativeHeight);
        // Transform two adjacent native positions instead of guessing document axes.  This specifically
        // exercises rotation, both mirrors and unequal x/y scale on the sparse write-back path.
        var start = toDocument.MapPoint(300.5f, 200.5f);
        var end = toDocument.MapPoint(460.5f, 200.5f);
        var history = new DocumentHistory();

        history.Begin("Warp", document, layer.ID);
        WarpEditsDiagnostics.Reset();
        Assert.True(WarpEdits.Warp(document, layer.ID, [start, end], mode,
            new BrushSettings(Diameter: 96, Hardness: 0.65, Opacity: 0.8)));
        history.End(document, layer.ID);

        var after = layer.Asset!;
        Assert.NotSame(before, after);
        Assert.Equal(nativeWidth, after.Width);
        Assert.Equal(nativeHeight, after.Height);
        Assert.Equal(sourceBytes, before.Image.GetPixelSpan().ToArray());
        Assert.True(HasChanged(before.Image, after.Image, 260, 500, 120, 280),
            "the transformed stroke did not make it back to its native grid");

        var stats = WarpEditsDiagnostics.Last;
        Assert.InRange(stats.WorkingPixels, 1, (long)document.Width * document.Height - 1);
        Assert.False(stats.UsedDocumentSurface);
        Assert.InRange(stats.PeakScratchBytes, 1, MaxScratchBytes);
        Assert.Equal(1, history.UndoCount);

        var undone = Assert.IsType<DocumentHistory.Snapshot>(history.Undo());
        var undoImage = Assert.Single(undone.Document!.Layers).Asset!.Image;
        Assert.Equal(sourceBytes, undoImage.GetPixelSpan().ToArray());
        // The live document owns the replacement; the history snapshot borrows the earlier asset, which can
        // now be released after its immutability/undo assertion.
        before.Dispose();
    }

    private static CanvasDocument SplitLargeCanvas(out ImageLayer layer)
    {
        var image = new SKBitmap(Bitmaps.ColorInfo(LargeSide, LargeSide));
        using (var canvas = new SKCanvas(image))
        using (var red = new SKPaint { Color = new SKColor(220, 40, 40) })
        using (var blue = new SKPaint { Color = new SKColor(40, 60, 220) })
        {
            canvas.DrawRect(SKRect.Create(0, 0, LargeSide / 2, LargeSide), red);
            canvas.DrawRect(SKRect.Create(LargeSide / 2, 0, LargeSide / 2, LargeSide), blue);
        }

        var document = new CanvasDocument(Guid.NewGuid(), LargeSide, LargeSide);
        layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(image, "Large split"),
            new LayerTransform(0, 0, LargeSide, LargeSide), "Large split");
        document.Layers.Add(layer);
        return document;
    }

    private static SKBitmap Gradient(int width, int height)
    {
        var image = new SKBitmap(Bitmaps.ColorInfo(width, height));
        var pixels = image.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = y * image.RowBytes + x * 4;
                pixels[at] = (byte)(x * 255 / (width - 1));
                pixels[at + 1] = (byte)(y * 255 / (height - 1));
                pixels[at + 2] = (byte)((x * 13 + y * 7) & 255);
                pixels[at + 3] = 255;
            }
        }
        return image;
    }

    private static bool HasChanged(SKBitmap before, SKBitmap after, int left, int right, int top, int bottom)
    {
        for (var y = top; y <= bottom; y += 8)
        {
            for (var x = left; x <= right; x += 8)
            {
                if (before.GetPixel(x, y) != after.GetPixel(x, y)) return true;
            }
        }
        return false;
    }
}
