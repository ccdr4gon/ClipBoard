using System.Numerics;
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
    public RichContent? Rich { get; init; }
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
            // Clipboard.Get* 每次都重新 OleGetClipboard（失败时在窗口线程上最多 10×100ms 重试）。
            // 同一次通知共用一个数据对象：确实要读时才取，取失败不缓存，各处的重试和异常走向不变。
            System.Windows.IDataObject? data = null;
            bool fetched = false;
            System.Windows.IDataObject? Data()
            {
                if (!fetched) { data = Clipboard.GetDataObject(); fetched = true; }
                return data;
            }
            if (Clipboard.ContainsImage())
            {
                var gifUrl = GifHelper.ReadGifUrlFromClipboard(Data);
                var img = Data()?.GetData(DataFormats.Bitmap, true) as BitmapSource; // 与 Clipboard.GetImage() 相同
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
                var files = Data()?.GetData(DataFormats.FileDrop, true) as string[]; // 与 Clipboard.GetFileDropList() 相同
                var arr = files == null ? new string[0] : (string[])files.Clone();
                QueueChange(new ClipboardChangedEventArgs
                {
                    Kind = ClipKind.Files,
                    Files = arr,
                });
                return;
            }
            if (Clipboard.ContainsText())
            {
                var textData = Data();
                // 与 Clipboard.GetText() 相同：强制转换，空值记为空串。
                string text = (string?)textData?.GetData(DataFormats.UnicodeText, false) ?? string.Empty;
                QueueChange(new ClipboardChangedEventArgs
                {
                    Kind = ClipKind.Text,
                    Text = text,
                    Rich = ReadRichContent(textData),
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

    // 格式读取失败只影响“保留格式”粘贴，不能丢掉纯文本。
    private static RichContent? ReadRichContent(System.Windows.IDataObject? data)
    {
        string? html = null, rtf = null;
        try { if (Clipboard.ContainsData(DataFormats.Html)) html = RichText.FromCfHtml(data?.GetData(DataFormats.Html, false) as string); }
        catch (Exception) { }
        try { if (Clipboard.ContainsData(DataFormats.Rtf)) rtf = data?.GetData(DataFormats.Rtf, false) as string; }
        catch (Exception) { }
        return RichText.Create(html, rtf);
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

        int width = src.PixelWidth, height = src.PixelHeight;
        int stride = width * 4;
        int size = checked(stride * height); // 超大图仍在这里抛出、被上层丢弃，与以前一致
        // 按约 64KB 的条带读取（不进大对象堆）：有透明度的图读完第一条就返回，
        // 只有确实要修正时才申请整图缓冲区，且放在非托管内存里，用完立即归还系统。
        int rowsPerBlock = Math.Min(height, Math.Max(1, 65536 / stride));
        var strip = new byte[stride * rowsPerBlock];
        IntPtr pixels = IntPtr.Zero;
        try
        {
            for (int y = 0; y < height; y += rowsPerBlock)
            {
                int rows = Math.Min(rowsPerBlock, height - y);
                var block = strip.AsSpan(0, stride * rows);
                src.CopyPixels(new Int32Rect(0, y, width, rows), strip, stride, 0);
                if (HasAlpha(block)) return src;
                SetOpaque(block);
                if (pixels == IntPtr.Zero) pixels = Marshal.AllocHGlobal(size);
                Marshal.Copy(strip, 0, pixels + y * stride, block.Length);
            }
            // Create 会把像素复制进自己的位图，之后即可释放缓冲区。
            var fixed_ = BitmapSource.Create(width, height, src.DpiX, src.DpiY,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, size, stride);
            fixed_.Freeze();
            return fixed_;
        }
        finally
        {
            if (pixels != IntPtr.Zero) Marshal.FreeHGlobal(pixels);
        }
    }

    // 每个像素按小端 uint 读取（B,G,R,A），透明度是最高字节。
    private static bool HasAlpha(ReadOnlySpan<byte> bgra)
    {
        var px = MemoryMarshal.Cast<byte, uint>(bgra);
        var vectors = MemoryMarshal.Cast<uint, Vector<uint>>(px);
        var any = Vector<uint>.Zero;
        foreach (var v in vectors) any |= v;
        if ((any & new Vector<uint>(0xFF000000u)) != Vector<uint>.Zero) return true;
        for (int i = vectors.Length * Vector<uint>.Count; i < px.Length; i++)
            if ((px[i] & 0xFF000000u) != 0) return true;
        return false;
    }

    // 只在透明度全为 0 时调用：或上 0xFF000000 即把透明度置为 255，颜色不变。
    private static void SetOpaque(Span<byte> bgra)
    {
        var px = MemoryMarshal.Cast<byte, uint>(bgra);
        var vectors = MemoryMarshal.Cast<uint, Vector<uint>>(px);
        var alpha = new Vector<uint>(0xFF000000u);
        for (int i = 0; i < vectors.Length; i++) vectors[i] |= alpha;
        for (int i = vectors.Length * Vector<uint>.Count; i < px.Length; i++) px[i] |= 0xFF000000u;
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
