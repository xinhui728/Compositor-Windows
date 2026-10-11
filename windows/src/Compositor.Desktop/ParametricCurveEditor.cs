using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Compositor.Core.Document;

namespace Compositor.Desktop;

/// <summary>
/// Interactive graph for Camera Raw's parametric curve. This is deliberately separate from <see
/// cref="CurveEditor"/>: point curves use the persisted-format-compatible point representation, while the
/// four tonal regions and three dividers are transient Camera Raw state only.
/// </summary>
internal sealed class ParametricCurveEditor : Control
{
    private const double DividerBand = 18;
    private const double DividerGrab = 2;

    private static readonly IBrush Backdrop = Skin.CurveGround;
    private static readonly Pen GridPen = new() { Brush = Skin.CurveGrid, Thickness = 1 };
    private static readonly Pen CurvePen = new() { Brush = Brushes.White, Thickness = 2 };
    private static readonly Pen DividerPen = new() { Brush = Skin.AccentBrush, Thickness = 1 };

    private CameraRawCurveSettings _curve = new();
    private Drag _drag;
    private double _startAmount;
    private double _startY;

    private enum Drag
    {
        None,
        Shadows,
        Darks,
        Lights,
        Highlights,
        ShadowDivider,
        DarkDivider,
        LightDivider,
    }

    public ParametricCurveEditor()
    {
        Focusable = true;
    }

    /// <summary>Raised after a region amount or tonal divider changes.</summary>
    public event Action? Changed;

    /// <summary>The transient Camera Raw parametric curve being edited.</summary>
    public CameraRawCurveSettings Curve
    {
        get => _curve;
        set
        {
            _curve = value ?? new CameraRawCurveSettings();
            _drag = Drag.None;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var box = new Rect(Bounds.Size);
        context.FillRectangle(Backdrop, box);
        for (var quarter = 1; quarter < 4; quarter++)
        {
            var x = Bounds.Width * quarter / 4;
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, Bounds.Height));
            var y = Bounds.Height * quarter / 4;
            context.DrawLine(GridPen, new Point(0, y), new Point(Bounds.Width, y));
        }

        Point? before = null;
        var samples = _curve.ParametricSamples(256);
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var at = At(sample / (double)(samples.Length - 1), samples[sample]);
            if (before is { } previous) context.DrawLine(CurvePen, previous, at);
            before = at;
        }

        // Dividers live along the lower graph band, as in Camera Raw. The band itself is still part of the
        // graph so dragging above it adjusts a tonal region and dragging within it moves a divider.
        foreach (var divider in new[] { _curve.ShadowSplit, _curve.DarkSplit, _curve.LightSplit })
        {
            var x = divider / 100 * Bounds.Width;
            context.DrawLine(DividerPen, new Point(x, Math.Max(0, Bounds.Height - DividerBand)),
                new Point(x, Bounds.Height));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var at = e.GetPosition(this);
        _drag = at.Y > Bounds.Height - DividerBand ? NearestDivider(at.X) : RegionAt(at.X);
        _startAmount = Amount(_drag);
        _startY = at.Y;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == Drag.None) return;
        var at = e.GetPosition(this);
        if (IsDivider(_drag)) MoveDivider(at.X);
        else MoveRegion(at.Y);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = Drag.None;
    }

    private void EndDrag(IPointer pointer)
    {
        _drag = Drag.None;
        pointer.Capture(null);
    }

    private Drag RegionAt(double x)
    {
        var tone = Bounds.Width <= 0 ? 0 : x / Bounds.Width * 100;
        if (tone < _curve.ShadowSplit) return Drag.Shadows;
        if (tone < _curve.DarkSplit) return Drag.Darks;
        if (tone < _curve.LightSplit) return Drag.Lights;
        return Drag.Highlights;
    }

    private Drag NearestDivider(double x)
    {
        var tone = Bounds.Width <= 0 ? 0 : x / Bounds.Width * 100;
        var dividers = new[] { _curve.ShadowSplit, _curve.DarkSplit, _curve.LightSplit };
        var nearest = dividers.Select((value, index) => (value, index))
            .MinBy(item => Math.Abs(item.value - tone)).index;
        return nearest switch
        {
            0 => Drag.ShadowDivider,
            1 => Drag.DarkDivider,
            _ => Drag.LightDivider,
        };
    }

    private void MoveDivider(double x)
    {
        var value = Math.Clamp(Bounds.Width <= 0 ? 50 : x / Bounds.Width * 100, DividerGrab, 98);
        switch (_drag)
        {
            case Drag.ShadowDivider:
                _curve.ShadowSplit = Math.Min(value, _curve.DarkSplit - 2);
                break;
            case Drag.DarkDivider:
                _curve.DarkSplit = Math.Min(_curve.LightSplit - 2, Math.Max(_curve.ShadowSplit + 2, value));
                break;
            case Drag.LightDivider:
                _curve.LightSplit = Math.Max(value, _curve.DarkSplit + 2);
                break;
        }
        Announce();
    }

    private void MoveRegion(double y)
    {
        var change = Bounds.Height <= 0 ? 0 : (y - _startY) / Bounds.Height * 200;
        var amount = Math.Round(Math.Clamp(_startAmount - change, -100, 100), MidpointRounding.AwayFromZero);
        switch (_drag)
        {
            case Drag.Shadows: _curve.Shadows = amount; break;
            case Drag.Darks: _curve.Darks = amount; break;
            case Drag.Lights: _curve.Lights = amount; break;
            case Drag.Highlights: _curve.Highlights = amount; break;
            default: return;
        }
        Announce();
    }

    private double Amount(Drag drag) => drag switch
    {
        Drag.Shadows => _curve.Shadows,
        Drag.Darks => _curve.Darks,
        Drag.Lights => _curve.Lights,
        Drag.Highlights => _curve.Highlights,
        _ => 0,
    };

    private static bool IsDivider(Drag drag) => drag is Drag.ShadowDivider or Drag.DarkDivider or Drag.LightDivider;

    private Point At(double input, double output) => new(
        input * Bounds.Width,
        Bounds.Height - output * Bounds.Height);

    private void Announce()
    {
        InvalidateVisual();
        Changed?.Invoke();
    }
}
