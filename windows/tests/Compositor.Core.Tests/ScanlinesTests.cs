using Compositor.Core.Document;
using System.Text.Json;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using Compositor.Core.Pixels;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;

namespace Compositor.Core.Tests;

/// <summary>Source-backed CRT fixtures from 9c99853/cd2f998 and an independent double-precision beam oracle.</summary>
public sealed class ScanlinesTests : ProjectTestBase
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void EveryStyleMatchesPixelsProducedByTheUnmodifiedUpstreamCKernel(int style)
    {
        const int width = 23, height = 19;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var at = (y * width + x) * 4;
            var alpha = x % 7 == 0 ? 0 : x % 3 == 0 ? 128 : 255;
            pixels[at] = (byte)(((x * 11 + y * 3) % 256) * alpha / 255);
            pixels[at + 1] = (byte)(((x * 5 + y * 13) % 256) * alpha / 255);
            pixels[at + 2] = (byte)(((x * 17 + y * 7) % 256) * alpha / 255);
            pixels[at + 3] = (byte)alpha;
        }
        var p = new DitherParams
        {
            Style = style, Levels = 3, Diffusion = .8f, Density = .13f, Contrast = -.11f, Cell = 8,
            Angle = .25f, LightOnDark = true, OriginalColors = style % 2 != 0,
            Dark = [10, 20, 80], Light = [230, 160, 60], GlyphWidth = 2, GlyphHeight = 2, GlyphCount = 3,
            Glyphs = [0, 0, 0, 0, 0, 255, 255, 0, 255, 255, 255, 255], GlyphCoverage = [0, .5f, 1],
            Dots = .63f, Wobble = 4,
        };
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dither-upstream.json")));
        var expected = Convert.FromHexString(fixture.RootElement.GetProperty("styles")[style].GetProperty("rgba").GetString()!);
        Assert.Equal(1, DitherPixels.DitherApply(pixels, width, height, width * 4, p));
        Assert.Equal(expected.Length, pixels.Length);
        for (var i = 0; i < pixels.Length; i++)
            Assert.InRange(Math.Abs(pixels[i] - expected[i]), 0, 1); // Float libm rounding may differ by one byte.
    }

    [Fact]
    public void UpstreamWhiteGrayAndBlackBrightnessAndSpacing()
    {
        using var white = Flat(16, 32, SKColors.White, out var wl);
        using var gray = Flat(16, 32, new SKColor(89, 89, 89), out var gl);
        using var black = Flat(16, 32, SKColors.Black, out var bl);
        foreach (var (doc, layer) in new[] { (white, wl), (gray, gl), (black, bl) })
            Assert.True(DitherEdits.Apply(doc, layer.ID, DitherStyle.Scanlines, new DitherSettings { LineSpacing = 8, Glow = 0 }));

        // Upstream DitherTests.swift: white centers == 255, gaps < 40, gray total < half white, black == 0.
        for (var band = 0; band < 4; band++)
        {
            Assert.Equal(255, wl.Asset!.Image.GetPixel(5, band * 8 + 3).Red);
            Assert.Equal(255, wl.Asset.Image.GetPixel(5, band * 8 + 4).Red);
            Assert.Equal(0, wl.Asset.Image.GetPixel(5, band * 8).Red);
        }
        Assert.True(Column(gl).Sum() * 2 < Column(wl).Sum());
        Assert.All(Column(bl), value => Assert.Equal(0, value));
        Assert.Equal(new[] { 0, 255, 255, 255, 255, 255, 255, 0 }, Column(wl).Take(8));
    }

    [Theory]
    [InlineData(1, 1, 2)]
    [InlineData(19, 13, 3)]
    [InlineData(37, 29, 8)]
    [InlineData(65, 67, 32)]
    public void PartialBandsAndDotsMatchTheIndependentBeamFormula(int width, int height, int spacing)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 4] = 140; pixels[i * 4 + 1] = 90; pixels[i * 4 + 2] = 40; pixels[i * 4 + 3] = 255;
        }
        var expected = (byte[])pixels.Clone();
        var p = Parameters(spacing);
        p.OriginalColors = true;
        p.Density = .23f; p.Contrast = -.17f; p.Dots = .63f;
        Reference(expected, width, height, p);
        Assert.Equal(1, DitherPixels.DitherApply(pixels, width, height, width * 4, p));
        for (var i = 0; i < pixels.Length; i++) Assert.InRange(Math.Abs(pixels[i] - expected[i]), 0, 1);
    }

    [Fact]
    public void UpstreamDotsFormBeadsEveryLineSpacing()
    {
        using var doc = Flat(64, 16, SKColors.White, out var layer);
        Assert.True(DitherEdits.Apply(doc, layer.ID, DitherStyle.Scanlines,
            new DitherSettings { LineSpacing = 8, Dots = 100, Glow = 0 }));
        var row = Enumerable.Range(0, 64).Select(x => (int)layer.Asset!.Image.GetPixel(x, 4).Red).ToArray();
        Assert.Equal(255, row[3]); Assert.Equal(255, row[4]);
        Assert.True(row[0] < 60 && row[8] < 60);
        for (var x = 0; x < 56; x++) Assert.Equal(row[x], row[x + 8]);
    }

    [Fact]
    public void WobbleMatchesTheUpstreamTwoSineWaveShifts()
    {
        using var doc = Flat(64, 64, SKColors.Black, out var layer);
        for (var y = 0; y < 64; y++)
            for (var x = 32; x < 64; x++) layer.Asset!.Image.SetPixel(x, y, SKColors.White);
        Assert.True(DitherEdits.Apply(doc, layer.ID, DitherStyle.Scanlines,
            new DitherSettings { LineSpacing = 8, Wobble = 12, Glow = 0 }));
        for (var line = 0; line < 8; line++)
        {
            var shift = (int)Math.Round(12 * (Math.Sin(line * .45) * .7 + Math.Sin(line * 1.7 + 1.3) * .3),
                MidpointRounding.AwayFromZero);
            var edge = Enumerable.Range(0, 64).First(x => layer.Asset!.Image.GetPixel(x, line * 8 + 4).Red > 128);
            Assert.Equal(32 + shift, edge);
        }
    }

    [Fact]
    public void UpstreamGlowLightsTheGapsAndKeepsAlphaAndTransparentBorders()
    {
        using var doc = Flat(16, 32, SKColors.White, out var layer);
        Assert.True(DitherEdits.Apply(doc, layer.ID, DitherStyle.Scanlines,
            new DitherSettings { LineSpacing = 8, Glow = 100 }));
        Assert.True(layer.Asset!.Image.GetPixel(5, 0).Red > 40);

        using var alpha = Flat(33, 19, new SKColor(200, 100, 50, 128), out var al);
        for (var y = 0; y < 19; y++)
            for (var x = 0; x < 8; x++) al.Asset!.Image.SetPixel(x, y, SKColors.Transparent);
        Assert.True(DitherEdits.Apply(alpha, al.ID, DitherStyle.Scanlines,
            new DitherSettings { LineSpacing = 5, Glow = 100, Colors = DitherColors.Original }));
        for (var y = 0; y < 19; y++)
            for (var x = 0; x < 33; x++)
            {
                var color = al.Asset!.Image.GetPixel(x, y);
                Assert.Equal(x < 8 ? 0 : 128, color.Alpha);
                if (x < 8) Assert.Equal(new SKColor(0, 0, 0, 0), color);
            }
    }

    [Fact]
    public void OriginalAndTwoColorsUseTheSourceAveragingFormulaWithPartialAlpha()
    {
        foreach (var original in new[] { false, true })
        {
            const int width = 23, height = 17;
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    pixels[i + 3] = (byte)(x % 4 == 0 ? 0 : 128);
                    pixels[i] = pixels[i + 3] == 0 ? (byte)0 : (byte)(y * 5);
                    pixels[i + 1] = pixels[i + 3] == 0 ? (byte)0 : (byte)(x * 4);
                    pixels[i + 2] = pixels[i + 3] == 0 ? (byte)0 : (byte)30;
                }
            var expected = (byte[])pixels.Clone();
            var p = Parameters(6);
            p.OriginalColors = original; p.Dark = [10, 20, 80]; p.Light = [230, 160, 60]; p.Wobble = 4;
            Reference(expected, width, height, p);
            Assert.Equal(1, DitherPixels.DitherApply(pixels, width, height, width * 4, p));
            for (var i = 0; i < pixels.Length; i++) Assert.InRange(Math.Abs(pixels[i] - expected[i]), 0, 1);
        }
    }

    [Fact]
    public void GlowUsesTheUpstreamPremultipliedAdditiveEquation()
    {
        using var image = new SKBitmap(new SKImageInfo(31, 23, SKColorType.Rgba8888, SKAlphaType.Premul));
        var pixels = image.GetPixelSpan();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 32; pixels[i + 1] = 16; pixels[i + 2] = 8; pixels[i + 3] = 128;
        }
        Assert.True(ScanlinesPixels.Glow(image, spacing: 8, percent: 20));
        // A constant signal is unchanged by a normalized Gaussian, even at a clamped edge. Upstream's
        // addition is base + blurred * (.20 * 2.5) * alpha / 255, limited to the original alpha.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(40, pixels[i]); Assert.Equal(20, pixels[i + 1]);
            Assert.Equal(10, pixels[i + 2]); Assert.Equal(128, pixels[i + 3]);
        }
    }

    [Fact]
    public void ParallelBandsEqualSequentialKernelAndLeaveRowPaddingAlone()
    {
        const int width = 129, height = 133;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var random = new Random(3004);
        random.NextBytes(bitmap.GetPixelSpan());
        var sequential = bitmap.GetPixelSpan().ToArray();
        var p = Parameters(7); p.OriginalColors = true; p.Dots = .55f; p.Wobble = 12;
        Assert.Equal(1, DitherPixels.DitherApply(sequential, width, height, bitmap.RowBytes, p));
        Assert.True(ScanlinesPixels.Apply(bitmap, p));
        Assert.Equal(sequential, bitmap.GetPixelSpan().ToArray());

        var padded = new byte[2 * 20];
        Array.Fill(padded, (byte)99);
        for (var y = 0; y < 2; y++) for (var x = 0; x < 3; x++) padded[y * 20 + x * 4 + 3] = 255;
        Assert.Equal(1, DitherPixels.DitherApply(padded, 3, 2, 20, Parameters(2)));
        Assert.Equal(Enumerable.Repeat((byte)99, 8), padded.Skip(12).Take(8));
        Assert.Equal(Enumerable.Repeat((byte)99, 8), padded.Skip(32).Take(8));
    }

    [Fact]
    public void ScanlinesIgnoreChunkyPixelAndMarkControls()
    {
        using var a = Flat(19, 23, new SKColor(190, 130, 70), out var al);
        using var b = Flat(19, 23, new SKColor(190, 130, 70), out var bl);
        Assert.True(DitherEdits.Apply(a, al.ID, DitherStyle.Scanlines, new DitherSettings { Glow = 0 }));
        Assert.True(DitherEdits.Apply(b, bl.ID, DitherStyle.Scanlines, new DitherSettings
        {
            Glow = 0, PixelSize = 32, PixelShape = DitherPixelShape.Dot, CellSize = 64, TextSize = 64,
            Characters = "X", LightOnDark = false, Diffusion = 0, Angle = -90, Levels = 8,
        }));
        Assert.Equal(al.Asset!.Image.GetPixelSpan().ToArray(), bl.Asset!.Image.GetPixelSpan().ToArray());
    }

    [Fact]
    public void SettingsAppendTheStyleAndNormalizeAllNewAmounts()
    {
        Assert.Equal(Enumerable.Range(0, 11), Enum.GetValues<DitherStyle>().Select(value => (int)value));
        Assert.Equal(10, (int)DitherStyle.Scanlines);
        Assert.False(DitherSettings.UsesPixelSize(DitherStyle.Scanlines));
        var defaults = new DitherSettings().Normalized();
        Assert.Equal(4, defaults.LineSpacing); Assert.Equal(35, defaults.Glow);
        var values = new DitherSettings { LineSpacing = double.NaN, Glow = 400, Dots = -10, Wobble = 800 }.Normalized();
        Assert.Equal(4, values.LineSpacing); Assert.Equal(100, values.Glow);
        Assert.Equal(0, values.Dots); Assert.Equal(64, values.Wobble);
        Assert.Equal(5, new DitherSettings { LineSpacing = 4.5 }.Normalized().LineSpacing);
    }

    [Fact]
    public void SelectionClipsTheCrtResultAndLeavesTheOtherNativePixelsUntouched()
    {
        using var doc = Flat(33, 29, new SKColor(180, 120, 60), out var layer);
        var before = layer.Asset!.Image.GetPixelSpan().ToArray();
        SelectionEdits.Select(doc, SKRectI.Create(0, 0, 16, 29));
        Assert.True(DitherEdits.Apply(doc, layer.ID, DitherStyle.Scanlines, new DitherSettings { Glow = 70 }));
        var after = layer.Asset!.Image.GetPixelSpan().ToArray();
        Assert.False(before.SequenceEqual(after));
        for (var y = 0; y < 29; y++)
            Assert.Equal(before.Skip(y * 33 * 4 + 16 * 4).Take(17 * 4), after.Skip(y * 33 * 4 + 16 * 4).Take(17 * 4));
    }

    [Fact]
    public void BoundedMemoryPlanningRejectsBeforeAnEditAndHistoryRemainUnchanged()
    {
        Assert.False(ScanlinesPixels.CanPrepare(30_000, 30_000, 32, 100, true));
        Assert.True(ScanlinesPixels.WorkingBytes(4_096, 4_096, 8, 100, false) < ScanlinesPixels.MaxWorkingBytes);
        using var doc = Flat(16, 16, SKColors.White, out var layer);
        var asset = layer.Asset;
        var bytes = asset!.Image.GetPixelSpan().ToArray();
        var history = new DocumentHistory();
        history.Begin("Dither", doc, layer.ID);
        Assert.False(DitherEdits.ApplyScanlines(doc, layer.ID, new DitherSettings(), memoryBudget: 1));
        history.End(doc, layer.ID);
        Assert.Same(asset, layer.Asset);
        Assert.Equal(bytes, asset.Image.GetPixelSpan().ToArray());
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(0, DitherPixels.DitherApply(new byte[4], int.MaxValue, int.MaxValue, 4, Parameters(4)));
    }

    [Fact]
    public void PreviewCancelApplyUndoRedoAndFormatElevenRoundTrip()
    {
        using var doc = Flat(33, 29, new SKColor(180, 120, 60), out var layer);
        var original = layer.Asset!;
        var before = original.Image.GetPixelSpan().ToArray();
        var history = new DocumentHistory();
        var settings = new DitherSettings { Glow = 35, Dots = 70, Wobble = 4 };
        var preview = Assert.IsType<FilterPreview>(FilterPreview.Begin(doc, layer.ID));
        Assert.True(preview.Show((target, id) => DitherEdits.Apply(target, id, DitherStyle.Scanlines, settings)));
        Assert.Same(original, layer.Asset);
        Assert.Equal(0, history.UndoCount);
        preview.Dispose(); preview.Dispose();
        Assert.False(preview.Show((target, id) => DitherEdits.Apply(target, id, DitherStyle.Scanlines, settings)));
        Assert.Equal(before, original.Image.GetPixelSpan().ToArray());

        history.Begin("Dither", doc, layer.ID);
        Assert.True(DitherEdits.Apply(doc, layer.ID, DitherStyle.Scanlines, settings));
        history.End(doc, layer.ID);
        Assert.Equal(1, history.UndoCount);
        var edited = layer.Asset!.Image.GetPixelSpan().ToArray();
        Assert.False(before.SequenceEqual(edited));
        Assert.Equal(before, Assert.Single(Assert.IsType<DocumentHistory.Snapshot>(history.Undo()).Document!.Layers).Asset!.Image.GetPixelSpan().ToArray());
        Assert.Equal(edited, Assert.Single(Assert.IsType<DocumentHistory.Snapshot>(history.Redo()).Document!.Layers).Asset!.Image.GetPixelSpan().ToArray());

        var package = PathIn("Scanlines.comp");
        ProjectStore.Save(ProjectSnapshot.FromDocument(doc), package);
        using var loaded = ProjectStore.Load(package);
        Assert.Equal(11, loaded.Manifest.Version);
        Assert.Equal(11, ProjectManifest.Current);
        Assert.Equal(edited, loaded.Images[layer.ID].Image.GetPixelSpan().ToArray());
    }

    private static int[] Column(ImageLayer layer) => Enumerable.Range(0, layer.Asset!.Height)
        .Select(y => (int)layer.Asset.Image.GetPixel(5, y).Red).ToArray();

    private static CanvasDocument Flat(int width, int height, SKColor color, out ImageLayer layer)
    {
        var pixels = new SKBitmap(Bitmaps.ColorInfo(width, height)); pixels.Erase(color);
        var document = new CanvasDocument(Guid.NewGuid(), width, height);
        layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(pixels, "CRT"), new LayerTransform(0, 0, width, height), "CRT");
        document.Layers.Add(layer);
        return document;
    }

    private static DitherParams Parameters(int spacing) => new()
    {
        Style = DitherPixels.Scanlines, Cell = spacing, Dark = [0, 0, 0], Light = [255, 255, 255],
    };

    // Independent reference deliberately materializes adjusted source tones from the immutable input,
    // unlike the streaming production path. Equations and ordering are taken from cd2f998 DitherPixels.c.
    private static void Reference(byte[] pixels, int width, int height, DitherParams p)
    {
        var source = (byte[])pixels.Clone();
        var spacing = p.Cell;
        var gamma = Math.Pow(2, p.Density * 1.5);
        var contrast = p.Contrast >= 0 ? 1 / (1 - .95 * p.Contrast) : 1 + p.Contrast;
        double Tone(double value) => Math.Clamp((Math.Pow(Math.Clamp(value, 0, 1), gamma) - .5) * contrast + .5, 0, 1);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var at = (y * width + x) * 4;
            if (source[at + 3] == 0) continue;
            var line = y / spacing;
            var along = (x + .5) % spacing - spacing / 2.0;
            var center = Math.Clamp((int)Math.Round(x - along * p.Dots, MidpointRounding.AwayFromZero), 0, width - 1);
            var shift = (int)Math.Round(p.Wobble * (Math.Sin(line * .45) * .7 + Math.Sin(line * 1.7 + 1.3) * .3), MidpointRounding.AwayFromZero);
            var sx = center - shift;
            var sums = new double[3]; var n = 0;
            if (sx >= 0 && sx < width) for (var yy = line * spacing; yy < Math.Min(height, (line + 1) * spacing); yy++)
            {
                var i = (yy * width + sx) * 4;
                if (source[i + 3] == 0) continue;
                var r = source[i] / (double)source[i + 3];
                var g = source[i + 1] / (double)source[i + 3];
                var b = source[i + 2] / (double)source[i + 3];
                if (p.OriginalColors) { sums[0] += Tone(r); sums[1] += Tone(g); sums[2] += Tone(b); }
                else sums[0] += Tone(.2126 * r + .7152 * g + .0722 * b);
                n++;
            }
            for (var c = 0; c < 3; c++) sums[c] /= Math.Max(1, n);
            var tone = p.OriginalColors ? .2126 * sums[0] + .7152 * sums[1] + .0722 * sums[2] : sums[0];
            var beam = spacing / 2.0 * (.2 + .5 * Math.Sqrt(Math.Clamp(tone, 0, 1)));
            var vertical = y % spacing + .5 - spacing / 2.0;
            var cover = Math.Clamp(beam - Math.Sqrt(vertical * vertical + Math.Pow(along * p.Dots, 2)) + .5, 0, 1);
            for (var c = 0; c < 3; c++)
            {
                var background = p.OriginalColors ? 0 : p.Dark[c] / 255.0;
                var color = p.OriginalColors ? sums[c] : background + (p.Light[c] / 255.0 - background) * tone;
                pixels[at + c] = (byte)Math.Round(Math.Clamp(background + (color * 1.35 - background) * cover, 0, 1) * source[at + 3], MidpointRounding.AwayFromZero);
            }
        }
    }
}
