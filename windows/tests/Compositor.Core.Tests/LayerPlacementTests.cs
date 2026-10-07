using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// Adding layers and folders, wrapping layers in a folder, moving a layer into one, and the mask and
/// clipping verbs. The compositor's own tests cover what those layers then look like.
/// </summary>
public class LayerPlacementTests
{
    private static ImageLayer Patch(SKColor colour, double x, double y, int width, int height, string name)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(colour);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, name),
            new Model.LayerTransform(x, y, width, height), name);
    }

    private static CanvasDocument Doc(int width, int height, params ImageLayer[] layers)
    {
        var document = new CanvasDocument(Guid.NewGuid(), width, height);
        document.Layers.AddRange(layers);
        return document;
    }

    private static List<string> Names(CanvasDocument document) =>
        document.Layers.Select(layer => layer.Name).ToList();

    [Fact]
    public void ANewBlankLayerGoesAboveTheSelectedOneWithNoPixels()
    {
        var a = Patch(SKColors.Blue, 0, 0, 8, 8, "A");
        var b = Patch(SKColors.Green, 0, 0, 8, 8, "B");
        using var document = Doc(8, 8, a, b);

        var made = LayerPlacement.AddBlank(document, a.ID);
        Assert.NotNull(made);
        Assert.Equal(new[] { "A", "Layer 1", "B" }, Names(document));
        var blank = document.Layers.Single(layer => layer.ID == made.Value);
        // Empty means no pixels at all, as the Mac build makes it.
        Assert.Null(blank.Asset);
        Assert.Equal(8, blank.Transform.Width);
        Assert.Equal(8, blank.Transform.Height);
    }

    [Fact]
    public void ANewBlankLayerAvoidsNamesTheDocumentIsUsing()
    {
        var used = Patch(SKColors.Blue, 0, 0, 8, 8, "Layer 1");
        using var document = Doc(8, 8, used);

        var made = LayerPlacement.AddBlank(document, used.ID);
        Assert.NotNull(made);
        Assert.Equal("Layer 2", document.Layers.Single(layer => layer.ID == made.Value).Name);
    }

    [Fact]
    public void ANewBlankLayerWithAFolderSelectedGoesToTheTopOfIt()
    {
        var folder = Folder("Folder", null);
        var first = Patch(SKColors.Blue, 0, 0, 8, 8, "First");
        first.ParentID = folder.ID;
        var second = Patch(SKColors.Green, 0, 0, 8, 8, "Second");
        second.ParentID = folder.ID;
        var above = Patch(SKColors.Red, 0, 0, 8, 8, "Above");
        using var document = Doc(8, 8, folder, first, second, above);

        var made = LayerPlacement.AddBlank(document, folder.ID);
        Assert.NotNull(made);
        var blank = document.Layers.Single(layer => layer.ID == made.Value);
        Assert.Equal(folder.ID, blank.ParentID);
        // Above the folder's last question, so it is the topmost thing inside the folder.
        Assert.True(document.Layers.IndexOf(blank) > document.Layers.IndexOf(second));
        Assert.True(document.Layers.IndexOf(blank) < document.Layers.IndexOf(above));
    }

    [Fact]
    public void ANewFolderGoesAboveTheSelectedLayerInTheSameFolder()
    {
        var inside = Patch(SKColors.Blue, 0, 0, 8, 8, "Inside");
        var folder = Folder("Outer", null);
        inside.ParentID = folder.ID;
        using var document = Doc(8, 8, folder, inside);

        var made = LayerPlacement.AddFolder(document, inside.ID);
        Assert.NotNull(made);
        var inner = document.Layers.Single(layer => layer.ID == made.Value);
        Assert.True(inner.IsGroup);
        Assert.Equal("Folder 1", inner.Name);
        // Inside the folder that held the layer it was made above.
        Assert.Equal(folder.ID, inner.ParentID);
    }

    [Fact]
    public void GroupingWrapsTheSelectedLayerAndTakesItsPlace()
    {
        var beneath = Patch(SKColors.Blue, 0, 0, 8, 8, "Beneath");
        var middle = Patch(SKColors.Green, 0, 0, 8, 8, "Middle");
        var above = Patch(SKColors.Red, 0, 0, 8, 8, "Above");
        using var document = Doc(8, 8, beneath, middle, above);
        using var was = DocumentRenderer.Render(document);

        var folder = LayerPlacement.GroupSelected(document, [middle.ID]);
        Assert.NotNull(folder);
        var group = document.Layers.Single(layer => layer.ID == folder.Value);
        Assert.True(group.IsGroup);
        Assert.Equal("Folder 1", group.Name);
        // The group takes the wrapped layer's place; the layer moves inside it.
        Assert.Equal(new[] { "Beneath", "Folder 1", "Above", "Middle" }, Names(document));
        Assert.Equal(group.ID, middle.ParentID);
        Assert.Null(beneath.ParentID);
        Assert.Null(above.ParentID);
        // Wrapping changes where a layer sits, not what the canvas shows.
        using var now = DocumentRenderer.Render(document);
        Assert.Equal(new SKColor(255, 0, 0), now.GetPixel(4, 4));
        Assert.Equal(was.GetPixel(4, 4), now.GetPixel(4, 4));
    }

    [Fact]
    public void GroupingIsRefusedWhenThereIsNothingToWrap()
    {
        var a = Patch(SKColors.Blue, 0, 0, 8, 8, "A");
        using var document = Doc(8, 8, a);

        Assert.Null(LayerPlacement.GroupSelected(document, [Guid.NewGuid()]));
        Assert.Null(LayerPlacement.GroupSelected(document, []));
    }

    [Fact]
    public void UngroupingPlacesDirectChildrenInTheFoldersSlotAndKeepsTheirAppearance()
    {
        var beneath = Patch(SKColors.Blue, 0, 0, 8, 8, "Beneath");
        var folder = Folder("Folder", null);
        var above = Patch(SKColors.Red, 0, 0, 2, 8, "Above");
        var first = Patch(SKColors.Green, 0, 0, 4, 8, "First");
        first.ParentID = folder.ID;
        var second = Patch(SKColors.Yellow, 4, 0, 4, 8, "Second");
        second.ParentID = folder.ID;
        using var document = Doc(8, 8, beneath, folder, above, first, second);
        using var was = DocumentRenderer.Render(document);

        Assert.True(LayerPlacement.Ungroup(document, folder.ID));

        Assert.Equal(new[] { "Beneath", "First", "Second", "Above" }, Names(document));
        Assert.All(new[] { first, second }, layer => Assert.Null(layer.ParentID));
        Assert.DoesNotContain(document.Layers, layer => layer.ID == folder.ID);
        Assert.Equal(new[] { "Beneath", "First", "Second", "Above" },
            document.HierarchyEntries().Select(entry => entry.Layer.Name));
        // A default pass-through folder has no visual contribution of its own.
        using var now = DocumentRenderer.Render(document);
        for (var x = 0; x < 8; x++) Assert.Equal(was.GetPixel(x, 4), now.GetPixel(x, 4));
    }

    [Fact]
    public void UngroupingRoundTripsTheFolderAndChildrenThroughHistory()
    {
        var below = Patch(SKColors.Blue, 0, 0, 8, 8, "Below");
        var folder = Folder("Folder", null);
        var first = Patch(SKColors.Green, 0, 0, 4, 8, "First");
        first.ParentID = folder.ID;
        var second = Patch(SKColors.Yellow, 4, 0, 4, 8, "Second");
        second.ParentID = folder.ID;
        using var document = Doc(8, 8, below, folder, first, second);
        var history = new DocumentHistory();

        history.Begin("Ungroup Layers", document, folder.ID);
        Assert.True(LayerPlacement.Ungroup(document, folder.ID));
        history.End(document, first.ID);

        Assert.Equal("Ungroup Layers", history.UndoName);
        Assert.Equal(new[] { "Below", "First", "Second" }, Names(document));
        var undone = history.Undo()!.Value.Document!;
        Assert.Contains(undone.Layers, layer => layer.ID == folder.ID && layer.IsGroup);
        Assert.Equal(folder.ID, undone.Layers.Single(layer => layer.ID == first.ID).ParentID);
        Assert.Equal(folder.ID, undone.Layers.Single(layer => layer.ID == second.ID).ParentID);

        var redone = history.Redo()!.Value;
        Assert.DoesNotContain(redone.Document!.Layers, layer => layer.ID == folder.ID);
        Assert.All(new[] { first.ID, second.ID }, id =>
            Assert.Null(redone.Document.Layers.Single(layer => layer.ID == id).ParentID));
        Assert.Equal(first.ID, redone.ActiveLayerID);
    }

    [Fact]
    public void UngroupingDiscardsTheFoldersOwnAppearanceAndMask()
    {
        var folder = Folder("Folder", null);
        folder.Opacity = 0.25;
        folder.BlendMode = LayerBlendMode.Multiply;
        folder.IsVisible = false;
        folder.Mask = Model.LayerMask.Solid(revealing: false);
        var child = Patch(SKColors.Red, 0, 0, 8, 8, "Child");
        child.ParentID = folder.ID;
        using var document = Doc(8, 8, folder, child);

        Assert.True(LayerPlacement.Ungroup(document, folder.ID));

        Assert.DoesNotContain(document.Layers, layer => layer.ID == folder.ID);
        Assert.Null(child.ParentID);
        // The child's own appearance is preserved; the removed folder's mask, blend, opacity and visibility
        // cannot silently be transferred to it.
        Assert.True(child.IsVisible);
        Assert.Equal(1, child.Opacity);
        Assert.Equal(LayerBlendMode.Normal, child.BlendMode);
        Assert.Null(child.Mask);
    }

    [Fact]
    public void UngroupingNestedFoldersOnlyReparentsTheDirectChildren()
    {
        var outer = Folder("Outer", null);
        var lower = Patch(SKColors.Blue, 0, 0, 8, 8, "Lower");
        lower.ParentID = outer.ID;
        var folder = Folder("Folder", outer.ID);
        var upper = Patch(SKColors.Red, 0, 0, 8, 8, "Upper");
        upper.ParentID = outer.ID;
        var first = Patch(SKColors.Green, 0, 0, 8, 8, "First");
        first.ParentID = folder.ID;
        var nested = Folder("Nested", folder.ID);
        var nestedChild = Patch(SKColors.Yellow, 0, 0, 8, 8, "Nested child");
        nestedChild.ParentID = nested.ID;
        var last = Patch(SKColors.White, 0, 0, 8, 8, "Last");
        last.ParentID = folder.ID;
        using var document = Doc(8, 8, outer, lower, folder, upper, first, nested, nestedChild, last);

        Assert.True(LayerPlacement.Ungroup(document, folder.ID));

        Assert.Equal(outer.ID, first.ParentID);
        Assert.Equal(outer.ID, nested.ParentID);
        Assert.Equal(outer.ID, last.ParentID);
        Assert.Equal(nested.ID, nestedChild.ParentID);
        Assert.Equal(new[] { "Lower", "First", "Nested", "Last", "Upper" },
            document.Layers.Where(layer => layer.ParentID == outer.ID).Select(layer => layer.Name));
        Assert.Equal(new[] { "Outer", "Lower", "First", "Nested", "Nested child", "Last", "Upper" },
            document.HierarchyEntries().Select(entry => entry.Layer.Name));
    }

    [Fact]
    public void UngroupingKeepsAnInternalClippingStackAndReleasesADetachedOne()
    {
        var baseLayer = Patch(SKColors.Blue, 0, 0, 8, 8, "Base");
        var folder = Folder("Folder", null);
        var clipped = Patch(SKColors.Green, 0, 0, 8, 8, "Clipped");
        clipped.ParentID = folder.ID;
        var clippedAgain = Patch(SKColors.Red, 0, 0, 8, 8, "Clipped again");
        clippedAgain.ParentID = folder.ID;
        clippedAgain.MaskSourceID = clipped.ID;
        using var document = Doc(8, 8, baseLayer, folder, clipped, clippedAgain);

        Assert.True(LayerPlacement.Ungroup(document, folder.ID));
        Assert.Equal(clipped.ID, clippedAgain.MaskSourceID);

        var spacer = Patch(SKColors.White, 0, 0, 8, 8, "Spacer");
        var detachedFolder = Folder("Detached", null);
        var detached = Patch(SKColors.Black, 0, 0, 8, 8, "Detached clip");
        detached.ParentID = detachedFolder.ID;
        // The format can represent this graph-valid cross-folder reference. Once the child is promoted
        // above a different sibling, it no longer forms a contiguous clipping stack and must be released.
        detached.MaskSourceID = baseLayer.ID;
        using var detachedDocument = Doc(8, 8, baseLayer, spacer, detachedFolder, detached);

        Assert.True(LayerPlacement.Ungroup(detachedDocument, detachedFolder.ID));
        Assert.Null(detached.MaskSourceID);
    }

    [Fact]
    public void UngroupingOnlyAcceptsAnExistingFolder()
    {
        var layer = Patch(SKColors.Blue, 0, 0, 8, 8, "Layer");
        using var document = Doc(8, 8, layer);

        Assert.False(LayerPlacement.Ungroup(document, layer.ID));
        Assert.False(LayerPlacement.Ungroup(document, Guid.NewGuid()));
        Assert.Equal(new[] { "Layer" }, Names(document));
    }

    [Fact]
    public void MovingALayerIntoAFolderLandsItOnTop()
    {
        var folder = Folder("Folder", null);
        var inside = Patch(SKColors.Blue, 0, 0, 8, 8, "Inside");
        inside.ParentID = folder.ID;
        var loose = Patch(SKColors.Red, 0, 0, 8, 8, "Loose");
        using var document = Doc(8, 8, folder, inside, loose);

        Assert.True(LayerPlacement.Place(document, loose.ID, folder.ID));
        Assert.Equal(folder.ID, loose.ParentID);
        Assert.True(document.Layers.IndexOf(loose) > document.Layers.IndexOf(inside));
        Assert.Equal(new[] { "Inside", "Loose" }, document.Layers.Where(l => l.ParentID == folder.ID).Select(l => l.Name));
    }

    [Fact]
    public void MovingALayerIntoItsOwnContentsIsRefused()
    {
        var folder = Folder("Folder", null);
        var inside = Patch(SKColors.Blue, 0, 0, 8, 8, "Inside");
        inside.ParentID = folder.ID;
        var loose = Patch(SKColors.Red, 0, 0, 8, 8, "Loose");
        using var document = Doc(8, 8, folder, inside, loose);

        // Into itself, or into anything inside it, and into something that is not a folder at all.
        Assert.False(LayerPlacement.CanPlace(document, folder.ID, folder.ID));
        Assert.False(LayerPlacement.CanPlace(document, folder.ID, inside.ID));
        Assert.False(LayerPlacement.CanPlace(document, inside.ID, loose.ID));
        Assert.False(LayerPlacement.Place(document, folder.ID, inside.ID));
        Assert.Equal(folder.ID, inside.ParentID);
        Assert.Equal(3, document.Layers.Count);
    }

    [Fact]
    public void MovingALayerOutOfAFolderPutsItAboveTheFolder()
    {
        var folder = Folder("Folder", null);
        var inside = Patch(SKColors.Blue, 0, 0, 8, 8, "Inside");
        inside.ParentID = folder.ID;
        var above = Patch(SKColors.Red, 0, 0, 8, 8, "Above");
        using var document = Doc(8, 8, folder, inside, above);

        Assert.True(LayerPlacement.MoveOutOfFolder(document, inside.ID));
        Assert.Null(inside.ParentID);
        // Just above the folder it came out of, so still beneath "Above".
        Assert.True(document.Layers.IndexOf(inside) > document.Layers.IndexOf(folder));
        Assert.True(document.Layers.IndexOf(inside) < document.Layers.IndexOf(above));
        // A layer at the top level has no folder to leave.
        Assert.False(LayerPlacement.MoveOutOfFolder(document, above.ID));
    }

    [Fact]
    public void MovingALayerBetweenClipsSettlesTheLinksTheWayTheMacBuildDoes()
    {
        var baseLayer = Patch(SKColors.Blue, 0, 0, 8, 8, "Base");
        var clipped = Patch(SKColors.Green, 0, 0, 8, 8, "Clipped");
        clipped.MaskSourceID = baseLayer.ID;
        var loose = Patch(SKColors.Red, 0, 0, 8, 8, "Loose");
        using var document = Doc(8, 8, baseLayer, clipped, loose);

        // Dropped in above the base but below the layer clipped to it, it joins that clip.
        Assert.True(LayerPlacement.Place(document, loose.ID, null, above: baseLayer.ID));
        Assert.Equal(baseLayer.ID, loose.MaskSourceID);
    }

    [Fact]
    public void AClipStopsWhenItsBaseIsNoLongerBeneathIt()
    {
        var baseLayer = Patch(SKColors.Blue, 0, 0, 8, 8, "Base");
        var clipped = Patch(SKColors.Green, 0, 0, 8, 8, "Clipped");
        clipped.MaskSourceID = baseLayer.ID;
        var other = Patch(SKColors.Red, 0, 0, 8, 8, "Other");
        using var document = Doc(8, 8, baseLayer, clipped, other);

        // Clipped moves to the top, so its base is no longer the layer beneath it and it stops clipping.
        Assert.True(LayerPlacement.Place(document, clipped.ID, null));
        Assert.Null(clipped.MaskSourceID);
        Assert.Equal(new[] { "Base", "Other", "Clipped" }, Names(document));
    }

    private static ImageLayer Folder(string name, Guid? parent) =>
        new(Guid.NewGuid(), null, new Model.LayerTransform(0, 0, 8, 8), name)
        {
            IsGroup = true,
            ParentID = parent,
        };
}

