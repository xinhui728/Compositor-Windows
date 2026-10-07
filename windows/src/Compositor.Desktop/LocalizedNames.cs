using Compositor.Core.Document;
using Compositor.Core.Format;

namespace Compositor.Desktop;

/// <summary>
/// Display names for stable Core values. Their resource keys deliberately follow enum member names rather than
/// manifest spellings, so translating the interface can never alter project serialization.
/// </summary>
internal static class LocalizedNames
{
    public static string Tool(Tool tool) => L.Get($"Tool.{tool}");

    public static string BlendMode(LayerBlendMode mode) => L.Get($"BlendMode.{mode}");

    public static string Adjustment(AdjustmentKind kind) => L.Get($"Adjustment.{kind}");

    public static string Filter(FilterKind kind) => L.Get($"Filter.{kind}");

    public static string Effect(EffectKind kind) => L.Get($"Effect.{kind}");

    public static string Shape(ShapeKind kind) => L.Get($"Shape.{kind}");

    public static string Gradient(GradientShape shape) => L.Get($"Gradient.{shape}");

    public static string Healing(HealingMode mode) => L.Get($"Healing.{mode}");

    public static string ColorRange(ColorRange range) => L.Get($"ColorRange.{range}");

    public static string LevelsChannel(LevelsChannel channel) => L.Get($"LevelsChannel.{channel}");

    public static string TextAlignment(TextAlignment alignment) => L.Get($"TextAlignment.{alignment}");

    public static string Dither(DitherStyle style) => style switch
    {
        DitherStyle.Atkinson => L.Get("Dither.Style.Atkinson"),
        DitherStyle.FloydSteinberg => L.Get("Dither.Style.FloydSteinberg"),
        DitherStyle.Bayer2 => L.Get("Dither.Style.Bayer2"),
        DitherStyle.Bayer4 => L.Get("Dither.Style.Bayer4"),
        DitherStyle.Bayer8 => L.Get("Dither.Style.Bayer8"),
        DitherStyle.Dots => L.Get("Dither.Style.HalftoneDots"),
        DitherStyle.Lines => L.Get("Dither.Style.HalftoneLines"),
        DitherStyle.Diamonds => L.Get("Dither.Style.HalftoneDiamonds"),
        DitherStyle.Patterns => L.Get("Dither.Style.MacPatterns"),
        _ => L.Get("Dither.Style.Ascii"),
    };

    public static string GuideAxis(GuideAxis axis) => L.Get($"GuideAxis.{axis}");

    /// <summary>
    /// History entries deliberately retain their stable English operation names so undo data never becomes
    /// culture-dependent. This maps only the display copy at the Desktop boundary.
    /// </summary>
    public static string HistoryOperation(string operation) => L.Get($"History.{operation}");
}
