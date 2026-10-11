using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using Compositor.Core.Pixels;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>
/// Golden contracts lifted from macOS Camera Raw v1.4.5 (60d9117). These deliberately exercise the
/// transient Camera Raw curve model rather than Format.CurvesSettings, whose serialized contract must remain
/// the ordinary adjustment-layer curve.
/// </summary>
public sealed class CameraRawCurveParityTests
{
    private static (CanvasDocument Document, ImageLayer Layer) Flat(SKColor color)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(8, 8));
        bitmap.Erase(color);
        var document = new CanvasDocument(Guid.NewGuid(), 8, 8);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Curve test"),
            new LayerTransform(0, 0, 8, 8), "Curve test");
        document.Layers.Add(layer);
        return (document, layer);
    }

    private static SKColor Apply(SKColor color, CameraRawSettings settings)
    {
        var (document, layer) = Flat(color);
        using (document)
        {
            Assert.True(CameraRawEdits.Apply(document, layer.ID, settings));
            return layer.Asset!.Image.GetPixel(4, 4);
        }
    }

    [Fact]
    public void ParametricCurveIsIdentityWithNoAmountsAndKeepsItsEndpoints()
    {
        var curve = new CameraRawCurveSettings();
        Assert.Equal(0.3, curve.Parametric(0.3), 12);
        Assert.Equal(0, curve.Parametric(0), 12);
        Assert.Equal(1, curve.Parametric(1), 12);

        curve.Darks = -100;
        Assert.Equal(0, curve.Parametric(0), 12);
        Assert.Equal(1, curve.Parametric(1), 12);
    }

    [Theory]
    [InlineData(100, 0, 0, 0)]
    [InlineData(0, 100, -100, 0)]
    [InlineData(-100, 50, 100, -60)]
    public void ParametricCurveIsMonotonicAndSmoothAtTheDividers(double shadows, double darks, double lights, double highlights)
    {
        var curve = new CameraRawCurveSettings
        {
            Shadows = shadows,
            Darks = darks,
            Lights = lights,
            Highlights = highlights,
        };
        var samples = Enumerable.Range(0, 201).Select(index => curve.Parametric(index / 200.0)).ToArray();
        Assert.Equal(0, samples[0], 12);
        Assert.Equal(1, samples[^1], 12);
        Assert.All(samples.Zip(samples.Skip(1)), pair => Assert.True(pair.Second >= pair.First - 1e-9));

        static double LargestSlopeJump(CameraRawCurveSettings current, int steps)
        {
            var values = Enumerable.Range(0, steps + 1).Select(index => current.Parametric(index / (double)steps)).ToArray();
            var slopes = values.Zip(values.Skip(1)).Select(pair => (pair.Second - pair.First) * steps).ToArray();
            return slopes.Zip(slopes.Skip(1)).Select(pair => Math.Abs(pair.Second - pair.First)).DefaultIfEmpty().Max();
        }

        Assert.True(LargestSlopeJump(curve, 400) < LargestSlopeJump(curve, 200) * 0.7,
            "the parametric curve has a corner at a tonal divider");
    }

    [Fact]
    public void ParametricDividersStayOrderedAndSpreadTheirTonalRegion()
    {
        var curve = new CameraRawCurveSettings { Darks = 100 };
        var before = curve.Parametric(0.4);
        curve.DarkSplit = 70;
        Assert.True(curve.Parametric(0.4) > before);

        Assert.False(new CameraRawSettings
        {
            ParametricCurve = new CameraRawCurveSettings { ShadowSplit = 90, DarkSplit = 50 },
        }.IsValid);
        Assert.False(new CameraRawSettings
        {
            ParametricCurve = new CameraRawCurveSettings { DarkSplit = 94, LightSplit = 95 },
        }.IsValid);
    }

    [Theory]
    [InlineData(2, 4, 6, 5, 7, 9)]
    [InlineData(94, 96, 98, 90, 95, 98)]
    public void ParametricDividerDragEndpointsCanPreviewAndApply(double shadowSplit, double darkSplit,
        double lightSplit, double expectedShadow, double expectedDark, double expectedLight)
    {
        // The graph permits the whole 2...98 interaction band.  Raw interaction values are normalized to Camera
        // Raw's fitted anchor band at execution time, rather than refusing a preview or an Apply after a legal drag.
        var settings = new CameraRawSettings
        {
            ParametricCurve = new CameraRawCurveSettings
            {
                Darks = -51,
                ShadowSplit = shadowSplit,
                DarkSplit = darkSplit,
                LightSplit = lightSplit,
            },
        };
        Assert.True(settings.IsValid);

        var normalized = settings.ParametricCurve.Normalized();
        Assert.Equal(expectedShadow, normalized.ShadowSplit);
        Assert.Equal(expectedDark, normalized.DarkSplit);
        Assert.Equal(expectedLight, normalized.LightSplit);

        // 8/255 lives in the darks range for both normalized boundary cases, so it also proves the accepted
        // drag is not merely treated as a no-op.
        var source = new SKColor(8, 8, 8);
        var (previewDocument, previewLayer) = Flat(source);
        using (previewDocument)
        {
            Assert.True(CameraRawEdits.Preview(previewDocument, previewLayer.ID, settings,
                shadows: false, highlights: false, sharpenMask: false, out _));
            Assert.NotEqual(source, previewLayer.Asset!.Image.GetPixel(4, 4));
        }

        var (document, layer) = Flat(source);
        using (document)
        {
            Assert.True(CameraRawEdits.Apply(document, layer.ID, settings));
            Assert.NotEqual(source, layer.Asset!.Image.GetPixel(4, 4));
        }
    }

    [Fact]
    public void ParametricCurveMatchesThePhotoshopTraceFromUpstream()
    {
        var curve = new CameraRawCurveSettings { Darks = -51, Lights = 59 };
        (double Tone, double Expected)[] photoshop =
        [
            (0.093, 0.011), (0.192, 0.089), (0.267, 0.174), (0.367, 0.310), (0.491, 0.498),
            (0.616, 0.698), (0.690, 0.804), (0.765, 0.886), (0.840, 0.947), (0.915, 0.982),
        ];

        foreach (var (tone, expected) in photoshop)
        {
            Assert.InRange(curve.Parametric(tone), expected - 0.035, expected + 0.035);
        }
    }

    [Fact]
    public void RgbToneCurveDeepensOrangeAndRefineSaturationMatchesTheThreeUpstreamModes()
    {
        var curve = new CameraRawCurveSettings { Darks = -51, Lights = 59 };
        static CameraRawSettings Settings(CameraRawCurveSettings current, double refine = 0) => new()
        {
            ParametricCurve = current.Clone(),
            RefineSaturation = refine,
        };

        var source = new SKColor(217, 89, 26);
        var luminanceOnly = Apply(source, Settings(curve, -100));
        var standard = Apply(source, Settings(curve));
        var boosted = Apply(source, Settings(curve, 100));

        // macOS's v1.4.5 golden checks these Photoshop-traced output characteristics.
        Assert.True(standard.Red > 230 && standard.Green < 80, $"RGB tone result was {standard}");
        Assert.True((double)luminanceOnly.Green / luminanceOnly.Red > 0.35,
            $"-100 Refine Saturation did not preserve the orange balance: {luminanceOnly}");
        Assert.True(Chroma(boosted) >= Chroma(standard), $"+100 did not strengthen RGB color: {standard} -> {boosted}");
        Assert.NotEqual(luminanceOnly, standard);
        Assert.NotEqual(standard, boosted);
    }

    [Fact]
    public void RgbToneIsAppliedOnceThenIndividualChannelsRun()
    {
        var master = new CurvesSettings();
        master.Channels[0] =
        [
            new CurvePoint { X = 0, Y = 0 },
            new CurvePoint { X = 128, Y = 200 },
            new CurvePoint { X = 255, Y = 255 },
        ];
        var once = Apply(new SKColor(128, 128, 128), new CameraRawSettings { Curve = master });
        Assert.InRange(once.Red, 195, 205);
        Assert.Equal(once.Red, once.Green);
        Assert.Equal(once.Green, once.Blue);

        var channels = new CurvesSettings();
        channels.Channels[1] =
        [
            new CurvePoint { X = 0, Y = 0 },
            new CurvePoint { X = 128, Y = 220 },
            new CurvePoint { X = 255, Y = 255 },
        ];
        var shifted = Apply(new SKColor(128, 128, 128), new CameraRawSettings { Curve = channels });
        Assert.True(shifted.Red > shifted.Green && shifted.Red > shifted.Blue, $"channel curve result was {shifted}");
    }

    [Fact]
    public void CurveKernelKeepsPremultipliedAlphaAndLeavesTransparentPixelsUntouched()
    {
        var tone = Enumerable.Range(0, 256).Select(value => (float)Math.Min(1, value / 128.0)).ToArray();
        var identity = Enumerable.Range(0, 256).Select(value => value / 255f).ToArray();
        var mixer = new float[24];
        var grade = new float[12];
        // First pixel is half alpha and premultiplied; second is intentionally nonzero RGB with zero alpha.
        byte[] pixels = [64, 24, 8, 128, 27, 44, 55, 0];

        AdjustPixels.CameraRawCurveColor(pixels, 2, 1, 8, tone, identity, identity, identity,
            0, mixer, 0, [], grade, 0.5, 0, -1);

        Assert.Equal(128, pixels[3]);
        Assert.InRange(pixels[0], 0, pixels[3]);
        Assert.InRange(pixels[1], 0, pixels[3]);
        Assert.InRange(pixels[2], 0, pixels[3]);
        Assert.Equal(new byte[] { 27, 44, 55, 0 }, pixels[4..8]);
    }

    [Fact]
    public void CameraRawCurveSettingsAreTransientAndDoNotChangeTheProjectCurveSchema()
    {
        var before = System.Text.Json.JsonSerializer.Serialize(new CurvesSettings(), ManifestJson.Options);
        _ = new CameraRawCurveSettings { Darks = -51, Lights = 59, DarkSplit = 70 };
        var after = System.Text.Json.JsonSerializer.Serialize(new CurvesSettings(), ManifestJson.Options);

        Assert.Equal(before, after);
        Assert.Equal(11, ProjectManifest.Current);
        Assert.DoesNotContain("Parametric", after, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyingParametricCameraRawDoesNotAddItsTransientStateToACompPackage()
    {
        var (document, layer) = Flat(new SKColor(217, 89, 26));
        var root = Path.Combine(Path.GetTempPath(), "CompositorCameraRaw-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "transient.comp");
        try
        {
            Assert.True(CameraRawEdits.Apply(document, layer.ID, new CameraRawSettings
            {
                ParametricCurve = new CameraRawCurveSettings { Darks = -51, Lights = 59 },
            }));
            var applied = layer.Asset!.Image.GetPixel(4, 4);

            // FromDocument and the package writer are the actual .comp boundary.  Camera Raw has baked its
            // pixels by this point, so the raw-only parametric state must not acquire a manifest field.
            ProjectStore.Save(ProjectSnapshot.FromDocument(document), package);
            var manifest = File.ReadAllText(Path.Combine(package, ProjectStore.ManifestName));
            Assert.DoesNotContain("Parametric", manifest, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CameraRaw", manifest, StringComparison.OrdinalIgnoreCase);

            using var loaded = ProjectStore.Load(package);
            Assert.Equal(11, loaded.Manifest.Version);
            using var reloaded = loaded.ToDocument();
            Assert.Equal(applied, reloaded.Layers.Single().Asset!.Image.GetPixel(4, 4));
        }
        finally
        {
            document.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CameraRawRejectsMoreThanTheUpstreamPointCurveLimit()
    {
        var points = Enumerable.Range(0, CameraRawCurveSettings.MaximumPointCount + 1)
            .Select(index => new CurvePoint { X = index * 255.0 / CameraRawCurveSettings.MaximumPointCount, Y = index * 255.0 / CameraRawCurveSettings.MaximumPointCount })
            .ToList();
        var curve = new CurvesSettings();
        curve.Channels[0] = points;
        Assert.False(new CameraRawSettings { Curve = curve }.IsValid);
    }

    [Fact]
    public void ChoosingAPointCurveChannelIsViewStateNotAnEdit()
    {
        var curve = new CurvesSettings { Channel = LevelsChannel.Red };
        var settings = new CameraRawSettings { Curve = curve };

        Assert.False(settings.CurveMoves);
        Assert.True(settings.IsIdentity);
    }

    private static int Chroma(SKColor color) => Math.Max(color.Red, Math.Max(color.Green, color.Blue))
        - Math.Min(color.Red, Math.Min(color.Green, color.Blue));
}
