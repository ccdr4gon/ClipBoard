using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ClipBoard.Models;

namespace ClipBoard.Services;

public class ClipboardChangedEventArgs : EventArgs
{
    public ClipKind Kind { get; init; }
    public string? Text { get; init; }
    public BitmapSource? Image { get; init; }
    public string[]? Files { get; init; }
    public byte[]? GifBytes { get; init; }
}

public class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly Window _window;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _disposed;

    public event EventHandler<ClipboardChangedEventArgs>? ClipboardChanged;

    public ClipboardMonitor(Window window)
    {
        _window = window;
        new WindowInteropHelper(window).EnsureHandle();
        Attach();
    }

    private void Attach()
    {
        var helper = new WindowInteropHelper(_window);
        _hwnd = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        AddClipboardFormatListener(_hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            ReadClipboardWithRetry(0);
        }
        return IntPtr.Zero;
    }

    private void ReadClipboardWithRetry(int attempt)
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                if (GifHelper.TryReadGifFromClipboard(out var gifBytes) && gifBytes != null)
                {
                    ClipboardChanged?.Invoke(this, new ClipboardChangedEventArgs
                    {
                        Kind = ClipKind.Gif,
                        GifBytes = gifBytes,
                    });
                    return;
                }

                var img = Clipboard.GetImage();
                if (img != null)
                {
                    img = FixAlphaChannel(img);
                    img.Freeze();
                    ClipboardChanged?.Invoke(this, new ClipboardChangedEventArgs
                    {
                        Kind = ClipKind.Image,
                        Image = img,
                    });
                    return;
                }
            }
            if (Clipboard.ContainsFileDropList())
            {
                StringCollection sc = Clipboard.GetFileDropList();
                var arr = new string[sc.Count];
                sc.CopyTo(arr, 0);
                ClipboardChanged?.Invoke(this, new ClipboardChangedEventArgs
                {
                    Kind = ClipKind.Files,
                    Files = arr,
                });
                return;
            }
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                ClipboardChanged?.Invoke(this, new ClipboardChangedEventArgs
                {
                    Kind = ClipKind.Text,
                    Text = text,
                });
            }
        }
        catch (COMException) when (attempt < 3)
        {
            _window.Dispatcher.BeginInvoke(new Action(() => ReadClipboardWithRetry(attempt + 1)),
                System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception)
        {
            // Swallow: next update will try again.
        }
    }

    private static BitmapSource FixAlphaChannel(BitmapSource src)
    {
        if (src.Format != System.Windows.Media.PixelFormats.Bgra32
            && src.Format != System.Windows.Media.PixelFormats.Pbgra32)
            return src;

        int stride = src.PixelWidth * 4;
        var pixels = new byte[stride * src.PixelHeight];
        src.CopyPixels(pixels, stride, 0);

        bool allAlphaZero = true;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) { allAlphaZero = false; break; }
        }

        if (!allAlphaZero) return src;

        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;

        var fixed_ = BitmapSource.Create(src.PixelWidth, src.PixelHeight, src.DpiX, src.DpiY,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        fixed_.Freeze();
        return fixed_;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
            RemoveClipboardFormatListener(_hwnd);
        _source?.RemoveHook(WndProc);
    }
}
