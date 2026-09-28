using System.Windows;
using System.Windows.Media;

namespace ClipBoard.Views;

/// <summary>按宽度等比显示图片，只在固定预览窗口中裁切纵向溢出。</summary>
public sealed class HistoryThumbnail : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(HistoryThumbnail),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Source is not { Width: > 0, Height: > 0 } source || ActualWidth <= 0 || ActualHeight <= 0) return;
        double height = ActualWidth * source.Height / source.Width;
        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        drawingContext.DrawImage(source, new Rect(0, (ActualHeight - height) / 2, ActualWidth, height));
        drawingContext.Pop();
    }
}
