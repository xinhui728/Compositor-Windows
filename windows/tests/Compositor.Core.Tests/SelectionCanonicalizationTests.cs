using Compositor.Core.Document;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>Canonical states around Select All and Select ▸ Inverse.</summary>
public class SelectionCanonicalizationTests
{
    private static CanvasDocument Document() => new(Guid.NewGuid(), 20, 20);

    private static int CoverageAt(CanvasDocument document, int x, int y)
    {
        var region = document.Selection.CoverageRect(document.Width, document.Height);
        using var coverage = document.Selection.Coverage(region);
        if (coverage is null) return 255;
        var column = x - region.Left;
        var row = y - region.Top;
        if (column < 0 || row < 0 || column >= region.Width || row >= region.Height) return 0;
        return coverage.GetPixelSpan()[row * coverage.RowBytes + column];
    }

    [Fact]
    public void NoActiveSelectionBecomesAnExplicitSelectionOnlyThroughSelectAll()
    {
        using var document = Document();

        Assert.Null(document.Selection.Path);
        Assert.True(SelectionEdits.SelectAll(document));

        Assert.Equal(SKRect.Create(0, 0, 20, 20), document.Selection.Path!.Bounds);
        Assert.Equal(255, CoverageAt(document, 0, 0));
        Assert.Equal(255, CoverageAt(document, 19, 19));
    }

    [Fact]
    public void InvertingFullSelectionUsesCanonicalNoActiveSelection()
    {
        using var document = Document();
        Assert.True(SelectionEdits.SelectAll(document));

        Assert.True(SelectionEdits.Invert(document));

        Assert.Same(DocumentSelection.All, document.Selection);
        Assert.Null(document.Selection.Path);
        Assert.Equal(255, CoverageAt(document, 10, 10));
    }

    [Fact]
    public void InvertingPartialSelectionTakesTheComplement()
    {
        using var document = Document();
        Assert.True(SelectionEdits.Select(document, SKRectI.Create(5, 5, 10, 10), antialiased: false));

        Assert.True(SelectionEdits.Invert(document));

        Assert.NotNull(document.Selection.Path);
        Assert.Equal(255, CoverageAt(document, 0, 0));
        Assert.Equal(0, CoverageAt(document, 10, 10));
    }

    [Fact]
    public void InvertingAPartialSelectionTwiceRestoresIt()
    {
        using var document = Document();
        Assert.True(SelectionEdits.Select(document, SKRectI.Create(5, 5, 10, 10), antialiased: false));

        Assert.True(SelectionEdits.Invert(document));
        Assert.True(SelectionEdits.Invert(document));

        Assert.NotNull(document.Selection.Path);
        Assert.Equal(0, CoverageAt(document, 0, 0));
        Assert.Equal(255, CoverageAt(document, 10, 10));
        Assert.Equal(0, CoverageAt(document, 19, 19));
    }

    [Fact]
    public void InvertingFullSelectionRoundTripsThroughUndoAndRedo()
    {
        using var document = Document();
        var history = new DocumentHistory();
        Assert.True(SelectionEdits.SelectAll(document));

        history.Begin("Inverse Selection", document, activeLayer: null);
        Assert.True(SelectionEdits.Invert(document));
        history.End(document, activeLayer: null);

        Assert.True(history.CanUndo);
        var undone = history.Undo();
        Assert.NotNull(undone);
        Assert.NotNull(undone!.Value.Document!.Selection.Path);

        var redone = history.Redo();
        Assert.NotNull(redone);
        Assert.Same(DocumentSelection.All, redone!.Value.Document!.Selection);
        Assert.Null(redone.Value.Document.Selection.Path);
    }
}
