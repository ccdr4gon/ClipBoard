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
            long sig = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(bytes));
            var kind = content.IsGif ? ClipKind.Gif : ClipKind.Image;
            if (previous?.Kind == kind && previous.ImageSig == sig) return;
            using var stream = new MemoryStream(bytes);
            using var image = new Bitmap(stream);
            item = new() { Kind = kind, PixelW = image.PixelSize.Width, PixelH = image.PixelSize.Height, ImageSig = sig };
            if (content.IsGif) item.GifBlobName = _persistence.SaveGifBlob(bytes);
            item.ImageBlobName = _persistence.SaveImageBlob(image);
            item.Image = _persistence.LoadImageThumbnail(item.ImageBlobName);
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
