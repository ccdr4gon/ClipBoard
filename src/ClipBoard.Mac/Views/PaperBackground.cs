using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClipBoard.Views;

internal sealed class PaperBackground : Control
{
    public override void Render(DrawingContext context)
    {
        var background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#F8F5EE"), 0), new GradientStop(Color.Parse("#F5F1E7"), 1) } };
        context.FillRectangle(background, new Rect(Bounds.Size));
        void Glow(Rect rect, string color)
        {
            var brush = new RadialGradientBrush { GradientStops = { new GradientStop(Color.Parse(color), 0), new GradientStop(Color.Parse(color), .25), new GradientStop(Colors.Transparent, 1) } };
            context.FillRectangle(brush, rect);
        }
        Glow(new Rect(-145, -145, 450, 450), "#5986D1A8");
        Glow(new Rect(475, 275, 430, 430), "#52E3C58C");
        Glow(new Rect(105, 185, 390, 390), "#38C9B5E2");
    }
}
