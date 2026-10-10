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

    public void AddText(string text, RichContent? rich = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        text = RichText.PlainText(text, rich?.Html);
        if (Items.Count > 0 && Items[0].Kind == ClipKind.Text && Items[0].Text == text)
        {
            // 同一段文字再次复制时带上了格式：补到原条目，不新增。
            if (rich != null && Items[0].RichBlobName == null && _persistence != null)
            {
                Items[0].RichBlobName = _persistence.SaveRichBlob(rich);
                TriggerSave();
            }
            return;
        }
        var item = new ClipItem { Kind = ClipKind.Text, Text = text };
        if (rich != null && _persistence != null) item.RichBlobName = _persistence.SaveRichBlob(rich);
        InsertNew(item);
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
    // 签名只在内存里和最近一条比较、从不保存，用非加密的 XXH64 即可，比 SHA-256 快数倍。
    private static long ComputeImageSig(BitmapSource img)
    {
        try
        {
            if (img.Format != System.Windows.Media.PixelFormats.Bgra32)
                img = new FormatConvertedBitmap(img, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int stride = checked(img.PixelWidth * 4);
            int rowsPerBlock = Math.Min(img.PixelHeight, Math.Max(1, 65536 / stride));
            var buf = new byte[checked(stride * rowsPerBlock)];
            var hash = new XxHash64();
            for (int y = 0; y < img.PixelHeight; y += rowsPerBlock)
            {
                int rows = Math.Min(rowsPerBlock, img.PixelHeight - y);
                img.CopyPixels(new Int32Rect(0, y, img.PixelWidth, rows), buf, stride, 0);
                hash.Append(buf.AsSpan(0, stride * rows));
            }
            return unchecked((long)hash.Finish());
        }
        catch { return 0; }
    }

    /// <summary>流式 XXH64（种子 0）：分块追加与一次性计算的结果相同。</summary>
    internal sealed class XxHash64
    {
        private const ulong P1 = 0x9E3779B185EBCA87, P2 = 0xC2B2AE3D27D4EB4F, P3 = 0x165667B19E3779F9,
            P4 = 0x85EBCA77C2B2AE63, P5 = 0x27D4EB2F165667C5;
        private ulong _v1 = unchecked(P1 + P2), _v2 = P2, _v3, _v4 = unchecked(0 - P1);
        private readonly byte[] _tail = new byte[32];
        private int _tailLength;
        private ulong _total;

        public void Append(ReadOnlySpan<byte> data)
        {
            _total += (ulong)data.Length;
            if (_tailLength > 0)
            {
                int take = Math.Min(32 - _tailLength, data.Length);
                data[..take].CopyTo(_tail.AsSpan(_tailLength));
                _tailLength += take;
                data = data[take..];
                if (_tailLength < 32) return;
                Stripes(_tail);
                _tailLength = 0;
            }
            int whole = data.Length & ~31;
            if (whole > 0) Stripes(data[..whole]);
            data[whole..].CopyTo(_tail);
            _tailLength = data.Length - whole;
        }

        // 每 32 字节为一组，四个 64 位小端字。
        private void Stripes(ReadOnlySpan<byte> data)
        {
            var lanes = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(data);
            ulong v1 = _v1, v2 = _v2, v3 = _v3, v4 = _v4;
            for (int i = 0; i + 3 < lanes.Length; i += 4)
            {
                v1 = Round(v1, lanes[i]);
                v2 = Round(v2, lanes[i + 1]);
                v3 = Round(v3, lanes[i + 2]);
                v4 = Round(v4, lanes[i + 3]);
            }
            _v1 = v1; _v2 = v2; _v3 = v3; _v4 = v4;
        }

        public ulong Finish()
        {
            ulong h;
            if (_total >= 32)
            {
                h = Rotl(_v1, 1) + Rotl(_v2, 7) + Rotl(_v3, 12) + Rotl(_v4, 18);
                h = Merge(h, _v1); h = Merge(h, _v2); h = Merge(h, _v3); h = Merge(h, _v4);
            }
            else h = P5;
            h += _total;
            var rest = new ReadOnlySpan<byte>(_tail, 0, _tailLength);
            for (; rest.Length >= 8; rest = rest[8..])
                h = Rotl(h ^ Round(0, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(rest)), 27) * P1 + P4;
            if (rest.Length >= 4)
            {
                h = Rotl(h ^ System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(rest) * P1, 23) * P2 + P3;
                rest = rest[4..];
            }
            foreach (byte b in rest) h = Rotl(h ^ b * P5, 11) * P1;
            h ^= h >> 33; h *= P2; h ^= h >> 29; h *= P3; h ^= h >> 32;
            return h;
        }

        private static ulong Rotl(ulong x, int r) => System.Numerics.BitOperations.RotateLeft(x, r);
        private static ulong Round(ulong acc, ulong lane) => Rotl(acc + lane * P2, 31) * P1;
        private static ulong Merge(ulong h, ulong v) => (h ^ Round(0, v)) * P1 + P4;
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

    /// <param name="plainText">只写纯文本，丢弃复制时带来的 HTML / RTF 格式。</param>
    public bool CopyToClipboard(ClipItem item, bool plainText = false)
    {
        _isWritingClipboard = true;
        try { return TryCopyToClipboard(item, plainText); }
        finally { _isWritingClipboard = false; }
    }

    /// <summary>纯文本加上原有的 HTML / RTF，让 Word、邮件、笔记等保留列表编号和格式。</summary>
    internal static DataObject CreateTextData(string text, RichContent? rich)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        if (rich?.Html is { Length: > 0 } html) data.SetData(DataFormats.Html, RichText.ToCfHtml(html));
        if (rich?.Rtf is { Length: > 0 } rtf) data.SetData(DataFormats.Rtf, rtf);
        return data;
    }

    private bool TryCopyToClipboard(ClipItem item, bool plainText)
    {
        var rich = !plainText && item.HasRichText ? _persistence?.LoadRichBlob(item.RichBlobName) : null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                switch (item.Kind)
                {
                    case ClipKind.Text:
                        if (item.Text == null) return false;
                        Clipboard.SetDataObject(CreateTextData(item.Text, rich), true);
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
