using Compositor.Core.Document;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>Deterministic planning coverage for large native-resolution brush samples; no timing assertion or giant bitmap is needed.</summary>
public sealed class NativeResolutionBrushBudgetTests
{
    [Fact]
    public void BlurRoiCoarsensToTheExplicitWorkingPixelBudget()
    {
        // This represents a 10,000-square native ROI around a small canvas stroke. The budget is injected
        // directly so CI does not allocate the source or depend on machine memory/timing.
        var region = SKRectI.Create(-500, -500, 10_000, 10_000);
        var fit = NativeBrushSampleLimits.Fit(region, maximumPixels: 16_384);
        var width = (int)Math.Ceiling(region.Width * fit);
        var height = (int)Math.Ceiling(region.Height * fit);

        Assert.Equal(128, width);
        Assert.Equal(128, height);
        Assert.True((long)width * height <= 16_384);
    }
}
