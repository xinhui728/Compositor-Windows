using System.Text;
using System.Text.Json;
using Compositor.Core.Document;
using Compositor.Core.IO;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;
using Format = Compositor.Core.Format;

namespace Compositor.Core.Tests;

/// <summary>
/// Persistence and history coverage for a Clone Stamp stroke whose pixels live in a transformed native
/// layer grid. The source is deliberately not a document-sized proxy: these assertions inspect the stored
/// raster before and after history and project-package boundaries.
/// </summary>
public sealed class NativeResolutionBrushPersistenceTests : ProjectTestBase
{
    [Fact]
    public void TransformedNativeCloneIsOneHistoryEditAndUndoRedoRestoresRawPixels()
    {
        using var document = TransformedCloneFixture(out var layer, out var stroke, out var settings,
            out var targetX, out var targetY);
        var before = RawPixels(layer.Asset!.Image);
        var history = new DocumentHistory();

        history.Begin("Clone Stamp", document, layer.ID);
        Assert.True(BrushEdits.Paint(document, layer.ID, [stroke], settings));
        history.End(document, layer.ID);

        var edited = RawPixels(layer.Asset!.Image);
        Assert.False(before.SequenceEqual(edited));
        Assert.Equal(1, history.UndoCount);
        Assert.NotEqual(layer.Asset.Image.GetPixel(targetX, targetY), Pixel(before, layer.Asset.Image.RowBytes, targetX, targetY));

        var undone = Assert.IsType<DocumentHistory.Snapshot>(history.Undo());
        var undoImage = Assert.Single(undone.Document!.Layers).Asset!.Image;
        Assert.Equal(before, RawPixels(undoImage));

        var redone = Assert.IsType<DocumentHistory.Snapshot>(history.Redo());
        var redoImage = Assert.Single(redone.Document!.Layers).Asset!.Image;
        Assert.Equal(edited, RawPixels(redoImage));
    }

