using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Desktop.Tests;

/// <summary>
/// Contracts around the small, UI-independent parts of document closing.  Window interaction itself is
/// exercised by the desktop self-checks; these tests keep the decisions and the pointer-settlement edge case
/// deterministic without a native window or file picker.
/// </summary>
public sealed class DocumentLifecycleTests
{
    [Fact]
    public async Task CleanDocumentClosesWithoutAskingOrSaving()
    {
        var asked = 0;
        var saved = 0;

        var allowed = await DocumentCloseFlow.ConfirmAsync(
            isModified: false,
            ask: () =>
            {
                asked++;
                return Task.FromResult(UnsavedChangesChoice.Cancel);
            },
            save: () =>
            {
                saved++;
                return Task.FromResult(false);
            });

        Assert.True(allowed);
        Assert.Equal(0, asked);
        Assert.Equal(0, saved);
    }

    [Fact]
    public async Task DiscardClosesChangedDocumentWithoutSaving()
    {
        var saved = 0;

        var allowed = await DocumentCloseFlow.ConfirmAsync(
            isModified: true,
            ask: () => Task.FromResult(UnsavedChangesChoice.Discard),
            save: () =>
            {
                saved++;
                return Task.FromResult(true);
            });

        Assert.True(allowed);
        Assert.Equal(0, saved);
    }

    [Fact]
    public async Task CancelKeepsChangedDocumentOpenWithoutSaving()
    {
        var saved = 0;

        var allowed = await DocumentCloseFlow.ConfirmAsync(
            isModified: true,
            ask: () => Task.FromResult(UnsavedChangesChoice.Cancel),
            save: () =>
            {
                saved++;
                return Task.FromResult(true);
            });

        Assert.False(allowed);
        Assert.Equal(0, saved);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveChoiceOnlyAllowsCloseAfterSuccessfulSave(bool saveSucceeds)
    {
        var saves = 0;

        var allowed = await DocumentCloseFlow.ConfirmAsync(
            isModified: true,
            ask: () => Task.FromResult(UnsavedChangesChoice.Save),
            save: () =>
            {
                saves++;
                return Task.FromResult(saveSucceeds);
            });

        Assert.Equal(saveSucceeds, allowed);
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task MultipleTabsAreConfirmedActiveFirstAndCancelStopsTheRemainingTabs()
    {
        var seen = new List<string>();

        var allowed = await DocumentCloseFlow.ConfirmAllAsync(
            ["active", "left", "right"],
            tab =>
            {
                seen.Add(tab);
                return Task.FromResult(tab != "left");
            });

        Assert.False(allowed);
        Assert.Equal(["active", "left"], seen);
    }

    [Fact]
    public void InvalidatingPreviewWorkMakesAnAlreadyQueuedCallbackHarmless()
    {
        var generation = new PreviewCallbackGeneration();
        var queued = generation.Schedule();

        Assert.True(generation.IsCurrent(queued));
        generation.Invalidate();

        Assert.False(generation.IsCurrent(queued));
        var replacement = generation.Schedule();
        Assert.True(generation.IsCurrent(replacement));
        Assert.NotEqual(queued, replacement);
    }

    [Fact]
    public void ClosingTheNonModalColorPickerCancelsExactlyOnce()
    {
        DesktopHeadless.EnsureInitialized();
        var owner = new Window { Width = 100, Height = 100 };
        var picker = new ColorPickerDialog("Colour", (0.2, 0.4, 0.6));
        var cancellations = 0;
        picker.Cancelled += () => cancellations++;
        owner.Show();
        picker.Show(owner);
        try
        {
            picker.Close();
            Dispatcher.UIThread.RunJobs();
            picker.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, cancellations);
        }
        finally
        {
            picker.Close();
            owner.Close();
        }
    }

    [Fact]
    public void ClosingAnEmptyEditorUsesTheAsyncBridgeAndClosesOnlyOnce()
    {
        DesktopHeadless.EnsureInitialized();
        var window = new MainWindow { Width = 320, Height = 240 };
        var closed = 0;
        window.Closed += (_, _) => closed++;
        window.Show();
        try
        {
            // The first call is synchronously cancelled by MainWindow. Its clean-tab lifecycle finishes and
            // permits exactly one second Close; a repeated user request while that is happening cannot dispose
            // the blank tab twice.
            window.Close();
            window.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, closed);
        }
        finally
        {
            if (closed == 0) window.Close();
        }
    }

    [Fact]
    public void HistoryExposesAnOpenInteractiveTransactionUntilTheOutermostEnd()
    {
        var history = new DocumentHistory();

        Assert.False(history.HasOpenTransaction);
        history.Begin("outer", document: null, activeLayer: null);
        Assert.True(history.HasOpenTransaction);
        history.Begin("inner", document: null, activeLayer: null);
        Assert.True(history.HasOpenTransaction);

        history.End(document: null, activeLayer: null);
        Assert.True(history.HasOpenTransaction);
        history.End(document: null, activeLayer: null);

        Assert.False(history.HasOpenTransaction);
    }

    [Fact]
    public void FinishingGradientDragTwiceRaisesOneCompletionOnly()
    {
        DesktopHeadless.EnsureInitialized();
        var canvas = new CanvasView();
        var completions = new List<(SKPoint Start, SKPoint End)>();
        canvas.GradientFinished = (start, end) => completions.Add((start, end));
        canvas.GradientEnabled = true;
        var window = new Window { Width = 100, Height = 100, Content = canvas };
        window.Show();
        try
        {
            // A lifecycle settlement can arrive between pointer down and pointer up.  Drive that real input
            // path on Avalonia's headless platform, then ensure the late release cannot apply a second gradient
            // (and therefore cannot make a second undo step in the window's callback).
            window.MouseDown(new Point(4, 8), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseMove(new Point(20, 32), RawInputModifiers.LeftMouseButton);

            Assert.True(canvas.FinishGradientDrag());
            Assert.False(canvas.FinishGradientDrag());
            window.MouseUp(new Point(20, 32), MouseButton.Left, RawInputModifiers.None);

            var completion = Assert.Single(completions);
            Assert.Equal(new SKPoint(4, 8), completion.Start);
            Assert.Equal(new SKPoint(20, 32), completion.End);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Starts the same headless Avalonia stack the desktop self-check uses, once for this test assembly.</summary>
    private static class DesktopHeadless
    {
        private static readonly object Gate = new();
        private static bool _initialized;

        public static void EnsureInitialized()
        {
            lock (Gate)
            {
                if (_initialized) return;
                AppBuilder.Configure<DesktopApp>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                    .UseSkia()
                    .WithInterFont()
                    .SetupWithoutStarting();
                _initialized = true;
            }
        }
    }
}
