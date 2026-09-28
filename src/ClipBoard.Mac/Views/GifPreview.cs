using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace ClipBoard.Views;

internal sealed class GifPreview : Image, IDisposable
{
    private readonly SKCodec _codec;
    private readonly SKBitmap _pixels;
    private readonly SKCodecFrameInfo[] _frames;
    private readonly DispatcherTimer _timer = new();
    private Bitmap? _display;
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
        var result = _codec.GetPixels(_pixels.Info, _pixels.GetPixels(), new SKCodecOptions(_frame, _frame == 0 ? -1 : _frame - 1));
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) { _timer.Stop(); return; }
        using var encoded = _pixels.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = encoded.AsStream();
        var previous = _display;
        Source = _display = new Bitmap(stream);
        previous?.Dispose();
        _timer.Interval = TimeSpan.FromMilliseconds(_frames.Length > 0 ? Math.Max(20, _frames[_frame].Duration) : 100);
        _frame = (_frame + 1) % Math.Max(1, _frames.Length);
    }
    public void Dispose() { _timer.Stop(); Source = null; _display?.Dispose(); _pixels.Dispose(); _codec.Dispose(); }
}
