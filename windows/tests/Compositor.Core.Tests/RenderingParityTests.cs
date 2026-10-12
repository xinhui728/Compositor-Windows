using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;
using LayerMask = Compositor.Core.Model.LayerMask;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// Small renderer fixtures derived from the macOS 1.4 GPU/export parity cases.  Each uses opaque, nearest-size
/// source images where possible so a failed assertion identifies compositing rather than resampling or alpha math.
/// </summary>
public sealed class RenderingParityTests
{
    private static CanvasDocument Document(int width, int height) => new(Guid.NewGuid(), width, height);

    private static ImageLayer Solid(SKColor color, int x, int y, int width, int height,
        LayerBlendMode blend = LayerBlendMode.Normal, double opacity = 1, bool visible = true)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(color);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Fixture"),
            new LayerTransform(x, y, width, height), "Fixture")
        {
            BlendMode = blend,
            Opacity = opacity,
            IsVisible = visible,
        };
    }

    private static LayerMask Mask(int width, int height, Func<int, int, byte> coverage)
    {
        var bitmap = new SKBitmap(Bitmaps.MaskInfo(width, height));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = coverage(x, y);
                bitmap.SetPixel(x, y, new SKColor(value, value, value));
            }
        }
        return LayerMask.AssetFrom(bitmap);
    }

    private static void Close(SKColor actual, int red, int green, int blue, int alpha = 255, int tolerance = 0)
    {
        Assert.InRange(actual.Red, red - tolerance, red + tolerance);
        Assert.InRange(actual.Green, green - tolerance, green + tolerance);
        Assert.InRange(actual.Blue, blue - tolerance, blue + tolerance);
        Assert.InRange(actual.Alpha, alpha - tolerance, alpha + tolerance);
    }

    private static void Close(SKColor actual, SKColor expected, int tolerance = 0) =>
        Close(actual, expected.Red, expected.Green, expected.Blue, expected.Alpha, tolerance);

    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>The W3C/Photoshop source-over blend equation, kept here as an independent pixel oracle.</summary>
    private static SKColor Blended(SKColor backdrop, SKColor source, double opacity, Func<double, double, double> blend)
    {
        var backdropAlpha = backdrop.Alpha / 255d;
        var sourceAlpha = source.Alpha / 255d * opacity;
        var outputAlpha = sourceAlpha + backdropAlpha - sourceAlpha * backdropAlpha;
        if (outputAlpha <= 0) return SKColors.Transparent;

        double Channel(byte behind, byte over)
        {
            var b = behind / 255d;
            var s = over / 255d;
            var premultiplied = (1 - sourceAlpha) * b * backdropAlpha
                + (1 - backdropAlpha) * s * sourceAlpha
                + sourceAlpha * backdropAlpha * blend(b, s);
            return premultiplied / outputAlpha;
        }

        return new SKColor(Byte(Channel(backdrop.Red, source.Red)), Byte(Channel(backdrop.Green, source.Green)),
            Byte(Channel(backdrop.Blue, source.Blue)), Byte(outputAlpha));
    }

    private static double SoftLight(double backdrop, double source)
    {
        if (source <= .5) return backdrop - (1 - 2 * source) * backdrop * (1 - backdrop);
        var d = backdrop <= .25 ? ((16 * backdrop - 12) * backdrop + 4) * backdrop : Math.Sqrt(backdrop);
        return backdrop + (2 * source - 1) * (d - backdrop);
    }

    private static void AssertPixelsEqual(SKBitmap expected, SKBitmap actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
            {
                Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void SoftLightMatchesTheUpstreamPhotoshopGoldenPixel()
    {
        using var document = Document(10, 10);
        document.Layers.Add(Solid(new SKColor(128, 128, 128), 0, 0, 10, 10));
        document.Layers.Add(Solid(new SKColor(230, 230, 230), 0, 0, 10, 10, LayerBlendMode.SoftLight));

        using var rendered = DocumentRenderer.Render(document);

        // Photoshop: 2 * .5 * (1 - .9) + sqrt(.5) * (2 * .9 - 1) = .666, or about 170/255.
        Close(rendered.GetPixel(5, 5), 170, 170, 170, tolerance: 2);
    }

    [Fact]
    public void SoftLightMatchesTheAnalyticalColoredAndDarkSourceCases()
    {
        var backdrop = new SKColor(64, 128, 204);
        var source = new SKColor(26, 230, 102);
        using var document = Document(1, 1);
        document.Layers.Add(Solid(backdrop, 0, 0, 1, 1));
        document.Layers.Add(Solid(source, 0, 0, 1, 1, LayerBlendMode.SoftLight));

        using var rendered = DocumentRenderer.Render(document);

        Close(rendered.GetPixel(0, 0), Blended(backdrop, source, 1, SoftLight), tolerance: 2);
    }

    [Fact]
    public void SoftLightUsesSourceOpacityAndPremultipliedAlpha()
    {
        var backdrop = new SKColor(80, 150, 220, 128);
        var source = new SKColor(230, 40, 120, 128);
        const double opacity = .6;
        using var document = Document(1, 1);
        document.Layers.Add(Solid(backdrop, 0, 0, 1, 1));
        document.Layers.Add(Solid(source, 0, 0, 1, 1, LayerBlendMode.SoftLight, opacity));

        using var rendered = DocumentRenderer.Render(document);

        Close(rendered.GetPixel(0, 0), Blended(backdrop, source, opacity, SoftLight), tolerance: 2);
    }

    [Fact]
    public void ATransparentSoftLightSourceLeavesItsBackdropAlone()
    {
        var backdrop = new SKColor(80, 150, 220, 192);
        using var document = Document(1, 1);
        document.Layers.Add(Solid(backdrop, 0, 0, 1, 1));
        document.Layers.Add(Solid(new SKColor(230, 40, 120, 0), 0, 0, 1, 1, LayerBlendMode.SoftLight));

        using var rendered = DocumentRenderer.Render(document);

        Close(rendered.GetPixel(0, 0), backdrop, tolerance: 1);
    }

    [Fact]
    public void StandardBlendControlsMatchTheirAnalyticalOracles()
    {
        var backdrop = new SKColor(51, 128, 204);
        var source = new SKColor(230, 77, 26);
        var controls = new (LayerBlendMode Mode, Func<double, double, double> Formula)[]
        {
            (LayerBlendMode.Normal, (_, s) => s),
            (LayerBlendMode.Multiply, (b, s) => b * s),
            (LayerBlendMode.Screen, (b, s) => b + s - b * s),
            (LayerBlendMode.Overlay, (b, s) => b <= .5 ? 2 * b * s : 1 - 2 * (1 - b) * (1 - s)),
        };

        foreach (var (mode, formula) in controls)
        {
            using var document = Document(1, 1);
            document.Layers.Add(Solid(backdrop, 0, 0, 1, 1));
            document.Layers.Add(Solid(source, 0, 0, 1, 1, mode));
            using var rendered = DocumentRenderer.Render(document);
            Close(rendered.GetPixel(0, 0), Blended(backdrop, source, 1, formula), tolerance: 1);
        }
    }

    [Fact]
    public void LinearDodgeClippingStackUsesTheBasesBlendMode()
    {
        using var document = Document(4, 4);
        document.Layers.Add(Solid(new SKColor(102, 51, 26), 0, 0, 4, 4));
        var baseLayer = Solid(new SKColor(77, 77, 77), 0, 0, 4, 4, LayerBlendMode.LinearDodgeAdd);
        var clipped = Solid(new SKColor(51, 13, 0), 0, 0, 2, 2);
        clipped.MaskSourceID = baseLayer.ID;
        document.Layers.Add(baseLayer);
        document.Layers.Add(clipped);

        using var rendered = DocumentRenderer.Render(document);

        // The clipped layer covers the base at (1,1); at (3,3) only the base reaches the stack.  The stack
        // is then composited in its base's Linear Dodge mode, rather than being flattened as Normal.
        Close(rendered.GetPixel(1, 1), 153, 64, 26, tolerance: 1);
        Close(rendered.GetPixel(3, 3), 179, 128, 102, tolerance: 1);
    }

    [Fact]
    public void AHiddenUnrelatedLayerDoesNotSplitAVisibleClippingStack()
    {
        using var document = Document(4, 4);
        document.Layers.Add(Solid(new SKColor(102, 51, 26), 0, 0, 4, 4));
        var baseLayer = Solid(new SKColor(77, 77, 77), 0, 0, 4, 4, LayerBlendMode.LinearDodgeAdd);
        var hidden = Solid(SKColors.Blue, 0, 0, 4, 4, visible: false);
        var clipped = Solid(new SKColor(51, 13, 0), 0, 0, 2, 2);
        clipped.MaskSourceID = baseLayer.ID;
        document.Layers.Add(baseLayer);
        document.Layers.Add(hidden);
        document.Layers.Add(clipped);

        using var rendered = DocumentRenderer.Render(document);

        // The upstream renderer identifies stacks from the visibility-filtered layer list, so a hidden unrelated
        // sibling cannot change the base mode or coverage that the visible clipped child receives.
        Close(rendered.GetPixel(1, 1), 153, 64, 26, tolerance: 1);
        Close(rendered.GetPixel(3, 3), 179, 128, 102, tolerance: 1);
    }

    [Fact]
    public void ABaseMaskConstrainsTheEntireClippedLayer()
    {
        using var document = Document(4, 4);
        var baseLayer = Solid(SKColors.White, 0, 0, 4, 4);
        baseLayer.Mask = Mask(4, 4, (x, y) => x < 2 && y < 2 ? (byte)255 : (byte)0);
        var clipped = Solid(SKColors.Red, 0, 0, 4, 4);
        clipped.MaskSourceID = baseLayer.ID;
        document.Layers.Add(baseLayer);
        document.Layers.Add(clipped);

        using var rendered = DocumentRenderer.Render(document);

        Close(rendered.GetPixel(1, 1), 255, 0, 0);
        Close(rendered.GetPixel(3, 3), 0, 0, 0, alpha: 0);
    }

    [Fact]
    public void ClippingStackRestoresTheMaskedBasesAlphaAfterCompositingChildren()
    {
        using var document = Document(2, 2);
        var baseLayer = Solid(SKColors.White, 0, 0, 2, 2);
        baseLayer.Mask = Mask(2, 2, (_, _) => 128);
        var clipped = Solid(SKColors.Red, 0, 0, 2, 2);
        clipped.MaskSourceID = baseLayer.ID;
        document.Layers.Add(baseLayer);
        document.Layers.Add(clipped);

        using var rendered = DocumentRenderer.Render(document);

        // The child changes the stack's color, not its base coverage: 50% must remain 50%, not build to 75%.
        Close(rendered.GetPixel(1, 1), 255, 0, 0, alpha: 128, tolerance: 1);
    }

    [Fact]
    public void AFolderMaskAppliesOnceAfterAClippingStack()
    {
        using var document = Document(2, 2);
        var folder = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 2, 2), "Folder")
        {
            IsGroup = true,
            Mask = Mask(2, 2, (_, _) => 128),
        };
        var baseLayer = Solid(SKColors.White, 0, 0, 2, 2);
        baseLayer.ParentID = folder.ID;
        var clipped = Solid(SKColors.Red, 0, 0, 2, 2);
        clipped.ParentID = folder.ID;
        clipped.MaskSourceID = baseLayer.ID;
        document.Layers.Add(folder);
        document.Layers.Add(baseLayer);
        document.Layers.Add(clipped);

        using var rendered = DocumentRenderer.Render(document);

        // A folder clips the completed stack. Applying 50% coverage to both members would leave a pink blend;
        // applying it once preserves the clipped child's red colour at 50% alpha.
        Close(rendered.GetPixel(1, 1), 255, 0, 0, alpha: 128, tolerance: 1);
    }

    [Fact]
    public void AClippedLayerRetainsItsOwnMaskInsideTheBaseCoverage()
    {
        using var document = Document(4, 2);
        var baseLayer = Solid(SKColors.White, 0, 0, 4, 2);
        var clipped = Solid(SKColors.Red, 0, 0, 4, 2);
        clipped.Mask = Mask(4, 2, (x, _) => x < 2 ? (byte)255 : (byte)0);
        clipped.MaskSourceID = baseLayer.ID;
        document.Layers.Add(baseLayer);
        document.Layers.Add(clipped);

        using var rendered = DocumentRenderer.Render(document);

        Close(rendered.GetPixel(1, 1), 255, 0, 0);
        Close(rendered.GetPixel(3, 1), 255, 255, 255);
    }

    [Fact]
    public void ALinkedMaskTurnsWithTheLayerPixels()
    {
        using var document = Document(6, 6);
        var layer = Solid(SKColors.Red, 1, 2, 4, 2);
        layer.Transform = new LayerTransform(1, 2, 4, 2, 90, false, false, LayerSampling.Nearest);
        layer.Mask = Mask(4, 2, (x, _) => x < 2 ? (byte)255 : (byte)0);
        document.Layers.Add(layer);

        using var rendered = DocumentRenderer.Render(document);

        // The local left half turns into the document's top half. The old axis-aligned mask path reversed this.
        Close(rendered.GetPixel(3, 1), 255, 0, 0);
        Assert.Equal(0, rendered.GetPixel(3, 4).Alpha);
    }

    [Fact]
    public void AnUnlinkedRevealMaskUsesItsWhiteEdgeBeyondItsPlacedPixels()
    {
        using var document = Document(6, 4);
        var layer = Solid(SKColors.Red, 0, 0, 6, 4);
        var mask = Mask(2, 2, (x, y) => x == 0 && y == 0 ? (byte)0 : (byte)255);
        mask.IsLinked = false;
        mask.Placement = new LayerTransform(2, 1, 2, 2, 0, false, false, LayerSampling.Nearest);
        layer.Mask = mask;
        document.Layers.Add(layer);

        using var rendered = DocumentRenderer.Render(document);

        Close(rendered.GetPixel(0, 0), 255, 0, 0);
        Assert.Equal(0, rendered.GetPixel(2, 1).Alpha);
    }

    [Fact]
    public void AnUnlinkedMaskKeepsItsOwnRotationAndWhiteEdgeBackground()
    {
        using var document = Document(6, 6);
        var layer = Solid(SKColors.Red, 0, 0, 6, 6);
        var mask = Mask(4, 2, (x, _) => x < 2 ? (byte)0 : (byte)255);
        mask.IsLinked = false;
        mask.Placement = new LayerTransform(1, 2, 4, 2, 90, false, false, LayerSampling.Nearest);
        layer.Mask = mask;
        document.Layers.Add(layer);

        using var rendered = DocumentRenderer.Render(document);

        // The placed mask's black local left half turns into its document top half. Equal black/white edge
        // coverage resolves to the upstream reveal background outside the placed raster.
        Close(rendered.GetPixel(0, 0), 255, 0, 0);
        Assert.Equal(0, rendered.GetPixel(3, 1).Alpha);
        Close(rendered.GetPixel(3, 4), 255, 0, 0);
    }

    [Fact]
    public void APlacedOnePixelGrayMaskUsesItsOwnPlacement()
    {
        using var document = Document(6, 4);
        var layer = Solid(SKColors.Red, 0, 0, 6, 4);
        var mask = Mask(1, 1, (_, _) => 127);
        mask.IsLinked = false;
        mask.Placement = new LayerTransform(2, 1, 2, 2, 0, false, false, LayerSampling.Nearest);
        layer.Mask = mask;
        document.Layers.Add(layer);

        using var rendered = DocumentRenderer.Render(document);

        // A uniform one-pixel default mask fills a matching grid, but an explicitly placed gray pixel still has a
        // real footprint. Its black edge background must not turn the entire layer gray.
        Assert.Equal(0, rendered.GetPixel(0, 0).Alpha);
        Close(rendered.GetPixel(2, 1), 255, 0, 0, alpha: 127, tolerance: 1);
    }

    [Fact]
    public void NestedFolderMasksMultiplyTheirCoverage()
    {
        using var document = Document(2, 2);
        var outer = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 2, 2), "Outer") { IsGroup = true };
        outer.Mask = Mask(2, 2, (_, _) => 128);
        var inner = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 2, 2), "Inner")
        {
            IsGroup = true,
            ParentID = outer.ID,
        };
        inner.Mask = Mask(2, 2, (_, _) => 128);
        var child = Solid(SKColors.Red, 0, 0, 2, 2);
        child.ParentID = inner.ID;
        document.Layers.Add(outer);
        document.Layers.Add(inner);
        document.Layers.Add(child);

        using var rendered = DocumentRenderer.Render(document);

        Close(rendered.GetPixel(1, 1), 255, 0, 0, alpha: 64, tolerance: 1);
    }

    [Fact]
    public void AHiddenNoncontiguousSourceStillSuppliesItsLiveMask()
    {
        using var document = Document(4, 4);
        var source = Solid(SKColors.White, 0, 0, 4, 4, visible: false);
        source.Mask = Mask(4, 4, (x, _) => x < 2 ? (byte)255 : (byte)0);
        document.Layers.Add(source);
        document.Layers.Add(Solid(SKColors.Transparent, 0, 0, 4, 4));
        var target = Solid(SKColors.Red, 0, 0, 4, 4);
        target.MaskSourceID = source.ID;
        document.Layers.Add(target);

        using var rendered = DocumentRenderer.Render(document);

        Close(rendered.GetPixel(1, 1), 255, 0, 0);
        Assert.Equal(0, rendered.GetPixel(3, 1).Alpha);
    }

    [Fact]
    public void AHiddenLiveSourceDoesNotImpartItsOwnBlendMode()
    {
        using var document = Document(4, 2);
        document.Layers.Add(Solid(new SKColor(50, 80, 100), 0, 0, 4, 2));
        var source = Solid(SKColors.White, 0, 0, 4, 2, LayerBlendMode.Multiply, visible: false);
        source.Mask = Mask(4, 2, (x, _) => x < 2 ? (byte)255 : (byte)0);
        document.Layers.Add(source);
        document.Layers.Add(Solid(SKColors.Transparent, 0, 0, 4, 2));
        var target = Solid(SKColors.Red, 0, 0, 4, 2);
        target.MaskSourceID = source.ID;
        document.Layers.Add(target);

        using var rendered = DocumentRenderer.Render(document);

        Close(rendered.GetPixel(1, 0), 255, 0, 0);
        Close(rendered.GetPixel(3, 0), 50, 80, 100);
    }

    [Fact]
    public void ADetachedClippedAdjustmentDoesNotAffectTheCanvas()
    {
        using var document = Document(4, 2);
        document.Layers.Add(Solid(new SKColor(50, 80, 100), 0, 0, 4, 2));
        var source = Solid(SKColors.White, 0, 0, 4, 2, visible: false);
        document.Layers.Add(source);
        document.Layers.Add(Solid(SKColors.Transparent, 0, 0, 4, 2));
        var adjustment = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 4, 2), "Invert")
        {
            Adjustment = new LayerAdjustment { Kind = AdjustmentKind.Invert },
            MaskSourceID = source.ID,
        };
        document.Layers.Add(adjustment);

        using var rendered = DocumentRenderer.Render(document);

        // The macOS live renderer only runs a clipped adjustment in a contiguous visual clipping stack.
        Close(rendered.GetPixel(1, 0), 50, 80, 100);
    }

    [Fact]
    public void SeededAddNoiseMatchesAnOffOriginRenderRegionExactly()
    {
        using var document = Document(13, 11);
        document.Layers.Add(Solid(new SKColor(128, 128, 128), 0, 0, 13, 11));
        document.Layers.Add(new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 13, 11), "Add Noise")
        {
            Adjustment = new LayerAdjustment
            {
                Kind = AdjustmentKind.AddNoise,
                NoiseAmount = 50,
                NoiseSeed = 4242,
                NoiseGaussian = true,
                NoiseMonochromatic = false,
            },
        });

        using var whole = DocumentRenderer.Render(document);
        var region = SKRectI.Create(3, 2, 7, 6);
        using var piece = DocumentRenderer.RenderRegion(document, region);

        Assert.Contains(Enumerable.Range(0, whole.Width * whole.Height), index =>
        {
            var pixel = whole.GetPixel(index % whole.Width, index / whole.Width);
            return pixel.Red != 128 || pixel.Green != 128 || pixel.Blue != 128;
        });
        for (var y = 0; y < region.Height; y++)
        {
            for (var x = 0; x < region.Width; x++)
            {
                Assert.Equal(whole.GetPixel(region.Left + x, region.Top + y), piece.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void SeededAddNoiseMatchesEveryTileAcrossAScaledAndTranslatedLayer()
    {
        using var document = Document(17, 13);
        document.Layers.Add(Solid(new SKColor(128, 128, 128), 3, 2, 7, 5));
        document.Layers[^1].Transform = new LayerTransform(3, 2, 11, 9, 0, false, false, LayerSampling.Nearest);
        document.Layers.Add(new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 17, 13), "Add Noise")
        {
            Adjustment = new LayerAdjustment
            {
                Kind = AdjustmentKind.AddNoise,
                NoiseAmount = 50,
                NoiseSeed = 99,
                NoiseGaussian = false,
                NoiseMonochromatic = true,
            },
        });

        using var whole = DocumentRenderer.Render(document);
        for (var y = 0; y < document.Height; y += 3)
        {
            for (var x = 0; x < document.Width; x += 4)
            {
                var region = SKRectI.Create(x, y, Math.Min(4, document.Width - x), Math.Min(3, document.Height - y));
                using var piece = DocumentRenderer.RenderRegion(document, region);
                for (var row = 0; row < region.Height; row++)
                {
                    for (var column = 0; column < region.Width; column++)
                    {
                        Assert.Equal(whole.GetPixel(region.Left + column, region.Top + row), piece.GetPixel(column, row));
                    }
                }
            }
        }
    }

    [Fact]
    public void PlacedMasksAndLiveLinksRenderIdenticallyAfterAProjectRoundTrip()
    {
        var package = Path.Combine(Path.GetTempPath(), $"CompositorRenderingParity-{Guid.NewGuid():N}.comp");
        try
        {
            using var document = Document(6, 4);
            var source = Solid(SKColors.White, 0, 0, 6, 4, visible: false);
            source.Mask = Mask(6, 4, (x, _) => x < 3 ? (byte)255 : (byte)0);
            document.Layers.Add(source);
            document.Layers.Add(Solid(SKColors.Transparent, 0, 0, 6, 4));
            var target = Solid(SKColors.Red, 0, 0, 6, 4);
            var placed = Mask(2, 2, (x, y) => x == 0 && y == 0 ? (byte)0 : (byte)255);
            placed.IsLinked = false;
            placed.Placement = new LayerTransform(2, 1, 2, 2, 0, false, false, LayerSampling.Nearest);
            target.Mask = placed;
            target.MaskSourceID = source.ID;
            document.Layers.Add(target);
            using var before = DocumentRenderer.Render(document);

            ProjectStore.Save(ProjectSnapshot.FromDocument(document), package);
            using var loaded = ProjectStore.Load(package);
            Assert.Equal(11, loaded.Manifest.Version);
            using var reloaded = loaded.ToDocument();
            using var after = DocumentRenderer.Render(reloaded);

            AssertPixelsEqual(before, after);
        }
        finally
        {
            if (Directory.Exists(package)) Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public void APlacedMasksEdgeBackgroundSurvivesAProjectRoundTrip()
    {
        var package = Path.Combine(Path.GetTempPath(), $"CompositorPlacedMask-{Guid.NewGuid():N}.comp");
        try
        {
            using var document = Document(6, 4);
            var layer = Solid(SKColors.Red, 0, 0, 6, 4);
            // Three black edge pixels make the placed mask's background black. This must agree before and after
            // persistence: new masks and loaded masks cannot derive their edge coverage from different thumbnails.
            var placed = Mask(2, 2, (x, y) => x == 1 && y == 1 ? (byte)255 : (byte)0);
            placed.IsLinked = false;
            placed.Placement = new LayerTransform(2, 1, 2, 2, 0, false, false, LayerSampling.Nearest);
            layer.Mask = placed;
            document.Layers.Add(layer);
            using var before = DocumentRenderer.Render(document);
            Assert.Equal(0, before.GetPixel(0, 0).Alpha);

            ProjectStore.Save(ProjectSnapshot.FromDocument(document), package);
            using var loaded = ProjectStore.Load(package);
            using var reloaded = loaded.ToDocument();
            using var after = DocumentRenderer.Render(reloaded);

            AssertPixelsEqual(before, after);
            Assert.Equal(0, after.GetPixel(0, 0).Alpha);
        }
        finally
        {
            if (Directory.Exists(package)) Directory.Delete(package, recursive: true);
        }
    }
}