/// <summary>A layer's mask, and the clipping link that lets one layer supply coverage to another.</summary>
public class LayerMaskEditTests
{
    private static ImageLayer Patch(SKColor colour, string name)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(8, 8));
        bitmap.Erase(colour);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, name),
            new Model.LayerTransform(0, 0, 8, 8), name);
    }

    private static CanvasDocument Doc(params ImageLayer[] layers)
    {
        var document = new CanvasDocument(Guid.NewGuid(), 8, 8);
        document.Layers.AddRange(layers);
        return document;
    }

    [Fact]
    public void ARevealAllMaskLeavesTheLayerAsItWasAndAHideAllOneTakesItAway()
    {
        var layer = Patch(SKColors.Red, "Red");
        using var document = Doc(layer);

        Assert.True(LayerMaskEdits.Add(document, layer.ID, revealing: true));
        using (var shown = DocumentRenderer.Render(document))
        {
            Assert.Equal(255, shown.GetPixel(4, 4).Alpha);
        }
        Assert.False(LayerMaskEdits.Add(document, layer.ID, revealing: false));

        Assert.True(LayerMaskEdits.Remove(document, layer.ID));
        Assert.True(LayerMaskEdits.Add(document, layer.ID, revealing: false));
        using var hidden = DocumentRenderer.Render(document);
        Assert.Equal(0, hidden.GetPixel(4, 4).Alpha);
    }

    [Fact]
    public void AMaskCanBeTurnedOffAndBackOn()
    {
        var layer = Patch(SKColors.Red, "Red");
        using var document = Doc(layer);
        LayerMaskEdits.Add(document, layer.ID, revealing: false);

        Assert.True(LayerMaskEdits.SetEnabled(document, layer.ID, false));
        using (var shown = DocumentRenderer.Render(document))
        {
            Assert.Equal(255, shown.GetPixel(4, 4).Alpha);
        }
        Assert.True(LayerMaskEdits.SetEnabled(document, layer.ID, true));
        using var hidden = DocumentRenderer.Render(document);
        Assert.Equal(0, hidden.GetPixel(4, 4).Alpha);
        // Setting the state it is already in is not an edit.
        Assert.False(LayerMaskEdits.SetEnabled(document, layer.ID, true));
    }

    [Fact]
    public void AMaskCanBeUnlinkedFromItsLayer()
    {
        var layer = Patch(SKColors.Red, "Red");
        using var document = Doc(layer);
        LayerMaskEdits.Add(document, layer.ID, revealing: true);
        Assert.True(layer.Mask!.IsLinked);

        Assert.True(LayerMaskEdits.SetLinked(document, layer.ID, false));
        Assert.False(layer.Mask!.IsLinked);
        Assert.False(LayerMaskEdits.SetLinked(document, layer.ID, false));
    }

    [Fact]
    public void AMaskOnAFolderClipsEverythingInsideIt()
    {
        var bitmap = new SKBitmap(Bitmaps.MaskInfo(1, 1));
        bitmap.Erase(new SKColor(0, 0, 0));
        var folder = new ImageLayer(Guid.NewGuid(), null, new Model.LayerTransform(0, 0, 8, 8), "Folder")
        {
            IsGroup = true,
            Mask = Model.LayerMask.AssetFrom(bitmap),
        };
        var inside = Patch(SKColors.Red, "Inside");
        inside.ParentID = folder.ID;
        using var document = Doc(folder, inside);

        using var hidden = DocumentRenderer.Render(document);
        Assert.Equal(0, hidden.GetPixel(4, 4).Alpha);
    }

    [Fact]
    public void ClippingTakesTheCoverageOfTheLayerBeneathAndReleasesItAgain()
    {
        var baseLayer = Patch(SKColors.Blue, "Base");
        var above = Patch(SKColors.Red, "Above");
        using var document = Doc(baseLayer, above);

        Assert.True(LayerMaskEdits.CanToggle(document, above.ID));
        Assert.True(LayerMaskEdits.Toggle(document, above.ID));
        Assert.Equal(baseLayer.ID, above.MaskSourceID);
        // Toggling again releases it.
        Assert.True(LayerMaskEdits.Toggle(document, above.ID));
        Assert.Null(above.MaskSourceID);
    }

    [Fact]
    public void ClippingRunsOnToTheBaseTheLayerBeneathAlreadyUses()
    {
        var baseLayer = Patch(SKColors.Blue, "Base");
        var clipped = Patch(SKColors.Green, "Clipped");
        clipped.MaskSourceID = baseLayer.ID;
        var top = Patch(SKColors.Red, "Top");
        using var document = Doc(baseLayer, clipped, top);

        // The layer beneath is already clipped, so the new one joins the same base rather than clipping to
        // the layer that is itself clipped.
        Assert.True(LayerMaskEdits.Toggle(document, top.ID));
        Assert.Equal(baseLayer.ID, top.MaskSourceID);
    }

    [Fact]
    public void TheBottomLayerHasNothingToClipTo()
    {
        var baseLayer = Patch(SKColors.Blue, "Base");
        var above = Patch(SKColors.Red, "Above");
        using var document = Doc(baseLayer, above);

        Assert.False(LayerMaskEdits.CanToggle(document, baseLayer.ID));
        Assert.False(LayerMaskEdits.Toggle(document, baseLayer.ID));
    }

    [Fact]
    public void AFolderIsNeverClipped()
    {
        var baseLayer = Patch(SKColors.Blue, "Base");
        var folder = new ImageLayer(Guid.NewGuid(), null, new Model.LayerTransform(0, 0, 8, 8), "Folder")
        {
            IsGroup = true,
        };
        using var document = Doc(baseLayer, folder);

        // A folder holds no pixels, so it can neither supply coverage nor be clipped.
        Assert.False(LayerMaskEdits.CanToggle(document, folder.ID));
        Assert.False(LayerMaskEdits.Link(document, folder.ID, baseLayer.ID));
        Assert.False(LayerMaskEdits.Link(document, baseLayer.ID, folder.ID));
    }

    [Fact]
    public void AClipCannotBeMadeIntoACircle()
    {
        var one = Patch(SKColors.Blue, "One");
        var two = Patch(SKColors.Green, "Two");
        using var document = Doc(one, two);

        Assert.True(LayerMaskEdits.Link(document, one.ID, two.ID));
        // Two cannot supply to One while One supplies to Two.
        Assert.False(LayerMaskEdits.CanLink(document, two.ID, one.ID));
        Assert.False(LayerMaskEdits.Link(document, two.ID, one.ID));
        Assert.Null(one.MaskSourceID);
    }

    [Fact]
    public void ReleaseLetsGoOfTheRunAboveTheLayerItIsAskedAbout()
    {
        var baseLayer = Patch(SKColors.Blue, "Base");
        var first = Patch(SKColors.Green, "First");
        first.MaskSourceID = baseLayer.ID;
        var second = Patch(SKColors.Red, "Second");
        second.MaskSourceID = baseLayer.ID;
        using var document = Doc(baseLayer, first, second);

        // The run above the released layer shares its clip, so it lets go too. The base is not clipped at
        // all, so there is nothing on it to release.
        Assert.False(LayerMaskEdits.Release(document, baseLayer.ID));
        Assert.True(LayerMaskEdits.Release(document, first.ID));
        Assert.Null(first.MaskSourceID);
        Assert.Null(second.MaskSourceID);
    }

    [Fact]
    public void ReleasingAClippedLayerLeavesTheOnesBeneathItAlone()
    {
        var baseLayer = Patch(SKColors.Blue, "Base");
        var first = Patch(SKColors.Green, "First");
        first.MaskSourceID = baseLayer.ID;
        var second = Patch(SKColors.Red, "Second");
        second.MaskSourceID = baseLayer.ID;
        using var document = Doc(baseLayer, first, second);

        // A release runs up the stack, not down it: what is above the released layer went with it, since
        // it shared the same base, and what is below keeps its clip.
        Assert.True(LayerMaskEdits.Release(document, second.ID));
        Assert.Null(second.MaskSourceID);
        Assert.Equal(baseLayer.ID, first.MaskSourceID);
    }
}
