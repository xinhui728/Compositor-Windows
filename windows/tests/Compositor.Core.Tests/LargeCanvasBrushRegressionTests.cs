using Compositor.Core.Document;
using Compositor.Core.Model;
using Compositor.Core.Pixels;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// Pixel fixtures for the large-canvas brush path.  These intentionally avoid wall-clock assertions: an
/// implementation may choose a different tile size, but the pieces it makes must agree with the same native
/// raster blurred as one whole image.
/// </summary>
public sealed class LargeCanvasBrushRegressionTests
{
    [Fact]
    public void BlurAcrossATileBoundaryMatchesAnIndependentWholeNativeGaussian()
    {
        const int width = 768;
        const int height = 384;
        const double radius = 4;
        var pixels = HighFrequencyBands(width, height);
        using var expected = Bitmaps.Premultiplied(pixels);
        GaussianBlur.Clamped(expected.GetPixelSpan(), width, height, 4, expected.RowBytes, radius);

        using var document = new CanvasDocument(Guid.NewGuid(), width, height);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(pixels, "Bands"),
            new LayerTransform(0, 0, width, height), "Bands");
        document.Layers.Add(layer);

        // The path crosses x=256, the first practical tile seam.  The exact tile width is deliberately not
        // part of this fixture: a future implementation can choose another width without weakening the
        // requirement that a seam be invisible.
        var settings = new BrushSettings(Diameter: 44, Hardness: 1, Opacity: 1, Mode: BrushMode.Blur,
            BlurRadius: radius);
        NativeBrushSamplingDiagnostics.Reset();
        Assert.True(BrushEdits.Paint(document, layer.ID,
            [new SKPoint(240.5f, 192.5f), new SKPoint(272.5f, 192.5f)], settings));

        // These points are well inside the solid tip, so opacity is exactly one and the expected result is
        // simply the independently blurred native source.  Reading expected as premultiplied is safe here:
        // the fixture is opaque, therefore straight and premultiplied RGB agree.
        var greatestDifference = 0;
        for (var y = 180; y <= 204; y += 4)
        {
            for (var x = 244; x <= 268; x += 2)
            {
                var actual = layer.Asset!.Image.GetPixel(x, y);
                var oracle = expected.GetPixel(x, y);
                greatestDifference = Math.Max(greatestDifference, Difference(actual, oracle));
            }
        }

