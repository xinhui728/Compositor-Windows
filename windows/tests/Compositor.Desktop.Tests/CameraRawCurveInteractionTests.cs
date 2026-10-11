using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Compositor.Core.Document;
using Compositor.Core.Format;
using SkiaSharp;

namespace Compositor.Desktop.Tests;

/// <summary>Headless interaction contracts for the Camera Raw v1.4.5 curve controls.</summary>
public sealed class CameraRawCurveInteractionTests
{
    [Fact]
    public void CameraRawPanelStartsOnParametricAndKeepsBothCurvePagesIndependent()
    {
        DesktopHeadless.EnsureInitialized();
        var panel = new CameraRawPanel(new CameraRawSettings(), SKColors.Orange);

        Assert.Equal(0, panel.CurvePage);
        Assert.NotNull(panel.ParametricCurveEditor);
        Assert.NotNull(panel.PointCurveEditor);
        Assert.Equal(CameraRawCurveSettings.MaximumPointCount, panel.PointCurveEditor.MaximumPointCount);

        panel.Move("Parametric darks", -51);
        Assert.Equal(-51, panel.Current().ParametricCurve.Darks);
        panel.SelectCurvePage(1);
        Assert.Equal(1, panel.CurvePage);
        panel.SelectCurvePage(0);
        Assert.Equal(-51, panel.Current().ParametricCurve.Darks);
    }

    [Fact]
    public void CameraRawCurvePanelPreviewsChangesAndApplyRaisesOneCommitEvent()
    {
        DesktopHeadless.EnsureInitialized();
        var panel = new CameraRawPanel(new CameraRawSettings(), SKColors.Orange);
        var previews = 0;
        CameraRawSettings? applied = null;
        panel.Preview = (_, _, _, _) => previews++;
        panel.Applied += settings => applied = settings;

        panel.Move("Parametric lights", 59);
        panel.Apply();

        Assert.True(previews > 0);
        Assert.NotNull(applied);
        Assert.Equal(59, applied!.ParametricCurve.Lights);
    }

