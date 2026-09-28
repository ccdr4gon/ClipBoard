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
            if (previous?.Kind == ClipKind.Text && previous.Text == content.Text) return;
            item = new() { Kind = ClipKind.Text, Text = content.Text };
        }
        else return;
        InsertExisting(item);
    }
    public void InsertExisting(ClipItem item)
    {
        Items.Insert(0, item);
        while (Items.Count > 200) Remove(Items[^1]);
        favorites.Save();
    }
    public bool Detach(ClipItem item) => Items.Remove(item);
    public void Remove(ClipItem item)
    {
        if (!Items.Remove(item)) return;
        favorites.DeleteUnusedBlob(item);
        favorites.Save();
    }
    public void Clear() { foreach (var item in Items.ToArray()) Remove(item); }
}
