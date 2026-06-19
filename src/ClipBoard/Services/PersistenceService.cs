using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using ClipBoard.Models;

namespace ClipBoard.Services;

public class PersistenceService
{
    private readonly string _root;
    private readonly string _dataFile;
    private readonly string _blobsDir;
    private readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private System.Threading.Timer? _debounce;
    private readonly object _lock = new();
    private PersistedData? _pendingSnapshot;

    public PersistenceService()
    {
        _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipBoard");
        _dataFile = Path.Combine(_root, "data.json");
        _blobsDir = Path.Combine(_root, "blobs");
        Directory.CreateDirectory(_blobsDir);
    }

    public string BlobsDir => _blobsDir;

    public PersistedData Load()
    {
        try
        {
            if (!File.Exists(_dataFile)) return new PersistedData();
            var json = File.ReadAllText(_dataFile);
            if (string.IsNullOrWhiteSpace(json)) return new PersistedData();
            return JsonSerializer.Deserialize<PersistedData>(json, _jsonOpts) ?? new PersistedData();
        }
        catch
        {
            return new PersistedData();
        }
    }

    public void SaveDebounced(PersistedData snapshot)
    {
        lock (_lock)
        {
            _pendingSnapshot = snapshot;
            _debounce?.Dispose();
            _debounce = new System.Threading.Timer(_ => WritePending(), null, 500, System.Threading.Timeout.Infinite);
        }
    }

    public void FlushSync()
    {
        WritePending();
    }

    private void WritePending()
    {
        PersistedData? snap;
        lock (_lock)
        {
            snap = _pendingSnapshot;
            _pendingSnapshot = null;
            _debounce?.Dispose();
            _debounce = null;
        }
        if (snap is null) return;
        try
        {
            var tmp = _dataFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snap, _jsonOpts));
            if (File.Exists(_dataFile)) File.Replace(tmp, _dataFile, null);
            else File.Move(tmp, _dataFile);
        }
        catch
        {
            // Ignore persistence errors to avoid killing the app.
        }
    }

    public string SaveImageBlob(BitmapSource image)
    {
        var name = Guid.NewGuid().ToString("N") + ".png";
        var full = Path.Combine(_blobsDir, name);
        using var fs = new FileStream(full, FileMode.Create, FileAccess.Write);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(fs);
        return name;
    }

    public BitmapImage? LoadImageBlob(string name)
    {
        try
        {
            var full = Path.Combine(_blobsDir, name);
            if (!File.Exists(full)) return null;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(full);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>缩略图最长边像素上限（用于内存中的显示副本，原图仍完整保存在磁盘 blob）。</summary>
    public const int ThumbnailMaxSide = 512;

    /// <summary>
    /// 从 blob 加载“降采样”缩略图：通过 DecodePixelWidth/Height 让编解码器在解码阶段就缩小，
    /// 从不把整张全分辨率位图读进内存。这是把单张图片常驻内存从数十 MB 降到 &lt;1MB 的关键。
    /// </summary>
    public BitmapImage? LoadImageThumbnail(string name, int maxSide = ThumbnailMaxSide)
    {
        try
        {
            var full = Path.Combine(_blobsDir, name);
            if (!File.Exists(full)) return null;
            var uri = new Uri(full);

            // 先只读文件头拿到原始尺寸，决定按宽还是按高限制（保证最长边 ≤ maxSide）。
            int ow = 0, oh = 0;
            try
            {
                var dec = BitmapDecoder.Create(uri, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (dec.Frames.Count > 0) { ow = dec.Frames[0].PixelWidth; oh = dec.Frames[0].PixelHeight; }
            }
            catch { }

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            if (ow >= oh && ow > maxSide) bi.DecodePixelWidth = maxSide;
            else if (oh > ow && oh > maxSide) bi.DecodePixelHeight = maxSide;
            bi.UriSource = uri;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    public void DeleteImageBlob(string name)
    {
        try
        {
            var full = Path.Combine(_blobsDir, name);
            if (File.Exists(full)) File.Delete(full);
        }
        catch { }
    }

    public string SaveGifBlob(byte[] gifBytes)
    {
        var name = Guid.NewGuid().ToString("N") + ".gif";
        var full = Path.Combine(_blobsDir, name);
        File.WriteAllBytes(full, gifBytes);
        return name;
    }

    public byte[]? LoadGifBlob(string name)
    {
        try
        {
            var full = Path.Combine(_blobsDir, name);
            return File.Exists(full) ? File.ReadAllBytes(full) : null;
        }
        catch { return null; }
    }

    public void DeleteGifBlob(string name) => DeleteImageBlob(name);
}