        Assert.InRange(greatestDifference, 0, 1);
        var stats = NativeBrushSamplingDiagnostics.Last;
        Assert.Equal(NativeBrushSampleLimits.BlurTileSize, stats.TileSize);
        // The path crosses two native 256px columns but stays in one row. Thousands of covered pixels must
        // therefore request only two cached soft samples, each blurred once.
        Assert.Equal(2, stats.TilesRequested);
        Assert.Equal(2, stats.TilesBuilt);
        Assert.Equal(stats.TilesBuilt, stats.BlurComputations);
        Assert.False(stats.UsedCoarsening);
        AssertBoundedWorkingSet(stats);
        // The brush did not reach this native-grid detail; a tile cache may not accidentally write its halo.
        Assert.Equal(new SKColor(0, 255, 32, 255), layer.Asset!.Image.GetPixel(32, 192));
    }

    [Fact]
    public void ASeverelyScaledDownLayerCoarsensEachLazyBlurTileInsteadOfAllocatingItsFullHalo()
    {
        // 50 canvas pixels become 1,600 native pixels here, clamped to a 128px native sigma.  The source
        // itself is intentionally modest; only the virtual halo is large, so this fixture detects planning
        // regressions without making CI allocate a giant layer.
        const int nativeSide = 256;
        var pixels = HighFrequencyBands(nativeSide, nativeSide);
        using var document = new CanvasDocument(Guid.NewGuid(), 8, 8);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(pixels, "Scaled bands"),
            new LayerTransform(0, 0, 8, 8), "Scaled bands");
        document.Layers.Add(layer);

        NativeBrushSamplingDiagnostics.Reset();
        Assert.True(BrushEdits.Paint(document, layer.ID, [new SKPoint(4.5f, 4.5f)],
            new BrushSettings(Diameter: 2, Hardness: 1, Opacity: 1, Mode: BrushMode.Blur, BlurRadius: 50)));

        var stats = NativeBrushSamplingDiagnostics.Last;
        Assert.True(stats.UsedCoarsening);
        Assert.True(stats.TilesBuilt > 0);
        Assert.Equal(stats.TilesBuilt, stats.BlurComputations);
        AssertBoundedWorkingSet(stats);
    }

    [Fact]
    public void MaskBlurAcrossATileBoundaryMatchesTheWholeGray8Oracle()
    {
        const int width = 768;
        const int height = 384;
        const double radius = 4;
        var maskPixels = HighFrequencyMask(width, height);
        using var expected = Bitmaps.Allocate(Bitmaps.MaskInfo(width, height));
        maskPixels.CopyTo(expected);
        GaussianBlur.Clamped(expected.GetPixelSpan(), width, height, 1, expected.RowBytes, radius);

        var layerPixels = new SKBitmap(Bitmaps.ColorInfo(width, height));
        layerPixels.Erase(SKColors.White);
        using var document = new CanvasDocument(Guid.NewGuid(), width, height);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(layerPixels, "Layer"),
            new LayerTransform(0, 0, width, height), "Layer")
        {
            Mask = LayerMask.AssetFrom(maskPixels),
        };
        document.Layers.Add(layer);

        NativeBrushSamplingDiagnostics.Reset();
        Assert.True(BrushEdits.PaintMask(document, layer.ID,
            [new SKPoint(240.5f, 192.5f), new SKPoint(272.5f, 192.5f)],
            new BrushSettings(Diameter: 44, Hardness: 1, Opacity: 1, Mode: BrushMode.Blur, BlurRadius: radius)));

        var greatestDifference = 0;
        for (var y = 180; y <= 204; y += 4)
        {
            for (var x = 244; x <= 268; x += 2)
            {
                greatestDifference = Math.Max(greatestDifference,
                    Math.Abs(MaskAt(layer.Mask!.Asset.Image, x, y) - MaskAt(expected, x, y)));
            }
        }

        Assert.InRange(greatestDifference, 0, 1);
        var stats = NativeBrushSamplingDiagnostics.Last;
        Assert.Equal(2, stats.TilesBuilt);
        Assert.Equal(stats.TilesBuilt, stats.BlurComputations);
        AssertBoundedWorkingSet(stats);
    }

    [Fact]
    public void LazyBlurKeepsNativeResultsAtATileBoundaryOnARotatedFlippedNonuniformLayer()
    {
        const int width = 768;
        const int height = 384;
        const double radius = 4;
        var pixels = HighFrequencyBands(width, height);
        using var expected = Bitmaps.Premultiplied(pixels);
        // This is the geometric mean document length of one grid pixel, calculated independently from the
        // transform below.  The native blur radius must use it even after rotation and both mirrors.
        var sigma = radius / Math.Sqrt(0.5 * 0.25);
        GaussianBlur.Clamped(expected.GetPixelSpan(), width, height, 4, expected.RowBytes, sigma);

        var transform = new LayerTransform(180, 190, 384, 96, Rotation: 30, FlipX: true, FlipY: true);
        using var document = new CanvasDocument(Guid.NewGuid(), 800, 600);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(pixels, "Transformed bands"), transform,
            "Transformed bands");
        document.Layers.Add(layer);
        var toDocument = BrushEdits.PixelToDocument(transform, width, height);
        // Native x=256 is a cache tile boundary; y=128 remains well inside its tile.
        var centre = toDocument.MapPoint(256.5f, 128.5f);

        NativeBrushSamplingDiagnostics.Reset();
        Assert.True(BrushEdits.Paint(document, layer.ID, [centre],
            new BrushSettings(Diameter: 20, Hardness: 1, Opacity: 1, Mode: BrushMode.Blur, BlurRadius: radius)));

        var greatestDifference = 0;
        for (var y = 120; y <= 136; y += 4)
        {
            for (var x = 248; x <= 264; x += 2)
            {
                greatestDifference = Math.Max(greatestDifference,
                    Difference(layer.Asset!.Image.GetPixel(x, y), expected.GetPixel(x, y)));
            }
        }

        Assert.InRange(greatestDifference, 0, 1);
        var stats = NativeBrushSamplingDiagnostics.Last;
        Assert.InRange(stats.TilesBuilt, 1, 4);
        Assert.Equal(stats.TilesBuilt, stats.BlurComputations);
        AssertBoundedWorkingSet(stats);
    }

    [Fact]
    public void ALongStrokeBuildsEachTouchedTileOnceWithoutGrowingItsCacheWithTheStroke()
    {
        const int width = 2_560;
        const int height = 384;
        var pixels = HighFrequencyBands(width, height);
        using var document = new CanvasDocument(Guid.NewGuid(), width, height);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(pixels, "Long bands"),
            new LayerTransform(0, 0, width, height), "Long bands");
        document.Layers.Add(layer);

        NativeBrushSamplingDiagnostics.Reset();
        Assert.True(BrushEdits.Paint(document, layer.ID,
            [new SKPoint(32.5f, 192.5f), new SKPoint(2_528.5f, 192.5f)],
            new BrushSettings(Diameter: 16, Hardness: 1, Opacity: 1, Mode: BrushMode.Blur, BlurRadius: 4)));

        var stats = NativeBrushSamplingDiagnostics.Last;
        Assert.Equal(10, stats.TilesRequested);
        Assert.Equal(10, stats.TilesBuilt);
        Assert.Equal(stats.TilesBuilt, stats.BlurComputations);
        AssertBoundedWorkingSet(stats);
        // The source was a one-pixel stripe; the middle of a touched tile must no longer be its unblurred value.
        Assert.NotEqual(new SKColor(20, 70, 250, 255), layer.Asset!.Image.GetPixel(128, 192));
    }

    [Fact]
    public void ALongWarpOnALargeCanvasUsesOnlyItsStrokeRoi()
    {
        // The document dimensions are intentionally much larger than the layer.  This catches a regression
        // to the old full-document Warp surface without needing a 5k-square layer or a timing assertion.
        const int documentSide = 5_000;
        const int layerSide = 512;
        var pixels = HighFrequencyBands(layerSide, layerSide);
        using var document = new CanvasDocument(Guid.NewGuid(), documentSide, documentSide);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(pixels, "Warp bands"),
            new LayerTransform(1_000, 1_000, layerSide, layerSide), "Warp bands");
        document.Layers.Add(layer);
        var before = layer.Asset;

        WarpEditsDiagnostics.Reset();
        Assert.True(WarpEdits.Warp(document, layer.ID,
            [new SKPoint(1_100, 1_256), new SKPoint(1_400, 1_256)], WarpMode.Liquify,
            new BrushSettings(Diameter: 40, Hardness: 0.6, Opacity: 1)));

        var stats = WarpEditsDiagnostics.Last;
        Assert.False(stats.UsedDocumentSurface);
        Assert.True(stats.DabCount > 0);
        Assert.InRange(stats.RetainedDabCount, 1, stats.DabCount);
        Assert.InRange(stats.WorkingPixels, 1, (long)documentSide * documentSide / 100);
        Assert.InRange(stats.WritePixels, 1, layerSide * layerSide);
        Assert.InRange(stats.PeakScratchBytes, 1, 96L * 1024 * 1024);
        Assert.NotSame(before, layer.Asset);
    }

    [Fact]
    public void AnOversizedWarpIsRejectedBeforeItCanAllocateOrReplaceTheLayer()
    {
        // This layer is only one native pixel, stretched across the document.  A diagonal max-size brush
        // would need a 25 MP document ROI, above Warp's explicit 16 MP temporary ceiling.  The assertion is
        // therefore a real large-canvas safety check without allocating a 100 MB test bitmap.
        var pixel = new SKBitmap(Bitmaps.ColorInfo(1, 1));
        pixel.Erase(SKColors.Red);
        using var document = new CanvasDocument(Guid.NewGuid(), 5_000, 5_000);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(pixel, "Large placement"),
            new LayerTransform(0, 0, 5_000, 5_000), "Large placement");
        document.Layers.Add(layer);
        var before = layer.Asset;

        Assert.False(WarpEdits.Warp(document, layer.ID,
            [new SKPoint(1_000, 1_000), new SKPoint(4_000, 4_000)], WarpMode.Liquify,
            new BrushSettings(Diameter: 2_100, Hardness: 1, Opacity: 1)));
        Assert.Same(before, layer.Asset);
    }

    [Fact]
    public void NativeBlurPlanningForAVirtualLargeRoiStaysInsideTheExplicitWorkingBudget()
    {
        // This is planning-only coverage for a 30k canvas: it proves the arithmetic and cap without making
        // a multi-gigabyte bitmap part of ordinary CI.  The lazy tile implementation has stricter per-tile
        // bounds; this remains the final fallback guard for an unusual enormous halo.
        var region = SKRectI.Create(0, 0, 30_000, 30_000);
        var fit = NativeBrushSampleLimits.Fit(region);
        var width = (int)Math.Ceiling(region.Width * fit);
        var height = (int)Math.Ceiling(region.Height * fit);

        Assert.Equal(4_000, width);
        Assert.Equal(4_000, height);
        Assert.True((long)width * height <= NativeBrushSampleLimits.MaxWorkingPixels);
    }

    [Fact]
    public void AScaledDownLayerBlurTileCoarsensBeforeItsHaloCanExceedTheTileBudget()
    {
        // A 50px canvas radius can become thousands of native pixels on a severely scaled-down layer.  This
        // exercises just the arithmetic; rendering a synthetic 30k halo would make the test itself the
        // memory regression it is intended to guard against.
        const double nativeSigma = 5_000;
        var fit = NativeBrushSampleLimits.TileFit(nativeSigma, strokeFit: 1);
        var unscaledSide = NativeBrushSampleLimits.BlurTileSize + 6 * nativeSigma + 4;
        var scaledSide = (int)Math.Ceiling(unscaledSide * fit);

        Assert.InRange(fit, 0.000001, 0.999999);
        Assert.True((long)scaledSide * scaledSide <= NativeBrushSampleLimits.MaxBlurTilePixels,
            $"{scaledSide}x{scaledSide} exceeds the lazy tile ceiling");
    }

    private static SKBitmap HighFrequencyBands(int width, int height)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // A one-pixel green/blue stripe has detail that document-sized downsampling would erase,
                // and the isolated bright left band also makes an accidental halo write easy to catch.
                var colour = x < 64
                    ? new SKColor(0, 255, 32, 255)
                    : (x & 1) == 0 ? new SKColor(20, 70, 250, 255) : new SKColor(245, 180, 15, 255);
                bitmap.SetPixel(x, y, colour);
            }
        }
        return bitmap;
    }

    private static SKBitmap HighFrequencyMask(int width, int height)
    {
        var bitmap = new SKBitmap(Bitmaps.MaskInfo(width, height));
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // A Gray8 equivalent of the colour fixture. Its white border also makes the mask's outside
                // reveal, exercising the explicit edge-background initialization rather than transparent black.
                pixels[y * bitmap.RowBytes + x] = x < 64 ? (byte)255 : (x & 1) == 0 ? (byte)12 : (byte)236;
            }
        }
        return bitmap;
    }

    private static int Difference(SKColor left, SKColor right) => Math.Max(
        Math.Max(Math.Abs(left.Red - right.Red), Math.Abs(left.Green - right.Green)),
        Math.Max(Math.Abs(left.Blue - right.Blue), Math.Abs(left.Alpha - right.Alpha)));

    private static byte MaskAt(SKBitmap bitmap, int x, int y) => bitmap.GetPixelSpan()[y * bitmap.RowBytes + x];

    private static void AssertBoundedWorkingSet(NativeBrushSamplingStats stats)
    {
        Assert.InRange(stats.PeakWorkingPixels, 1, NativeBrushSampleLimits.MaxBlurTilePixels);
        // At most eight cached RGBA tiles survive, plus one RGBA tile and its two float Gaussian planes
        // while it is being evaluated.  The implementation may use less (or Gray8 for masks), never more.
        var maximumBytes = NativeBrushSampleLimits.MaxCachedBlurTiles * NativeBrushSampleLimits.MaxBlurTilePixels * 4
            + NativeBrushSampleLimits.MaxBlurTilePixels * (4 + sizeof(float) * 2);
        Assert.InRange(stats.PeakWorkingBytes, 1, maximumBytes);
    }
}
