using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;

namespace ClipBoard.Views;

internal sealed class GifPreview : Image, IDisposable
{
    private readonly SKCodec _codec;
    private readonly SKBitmap _pixels;
    private readonly SKCodecFrameInfo[] _frames;
    private readonly DispatcherTimer _timer = new();
    private WriteableBitmap? _display;
    private int _frame;
    public GifPreview(string path)
    {
        _codec = SKCodec.Create(path) ?? throw new IOException("无法读取动画。");
        _pixels = new SKBitmap(_codec.Info.Width, _codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _frames = _codec.FrameInfo;
        Stretch = Stretch.Uniform;
        _timer.Tick += (_, _) => Draw();
        Draw();
        if (_frames.Length > 1) _timer.Start();
    }
    private void Draw()
    {
        // _pixels 保留上一帧，解码下一帧时要用到它，所以仍先解码到这里。
        var result = _codec.GetPixels(_pixels.Info, _pixels.GetPixels(), new SKCodecOptions(_frame, _frame == 0 ? -1 : _frame - 1));
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) { _timer.Stop(); return; }
        // 直接把像素拷进同一张 WriteableBitmap（同为 BGRA 预乘、96 DPI）。原先每帧编码成 PNG 再解码成新位图。
        _display ??= new WriteableBitmap(new PixelSize(_pixels.Width, _pixels.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var target = _display.Lock())
        using (var source = _pixels.PeekPixels())
            source.ReadPixels(new SKImageInfo(target.Size.Width, target.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul), target.Address, target.RowBytes);
        if (!ReferenceEquals(Source, _display)) Source = _display; // 第一帧解码成功时才显示，与原先一致
        else InvalidateVisual();
        _timer.Interval = TimeSpan.FromMilliseconds(_frames.Length > 0 ? Math.Max(20, _frames[_frame].Duration) : 100);
        _frame = (_frame + 1) % Math.Max(1, _frames.Length);
    }
    public void Dispose() { _timer.Stop(); Source = null; _display?.Dispose(); _pixels.Dispose(); _codec.Dispose(); }
}
