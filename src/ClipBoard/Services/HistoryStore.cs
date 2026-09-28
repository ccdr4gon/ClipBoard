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

    private uint? _selfUpdateSequence;
    private bool _isWritingClipboard;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    public bool IsSelfUpdate(uint sequence) => _isWritingClipboard || _selfUpdateSequence == sequence;

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
        // 去重比较完整像素；只看首行会漏掉顶部相同、正文不同的截图。
        if (Items.Count > 0 && Items[0].Kind == ClipKind.Image
            && sig != 0 && Items[0].PixelW == w && Items[0].PixelH == h && Items[0].ImageSig == sig) return;

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

    // 统一像素格式后逐块读取全部像素，内存只需一小块缓冲区。
    private static long ComputeImageSig(BitmapSource img)
    {
        try
        {
            if (img.Format != System.Windows.Media.PixelFormats.Bgra32)
                img = new FormatConvertedBitmap(img, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int stride = checked(img.PixelWidth * 4);
            int rowsPerBlock = Math.Min(img.PixelHeight, Math.Max(1, 65536 / stride));
            var buf = new byte[checked(stride * rowsPerBlock)];
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            for (int y = 0; y < img.PixelHeight; y += rowsPerBlock)
            {
                int rows = Math.Min(rowsPerBlock, img.PixelHeight - y);
                img.CopyPixels(new Int32Rect(0, y, img.PixelWidth, rows), buf, stride, 0);
                hash.AppendData(buf, 0, stride * rows);
            }
            return System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(hash.GetHashAndReset());
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
            && Items[0].GifBytes!.AsSpan().SequenceEqual(gifBytes)) return;
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
        if (!Items.Remove(item)) return;
        CleanupBlob(item);
        TriggerSave();
    }

    // 顶置只转移条目，不删除它仍需使用的图片文件，也不保存中间状态。
    internal bool Detach(ClipItem item) => Items.Remove(item);

    private void CleanupBlob(ClipItem item)
    {
        _favorites.DeleteUnusedBlob(item);
    }

    public void PromoteToTop(ClipItem item)
    {
        int idx = Items.IndexOf(item);
        if (idx > 0)
        {
            Items.Move(idx, 0);
            TriggerSave();
        }
    }

    public bool CopyToClipboard(ClipItem item)
    {
        _isWritingClipboard = true;
        try { return TryCopyToClipboard(item); }
        finally { _isWritingClipboard = false; }
    }

    private bool TryCopyToClipboard(ClipItem item)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                switch (item.Kind)
                {
                    case ClipKind.Text:
                        if (item.Text == null) return false;
                        Clipboard.SetDataObject(item.Text, true);
                        break;
                    case ClipKind.Image:
                        // 复制要保真：优先从磁盘读全分辨率原图，缩略图仅作回退。
                        var full = (_persistence != null && !string.IsNullOrEmpty(item.ImageBlobName))
                            ? _persistence.LoadImageBlob(item.ImageBlobName) : null;
                        var toCopy = full ?? item.Image;
                        if (toCopy == null) return false;
                        Clipboard.SetImage(toCopy);
                        break;
                    case ClipKind.Gif:
                        var gifBytes = item.GifBytes ?? (_persistence != null && item.GifBlobName is { } gifName ? _persistence.LoadGifBlob(gifName) : null);
                        if (gifBytes is { Length: > 0 })
                        {
                            var tempPath = WriteGifTemp(gifBytes);
                            var sc = new StringCollection();
                            sc.Add(tempPath);
                            var data = new DataObject();
                            data.SetFileDropList(sc);
                            Clipboard.SetDataObject(data, true);
                        }
                        else return false;
                        break;
                    case ClipKind.VideoSticker:
                    case ClipKind.VectorSticker:
                        if (_persistence == null || item.Sticker == null) return false;
                        var assetPath = _persistence.GetBlobPath(item.Sticker.WorkingBlobName);
                        if (!System.IO.File.Exists(assetPath)) return false;
                        Clipboard.SetFileDropList(new StringCollection { assetPath });
                        break;
                    case ClipKind.Files:
                        if (item.FilePaths is { Length: > 0 })
                        {
                            var sc = new StringCollection();
                            sc.AddRange(item.FilePaths);
                            var data = new DataObject();
                            data.SetFileDropList(sc);
                            Clipboard.SetDataObject(data, true);
                        }
                        else return false;
                        break;
                    default: return false;
                }
                _selfUpdateSequence = GetClipboardSequenceNumber();
                return true;
            }
            catch
            {
                System.Threading.Thread.Sleep(30);
            }
        }
        return false;
    }

}
