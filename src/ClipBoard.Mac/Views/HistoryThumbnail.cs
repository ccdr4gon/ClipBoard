using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClipBoard.Views;

public sealed class HistoryThumbnail : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty = AvaloniaProperty.Register<HistoryThumbnail, IImage?>(nameof(Source));
    static HistoryThumbnail() => AffectsRender<HistoryThumbnail>(SourceProperty);
    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Source is not { Size.Width: > 0, Size.Height: > 0 } source || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        double height = Bounds.Width * source.Size.Height / source.Size.Width;
        using (context.PushClip(new Rect(Bounds.Size)))
            context.DrawImage(source, new Rect(0, (Bounds.Height - height) / 2, Bounds.Width, height));
    }
}
