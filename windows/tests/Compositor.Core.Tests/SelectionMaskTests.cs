using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>Masks made from a selection, including the document state they consume.</summary>
public class SelectionMaskTests
{
    private static (CanvasDocument Document, ImageLayer Layer) Flat(int width = 20, int height = 20)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(SKColors.Red);
        var document = new CanvasDocument(Guid.NewGuid(), width, height);
        var layer = new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Layer"),
            new LayerTransform(0, 0, width, height), "Layer");
        document.Layers.Add(layer);
        return (document, layer);
    }

    private static byte At(ImageLayer layer, int x, int y) => layer.Mask!.Asset.Image.GetPixel(x, y).Red;

    [Theory]
    [InlineData(true, 255)]
    [InlineData(false, 0)]
    public void NoSelectionAddsTheExistingUniformRevealOrHideMask(bool revealing, byte expected)
    {
        var (document, layer) = Flat();
        using var _ = document;

        Assert.True(LayerMaskEdits.Add(document, layer.ID, revealing));

        Assert.Null(document.Selection.Path);
        Assert.Equal(1, layer.Mask!.Asset.Width);
        Assert.Equal(expected, At(layer, 0, 0));
    }

    [Theory]
    [InlineData(true, 255, 0)]
    [InlineData(false, 0, 255)]
    public void SelectionMaskUsesTheRequestedInsideAndOutsideValues(bool revealing, byte inside, byte outside)
    {
        var (document, layer) = Flat();
        using var _ = document;
        Assert.True(SelectionEdits.Select(document, SKRectI.Create(5, 5, 10, 10), antialiased: false));

        Assert.True(LayerMaskEdits.Add(document, layer.ID, revealing));

        Assert.Null(document.Selection.Path);
        Assert.Equal(20, layer.Mask!.Asset.Width);
        Assert.Equal(20, layer.Mask.Asset.Height);
        Assert.Equal(inside, At(layer, 10, 10));
        Assert.Equal(outside, At(layer, 1, 1));
    }

    [Theory]
    [InlineData(true, 255)]
    [InlineData(false, 0)]
    public void FullSelectionMakesAFullSizeUniformSelectionMask(bool revealing, byte expected)
    {
        var (document, layer) = Flat();
        using var _ = document;
        Assert.True(SelectionEdits.SelectAll(document));

        Assert.True(LayerMaskEdits.Add(document, layer.ID, revealing));

        Assert.Null(document.Selection.Path);
        Assert.Equal(20, layer.Mask!.Asset.Width);
        Assert.Equal(expected, At(layer, 10, 10));
    }

    [Fact]
    public void SelectionMaskOnAFolderUsesItsExistingMaskRepresentation()
    {
        using var document = new CanvasDocument(Guid.NewGuid(), 20, 20);
        var folder = new ImageLayer(Guid.NewGuid(), null, new LayerTransform(0, 0, 20, 20), "Folder")
        {
            IsGroup = true,
        };
        document.Layers.Add(folder);
        Assert.True(SelectionEdits.Select(document, SKRectI.Create(4, 4, 8, 8), antialiased: false));

        Assert.True(LayerMaskEdits.Add(document, folder.ID, revealing: true));

        Assert.Null(document.Selection.Path);
        Assert.Equal(20, folder.Mask!.Asset.Width);
        Assert.Equal(255, At(folder, 6, 6));
        Assert.Equal(0, At(folder, 1, 1));
    }

    [Fact]
    public void ExistingMaskRefusesSelectionCreationWithoutConsumingTheSelection()
    {
        var (document, layer) = Flat();
        using var _ = document;
        Assert.True(LayerMaskEdits.Add(document, layer.ID, revealing: true));
        var existing = layer.Mask;
        Assert.True(SelectionEdits.Select(document, SKRectI.Create(5, 5, 10, 10)));

        Assert.False(LayerMaskEdits.Add(document, layer.ID, revealing: false));

        Assert.Same(existing, layer.Mask);
        Assert.NotNull(document.Selection.Path);
    }

    [Fact]
    public void SelectionMaskAndConsumedSelectionRoundTripThroughHistory()
    {
        var (document, layer) = Flat();
        using var _ = document;
        var history = new DocumentHistory();
        Assert.True(SelectionEdits.Select(document, SKRectI.Create(5, 5, 10, 10), antialiased: false));

        history.Begin("Reveal Selection", document, layer.ID);
        Assert.True(LayerMaskEdits.Add(document, layer.ID, revealing: true));
        history.End(document, layer.ID);

        Assert.NotNull(layer.Mask);
        Assert.Null(document.Selection.Path);
        var undone = history.Undo()!.Value.Document!;
        Assert.Null(undone.Layers.Single().Mask);
        Assert.NotNull(undone.Selection.Path);
        var redone = history.Redo()!.Value.Document!;
        Assert.NotNull(redone.Layers.Single().Mask);
        Assert.Null(redone.Selection.Path);
    }
}
