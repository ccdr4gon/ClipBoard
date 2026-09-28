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
    private readonly Func<uint, bool>? _ignoreSequence;
    private readonly CancellationTokenSource _shutdown = new();
    private Task _pendingChanges = Task.CompletedTask;
    private readonly List<ClipboardChangedEventArgs> _queuedChanges = new();

    public event EventHandler<ClipboardChangedEventArgs>? ClipboardChanged;

    public ClipboardMonitor(Window window, Func<uint, bool>? ignoreSequence = null)
    {
        _window = window;
        _ignoreSequence = ignoreSequence;
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
        if (_disposed) return;
        try
        {
            // 在读取时识别自身写入；按时间忽略会漏掉用户紧接着复制的新内容。
            if (_ignoreSequence?.Invoke(HistoryStore.GetClipboardSequenceNumber()) == true) return;
            if (Clipboard.ContainsImage())
            {
                var gifUrl = GifHelper.ReadGifUrlFromClipboard();
                var img = Clipboard.GetImage();
                if (img != null)
                {
                    img = FixAlphaChannel(img);
                    img.Freeze();
                    QueueChange(new ClipboardChangedEventArgs
                    {
                        Kind = ClipKind.Image,
                        Image = img,
                    }, gifUrl);
                    return;
                }
            }
            if (Clipboard.ContainsFileDropList())
            {
                StringCollection sc = Clipboard.GetFileDropList();
                var arr = new string[sc.Count];
                sc.CopyTo(arr, 0);
                QueueChange(new ClipboardChangedEventArgs
                {
                    Kind = ClipKind.Files,
                    Files = arr,
                });
                return;
            }
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                QueueChange(new ClipboardChangedEventArgs
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

    private void QueueChange(ClipboardChangedEventArgs change, string? gifUrl = null)
    {
        _queuedChanges.Add(change);
        _pendingChanges = DeliverChangeAsync(_pendingChanges, change, gifUrl);
    }

    private async Task DeliverChangeAsync(Task previous, ClipboardChangedEventArgs change, string? gifUrl)
    {
        try
        {
            var captured = change;
            // 网络读取不占用窗口线程；按捕获顺序交付，慢 GIF 不会覆盖更晚的历史顺序。
            var download = gifUrl == null ? Task.FromResult<byte[]?>(null)
                : GifHelper.DownloadGifAsync(gifUrl, _shutdown.Token);
            await previous;
            var bytes = await download;
            if (_disposed) return;
            if (bytes != null)
                change = new ClipboardChangedEventArgs { Kind = ClipKind.Gif, GifBytes = bytes };
            await _window.Dispatcher.InvokeAsync(() =>
            {
                if (_disposed) return;
                _queuedChanges.Remove(captured);
                ClipboardChanged?.Invoke(this, change);
            });
        }
        catch (Exception)
        {
            // 单次读取失败不阻断后续剪贴板事件。
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
        _shutdown.Cancel();
        _shutdown.Dispose();
        if (_hwnd != IntPtr.Zero)
            RemoveClipboardFormatListener(_hwnd);
        _source?.RemoveHook(WndProc);
        // 退出时保存已捕获的内容；尚未下完的 GIF 使用捕获时的静态图回退。
        foreach (var change in _queuedChanges)
        {
            try { ClipboardChanged?.Invoke(this, change); }
            catch (Exception) { /* 单个条目保存失败不阻止其他条目及退出刷新。 */ }
        }
        _queuedChanges.Clear();
    }
}
