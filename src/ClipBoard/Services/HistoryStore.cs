using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media.Imaging;
using ClipBoard.Models;

namespace ClipBoard.Services;

public class HistoryStore
{
    private readonly FavoritesStore _favorites;
    private PersistenceService? _persistence;
    private const int MaxItems = 200;

    public ObservableCollection<ClipItem> Items { get; } = new();

    private DateTime _selfUpdateUntilUtc = DateTime.MinValue;
    public bool IsSelfUpdate => DateTime.UtcNow < _selfUpdateUntilUtc;
    public void ClearSelfUpdate() { /* time-window based, no-op */ }

    public HistoryStore(FavoritesStore favorites) { _favorites = favorites; }

    public void SetPersistence(PersistenceService persistence) { _persistence = persistence; }

    private void TriggerSave() => _favorites.Save();

    public void AddText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Items.Count > 0 && Items[0].Kind == ClipKind.Text && Items[0].Text == text) return;
        InsertNew(new ClipItem { Kind = ClipKind.Text, Text = text });
    }

    public void AddImage(BitmapSource image)
    {
        int w = image.PixelWidth, h = image.PixelHeight;
        long sig = ComputeImageSig(image);
        // 去重：与最近一条比较原始尺寸 + 首行签名（内存里的 Image 现在是缩略图，故不能直接比像素）。
        if (Items.Count > 0 && Items[0].Kind == ClipKind.Image
            && Items[0].PixelW == w && Items[0].PixelH == h && Items[0].ImageSig == sig) return;

        var item = new ClipItem { Kind = ClipKind.Image, PixelW = w, PixelH = h, ImageSig = sig };
        if (_persistence != null)
        {
            // 全分辨率只落盘；内存只保留降采样缩略图，避免长时间累积大量大图导致 OOM。
            item.ImageBlobName = _persistence.SaveImageBlob(image);
            item.Image = _persistence.LoadImageThumbnail(item.ImageBlobName) ?? image;
        }
        else
        {
            item.Image = image;
        }
        InsertNew(item);
    }

    // 由全分辨率位图算出去重签名（首行像素 + 尺寸的 FNV-1a 哈希）。
    private static long ComputeImageSig(BitmapSource img)
    {
        try
        {
            int stride = img.PixelWidth * (img.Format.BitsPerPixel + 7) / 8;
            var buf = new byte[stride];
            img.CopyPixels(new System.Windows.Int32Rect(0, 0, img.PixelWidth, 1), buf, stride, 0);
            unchecked
            {
                long hsh = 1469598103934665603L;
                hsh = (hsh ^ img.PixelWidth) * 1099511628211L;
                hsh = (hsh ^ img.PixelHeight) * 1099511628211L;
                foreach (var b in buf) hsh = (hsh ^ b) * 1099511628211L;
                return hsh;
            }
        }
        catch { return 0; }
    }

    public static string WriteGifTemp(byte[] gifBytes)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClipBoard");
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, Guid.NewGuid().ToString("N") + ".gif");
        System.IO.File.WriteAllBytes(path, gifBytes);
        return path;
    }

    public void AddGif(byte[] gifBytes)
    {
        if (gifBytes.Length == 0) return;
        if (Items.Count > 0 && Items[0].Kind == ClipKind.Gif && Items[0].GifBytes != null
            && Items[0].GifBytes!.Length == gifBytes.Length) return;
        var item = new ClipItem { Kind = ClipKind.Gif, GifBytes = gifBytes };
        if (_persistence != null)
            item.GifBlobName = _persistence.SaveGifBlob(gifBytes);
        InsertNew(item);
    }

    public void AddFiles(IEnumerable<string> files)
    {
        var arr = files?.ToArray();
        if (arr is null || arr.Length == 0) return;
        if (Items.Count > 0 && Items[0].Kind == ClipKind.Files && Items[0].FilePaths != null
            && Items[0].FilePaths!.SequenceEqual(arr)) return;
        InsertNew(new ClipItem { Kind = ClipKind.Files, FilePaths = arr });
    }

    private void InsertNew(ClipItem item)
    {
        Items.Insert(0, item);
        TrimExcess();
        TriggerSave();
    }

    public void InsertExisting(ClipItem item)
    {
        Items.Insert(0, item);
        TrimExcess();
    }

    private void TrimExcess()
    {
        while (Items.Count > MaxItems)
        {
            var old = Items[Items.Count - 1];
            Items.RemoveAt(Items.Count - 1);
            CleanupBlob(old);
        }
    }

    public void Remove(ClipItem item)
    {
        Items.Remove(item);
        CleanupBlob(item);
        TriggerSave();
    }

    private void CleanupBlob(ClipItem item)
    {
        if (_persistence == null) return;
        if (item.Kind == ClipKind.Image && !string.IsNullOrEmpty(item.ImageBlobName))
            _persistence.DeleteImageBlob(item.ImageBlobName);
        else if (item.Kind == ClipKind.Gif && !string.IsNullOrEmpty(item.GifBlobName))
            _persistence.DeleteGifBlob(item.GifBlobName);
    }

    public void PromoteToTop(ClipItem item)
    {
        int idx = Items.IndexOf(item);
        if (idx > 0) Items.Move(idx, 0);
    }

    public void CopyToClipboard(ClipItem item)
    {
        _selfUpdateUntilUtc = DateTime.UtcNow.AddMilliseconds(600);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                switch (item.Kind)
                {
                    case ClipKind.Text:
                        if (item.Text != null) Clipboard.SetDataObject(item.Text, false);
                        break;
                    case ClipKind.Image:
                        // 复制要保真：优先从磁盘读全分辨率原图，缩略图仅作回退。
                        var full = (_persistence != null && !string.IsNullOrEmpty(item.ImageBlobName))
                            ? _persistence.LoadImageBlob(item.ImageBlobName) : null;
                        var toCopy = full ?? item.Image;
                        if (toCopy != null) Clipboard.SetImage(toCopy);
                        break;
                    case ClipKind.Gif:
                        if (item.GifBytes is { Length: > 0 })
                        {
                            var tempPath = WriteGifTemp(item.GifBytes);
                            var sc = new StringCollection();
                            sc.Add(tempPath);
                            var data = new DataObject();
                            data.SetFileDropList(sc);
                            Clipboard.SetDataObject(data, false);
                        }
                        break;
                    case ClipKind.Files:
                        if (item.FilePaths is { Length: > 0 })
                        {
                            var sc = new StringCollection();
                            sc.AddRange(item.FilePaths);
                            var data = new DataObject();
                            data.SetFileDropList(sc);
                            Clipboard.SetDataObject(data, false);
                        }
                        break;
                }
                return;
            }
            catch
            {
                System.Threading.Thread.Sleep(30);
            }
        }
    }

}