    [Fact]
    public void TransformedNativeCloneSurvivesAFormatElevenProjectRoundTrip()
    {
        using var document = TransformedCloneFixture(out var layer, out var stroke, out var settings,
            out _, out _);
        Assert.True(BrushEdits.Paint(document, layer.ID, [stroke], settings));
        var expectedTransform = layer.Transform;
        var expectedPixels = layer.Asset!.Image;
        var package = PathIn("NativeClone.comp");

        // FromDocument intentionally shares the live raster while Save encodes it synchronously.
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), package);

        using var loaded = ProjectStore.Load(package);
        Assert.Equal(11, Compositor.Core.Format.ProjectManifest.Current);
        Assert.Equal(Compositor.Core.Format.ProjectManifest.Current, loaded.Manifest.Version);
        var record = Assert.Single(loaded.Manifest.Layers);
        Assert.Equal(expectedTransform, record.Transform.ToRuntime());
        AssertBitmapsEqual(expectedPixels, loaded.Images[layer.ID].Image);
    }

    [Fact]
    public void RenderRegionMatchesTheWholeCanvasAfterATransformedNativeClone()
    {
        using var document = TransformedCloneFixture(out var layer, out var stroke, out var settings,
            out _, out _);
        Assert.True(BrushEdits.Paint(document, layer.ID, [stroke], settings));

        var region = SKRectI.Create(30, 25, 120, 80);
        using var whole = DocumentRenderer.Render(document);
        using var piece = DocumentRenderer.RenderRegion(document, region);
        for (var y = 0; y < region.Height; y++)
        {
            for (var x = 0; x < region.Width; x++)
            {
                Assert.Equal(whole.GetPixel(region.Left + x, region.Top + y), piece.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void TransformedNativeBlurSurvivesAFormatElevenRoundTripAndTiledRender()
    {
        using var document = TransformedBlurFixture(out var layer, out var stroke, out var settings);
        var sourcePixels = RawPixels(layer.Asset!.Image);
        Assert.True(BrushEdits.Paint(document, layer.ID, [stroke], settings));
        var expectedPixels = RawPixels(layer.Asset!.Image);
        Assert.False(sourcePixels.SequenceEqual(expectedPixels));

        // The brush only changes the native PNG asset.  The writer must therefore keep the ordinary v11
        // manifest shape byte-for-byte rather than introducing a brush-specific field or format revision.
        var snapshot = ProjectSnapshot.FromDocument(document);
        var expectedManifest = Encoding.UTF8.GetString(Format.ManifestJson.Serialize(snapshot.Manifest));
        var package = PathIn("NativeBlur.comp");
        ProjectStore.Save(snapshot, package);
        var persistedManifest = File.ReadAllText(Path.Combine(package, ProjectStore.ManifestName), Encoding.UTF8);

        Assert.Equal(expectedManifest, persistedManifest);
        using (var manifest = JsonDocument.Parse(persistedManifest))
        {
            Assert.Equal(
            ["activeLayerID", "colorSpace", "documentID", "format", "height", "layers", "resolution", "version", "width"],
                manifest.RootElement.EnumerateObject().Select(property => property.Name));
        }

        using var loaded = ProjectStore.Load(package);
        Assert.Equal(11, Format.ProjectManifest.Current);
        Assert.Equal(Format.ProjectManifest.Current, loaded.Manifest.Version);
        Assert.Equal(expectedManifest, Encoding.UTF8.GetString(Format.ManifestJson.Serialize(loaded.Manifest)));
        var record = Assert.Single(loaded.Manifest.Layers);
        Assert.Equal(layer.Transform, record.Transform.ToRuntime());
        Assert.Equal(expectedPixels, RawPixels(loaded.Images[layer.ID].Image));

        using var reloaded = loaded.ToDocument();
        AssertRegionMatchesWhole(reloaded, SKRectI.Create(64, 48, 160, 112));
    }

    private static CanvasDocument TransformedCloneFixture(out ImageLayer layer, out SKPoint stroke,
        out BrushSettings settings, out int targetX, out int targetY)
    {
        const int width = 80;
        const int height = 60;
        var pixels = new SKBitmap(Bitmaps.ColorInfo(width, height));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // A unique native value at every point makes a bad document proxy or coordinate conversion
                // immediately visible, including after its PNG round trip.
                pixels.SetPixel(x, y, new SKColor((byte)(x * 3), (byte)(y * 4), (byte)(x + y), 255));
            }
        }

        // Native pixels are scaled non-uniformly and mirrored in document space. The source and target are
        // forty native pixels apart, hence exactly eighty document pixels apart after the horizontal flip.
        var transform = new LayerTransform(30, 35, 160, 90, Rotation: 0, FlipX: true);
        layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(pixels, "Native clone"), transform,
            "Native clone");
        var document = new CanvasDocument(Guid.NewGuid(), 220, 160);
        document.Layers.Add(layer);

        const int sourceX = 15;
        const int sourceY = 30;
        targetX = 55;
        targetY = 30;
        var toDocument = BrushEdits.PixelToDocument(transform, width, height);
        stroke = toDocument.MapPoint(targetX + 0.5f, targetY + 0.5f);
        var source = toDocument.MapPoint(sourceX + 0.5f, sourceY + 0.5f);
        settings = new BrushSettings(Diameter: 4, Hardness: 1, Opacity: 1, Mode: BrushMode.Clone,
            CloneFrom: new SKPointI((int)Math.Round(source.X - stroke.X), (int)Math.Round(source.Y - stroke.Y)));
        return document;
    }

    private static CanvasDocument TransformedBlurFixture(out ImageLayer layer, out SKPoint stroke,
        out BrushSettings settings)
    {
        const int width = 96;
        const int height = 64;
        var pixels = new SKBitmap(Bitmaps.ColorInfo(width, height));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // One-pixel high-frequency bands make this a real native-resolution blur fixture: a
                // document-space proxy would lose the source detail before the PNG round trip.
                pixels.SetPixel(x, y, (x & 1) == 0
                    ? new SKColor(24, 80, 240, 255)
                    : new SKColor(240, 172, 20, 255));
            }
        }

        var transform = new LayerTransform(92, 66, 144, 72, Rotation: 0, FlipX: true, FlipY: true,
            Sampling: Format.LayerSampling.Nearest);
        layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(pixels, "Native blur"), transform,
            "Native blur");
        var document = new CanvasDocument(Guid.NewGuid(), 300, 220);
        document.Layers.Add(layer);
        var toDocument = BrushEdits.PixelToDocument(transform, width, height);
        stroke = toDocument.MapPoint(48.5f, 32.5f);
        settings = new BrushSettings(Diameter: 28, Hardness: 1, Opacity: 1, Mode: BrushMode.Blur, BlurRadius: 3);
        return document;
    }

    private static byte[] RawPixels(SKBitmap bitmap) => bitmap.GetPixelSpan().ToArray();

    private static SKColor Pixel(byte[] pixels, int rowBytes, int x, int y)
    {
        var at = y * rowBytes + x * 4;
        return new SKColor(pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]);
    }

    private static void AssertBitmapsEqual(SKBitmap expected, SKBitmap actual)
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

    private static void AssertRegionMatchesWhole(CanvasDocument document, SKRectI region)
    {
        using var whole = DocumentRenderer.Render(document);
        using var piece = DocumentRenderer.RenderRegion(document, region);
        for (var y = 0; y < region.Height; y++)
        {
            for (var x = 0; x < region.Width; x++)
            {
                Assert.Equal(whole.GetPixel(region.Left + x, region.Top + y), piece.GetPixel(x, y));
            }
        }
    }
}
