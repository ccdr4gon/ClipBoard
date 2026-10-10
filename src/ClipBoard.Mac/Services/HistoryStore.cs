using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Avalonia.Media.Imaging;
using ClipBoard.Models;

namespace ClipBoard.Services;

public sealed class HistoryStore(FavoritesStore favorites)
{
    private PersistenceService _persistence = null!;
    public ObservableCollection<ClipItem> Items { get; } = [];
    public void SetPersistence(PersistenceService persistence) => _persistence = persistence;
    public void Load(IEnumerable<ClipItem> items)
    {
        foreach (var item in items.Take(200))
        {
            if (item.ImageBlobName != null) item.Image = _persistence.LoadImageThumbnail(item.ImageBlobName);
            else if (item.Kind == ClipKind.Gif && item.GifBlobName != null)
            {
                item.GifBytes = _persistence.LoadGifBlob(item.GifBlobName);
                try { item.Image = item.GifSource; } catch { /* Keep the original blob even if decoding fails. */ }
            }
            Items.Add(item);
        }
    }
    public void Capture(ClipboardContent content)
    {
        var previous = Items.FirstOrDefault();
        ClipItem item;
        if (content.Files is { Length: > 0 } files)
        {
            if (previous?.Kind == ClipKind.Files && previous.FilePaths?.SequenceEqual(files) == true) return;
            item = new() { Kind = ClipKind.Files, FilePaths = files.ToArray() };
        }
        else if (content.Image is { Length: > 0 } bytes)
        {
            if (IsDuplicateImage(bytes, content.IsGif, previous, out var kind, out long sig)) return;
            item = CreateImageItem(bytes, content.IsGif, kind, sig);
        }
        else if (!string.IsNullOrEmpty(content.Text))
        {
            string text = RichText.PlainText(content.Text, content.Rich?.Html);
            if (previous?.Kind == ClipKind.Text && previous.Text == text)
            {
                // 同一段文字再次复制时带上了格式：补到原条目，不新增。
                if (content.Rich != null && previous.RichBlobName == null)
                {
                    previous.RichBlobName = _persistence.SaveRichBlob(content.Rich);
                    favorites.Save();
                }
                return;
            }
            item = new() { Kind = ClipKind.Text, Text = text };
            if (content.Rich != null) item.RichBlobName = _persistence.SaveRichBlob(content.Rich);
        }
        else return;
        InsertExisting(item);
    }
    private static bool IsDuplicateImage(byte[] bytes, bool gif, ClipItem? previous, out ClipKind kind, out long sig)
    {
        sig = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(bytes));
        kind = gif ? ClipKind.Gif : ClipKind.Image;
        return previous?.Kind == kind && previous.ImageSig == sig;
    }
    // 解码、另存原图和生成缩略图；不碰任何集合，可以在后台线程运行。
    private ClipItem CreateImageItem(byte[] bytes, bool gif, ClipKind kind, long sig)
    {
        using var stream = new MemoryStream(bytes);
        using var image = new Bitmap(stream);
        var item = new ClipItem { Kind = kind, PixelW = image.PixelSize.Width, PixelH = image.PixelSize.Height, ImageSig = sig };
        if (gif) item.GifBlobName = _persistence.SaveGifBlob(bytes);
        item.ImageBlobName = _persistence.SaveImageBlob(image);
        item.Image = _persistence.LoadImageThumbnail(item.ImageBlobName);
        return item;
    }

    // 轮询剪贴板用（只在界面线程调用）：图片的解码、PNG 编码和缩略图改在后台线程完成，界面线程不再卡住几百毫秒
    // （固定显示的面板、菜单栏仍可响应）。去重判断和插入历史仍在界面线程，结果与同步的 Capture 相同；文字和文件直接同步处理。
    // 图片处理期间 App.Poll 不再读取剪贴板、隐藏的面板等它进入历史再打开，与原先界面线程被占用时一样；
    // 万一在处理期间又调用，会排在它之后，顺序不变。
    private Task<ClipItem>? _pendingImage;
    private bool _pendingInserted;
    private Task _capturing = Task.CompletedTask;
    public bool IsCapturing => !_capturing.IsCompleted;
    /// <summary>正在处理的图片进入历史（或处理失败）后完成。</summary>
    public Task WhenIdle => _capturing;
    public Task CaptureAsync(ClipboardContent content) => _capturing = IsCapturing ? After(_capturing, content) : CaptureCore(content);
    private async Task After(Task previous, ClipboardContent content)
    {
        try { await previous; } catch { /* 已由那次调用的调用方报告 */ }
        await CaptureCore(content);
    }
    private Task CaptureCore(ClipboardContent content)
    {
        if (content.Files is { Length: > 0 } || content.Image is not { Length: > 0 } bytes) { Capture(content); return Task.CompletedTask; }
        bool gif = content.IsGif;
        if (IsDuplicateImage(bytes, gif, Items.FirstOrDefault(), out var kind, out long sig)) return Task.CompletedTask;
        var work = Task.Run(() => CreateImageItem(bytes, gif, kind, sig));
        _pendingImage = work; _pendingInserted = false;
        return InsertWhenDone(work);
    }
    private async Task InsertWhenDone(Task<ClipItem> work)
    {
        try
        {
            var item = await work; // 回到界面线程
            if (_pendingInserted) return;
            _pendingInserted = true;
            InsertExisting(item);
        }
        finally { if (_pendingImage == work) _pendingImage = null; }
    }
    /// <summary>退出时（界面线程）把仍在后台处理的图片补进历史：原图文件已经写入，不能没有条目。</summary>
    public void DrainForExit()
    {
        if (_pendingImage is not { } work || _pendingInserted) return;
        try
        {
            if (!work.Wait(TimeSpan.FromSeconds(10))) return;
            _pendingInserted = true;
            InsertExisting(work.Result);
        }
        catch (AggregateException) { }
    }
    public void InsertExisting(ClipItem item)
    {
        Items.Insert(0, item);
        // 淘汰最旧的条目只清理文件，最后统一保存一次（每次保存都要复制整份数据）。
        while (Items.Count > 200)
        {
            var old = Items[^1];
            Items.RemoveAt(Items.Count - 1);
            favorites.DeleteUnusedBlob(old);
        }
        favorites.Save();
    }
    public bool Detach(ClipItem item) => Items.Remove(item);
    public void Remove(ClipItem item)
    {
        if (!Items.Remove(item)) return;
        favorites.DeleteUnusedBlob(item);
        favorites.Save();
    }
    public void Clear()
    {
        // 顺序和逐条通知不变，只是清空后保存一次，而不是每删一条保存一次。
        bool removed = false;
        foreach (var item in Items.ToArray())
        {
            if (!Items.Remove(item)) continue;
            favorites.DeleteUnusedBlob(item);
            removed = true;
        }
        if (removed) favorites.Save();
    }
}