    [Fact]
    public void CameraRawCurvePanelCancelKeepsThePreviewTransient()
    {
        DesktopHeadless.EnsureInitialized();
        var panel = new CameraRawPanel(new CameraRawSettings(), SKColors.Orange);
        var previews = 0;
        var applied = 0;
        var cancelled = 0;
        panel.Preview = (_, _, _, _) => previews++;
        panel.Applied += _ => applied++;
        panel.Cancelled += () => cancelled++;

        panel.Move("Parametric darks", -51);
        panel.Cancel();

        Assert.True(previews > 0);
        Assert.Equal(0, applied);
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void PointCurveDragSelectsAHandleAndDoubleClickRemovesIt()
    {
        DesktopHeadless.EnsureInitialized();
        var curves = new CurvesSettings();
        curves.Channels[0] =
        [
            new CurvePoint { X = 0, Y = 0 },
            new CurvePoint { X = 128, Y = 128 },
            new CurvePoint { X = 255, Y = 255 },
        ];
        var editor = new CurveEditor { Curves = curves, Width = 100, Height = 100 };
        var window = new Window { Width = 100, Height = 100, Content = editor };
        window.Show();
        try
        {
            window.MouseDown(new Point(50, 50), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseMove(new Point(50, 20), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(50, 20), MouseButton.Left, RawInputModifiers.None);

            Assert.Equal(1, editor.SelectedPointIndex);
            Assert.True(editor.Curves.Channels[0][1].Y > 180);

            // Avalonia Headless keeps click counts for consecutive presses at the same location, exercising the
            // production double-click path rather than a test-only point deletion helper.
            window.MouseDown(new Point(50, 20), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(50, 20), MouseButton.Left, RawInputModifiers.None);
            window.MouseDown(new Point(50, 20), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(50, 20), MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, editor.Curves.Channels[0].Count);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void PointCurveAddsRespectsItsCapAndKeepsTheWindowsRightClickRemoval()
    {
        DesktopHeadless.EnsureInitialized();
        var editor = new CurveEditor
        {
            Curves = new CurvesSettings(),
            MaximumPointCount = 3,
            MinimumPointSpacing = 3,
            EndpointInset = 3,
            Width = 100,
            Height = 100,
        };
        var window = new Window { Width = 100, Height = 100, Content = editor };
        window.Show();
        try
        {
            // This is away from the straight-line curve, so it adds the first interior point and selects it.
            window.MouseDown(new Point(50, 70), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(50, 70), MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(3, editor.Curves.Channels[0].Count);
            Assert.Equal(1, editor.SelectedPointIndex);

            // A second empty-space click cannot grow beyond the configured cap.
            window.MouseDown(new Point(25, 75), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(25, 75), MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(3, editor.Curves.Channels[0].Count);

            // Keep the established Windows shortcut alongside upstream's double-click removal.
            window.MouseDown(new Point(50, 50), MouseButton.Right, RawInputModifiers.RightMouseButton);
            window.MouseUp(new Point(50, 50), MouseButton.Right, RawInputModifiers.None);
            Assert.Equal(2, editor.Curves.Channels[0].Count);

            editor.Channel = 1;
            Assert.Equal(-1, editor.SelectedPointIndex);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void ParametricGraphDragsRegionsAndDividersWithinTheirConstraints()
    {
        DesktopHeadless.EnsureInitialized();
        var curve = new CameraRawCurveSettings();
        var editor = new ParametricCurveEditor { Curve = curve, Width = 200, Height = 160 };
        var window = new Window { Width = 200, Height = 160, Content = editor };
        window.Show();
        try
        {
            // x=50 is in Darks at the default dividers; dragging up lifts that region.
            window.MouseDown(new Point(50, 80), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseMove(new Point(50, 30), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(50, 30), MouseButton.Left, RawInputModifiers.None);
            Assert.True(curve.Darks > 0);

            // The lower band chooses its nearest divider. Move the middle divider right while retaining at
            // least two percentage points on each side.
            window.MouseDown(new Point(100, 150), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseMove(new Point(130, 150), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(130, 150), MouseButton.Left, RawInputModifiers.None);
            Assert.InRange(curve.DarkSplit, 64, 66);
            Assert.True(curve.DarkSplit >= curve.ShadowSplit + 2);
            Assert.True(curve.LightSplit >= curve.DarkSplit + 2);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void ParametricGraphAllowsTheReachableDividerEndpoints()
    {
        DesktopHeadless.EnsureInitialized();
        var curve = new CameraRawCurveSettings { Darks = -51 };
        var editor = new ParametricCurveEditor { Curve = curve, Width = 200, Height = 160 };
        var window = new Window { Width = 200, Height = 160, Content = editor };
        window.Show();
        try
        {
            // Move the dividers in the only order that makes the high endpoint range reachable: light, dark,
            // then shadow.  These are the exact 2...98 gesture constraints from the macOS implementation.
            window.MouseDown(new Point(150, 150), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseMove(new Point(196, 150), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(196, 150), MouseButton.Left, RawInputModifiers.None);
            window.MouseDown(new Point(100, 150), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseMove(new Point(192, 150), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(192, 150), MouseButton.Left, RawInputModifiers.None);
            window.MouseDown(new Point(50, 150), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseMove(new Point(188, 150), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(188, 150), MouseButton.Left, RawInputModifiers.None);

            Assert.Equal(94, curve.ShadowSplit);
            Assert.Equal(96, curve.DarkSplit);
            Assert.Equal(98, curve.LightSplit);
            Assert.True(new CameraRawSettings { ParametricCurve = curve }.IsValid);
        }
        finally
        {
            window.Close();
        }
    }

    private static class DesktopHeadless
    {
        public static void EnsureInitialized() => DesktopTestHost.EnsureInitialized();
    }
}
