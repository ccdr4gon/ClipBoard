using System.IO;
using System.Text.Json;
#if AVALONIA
using BitmapSource = Avalonia.Media.Imaging.Bitmap;
using BitmapImage = Avalonia.Media.Imaging.Bitmap;
#else
using System.Windows.Media.Imaging;
#endif
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

    public PersistenceService(string? rootDirectory = null)
    {
        _root = rootDirectory ?? (OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "ClipBoard")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipBoard"));
        _dataFile = Path.Combine(_root, "data.json");
        _blobsDir = Path.Combine(_root, "blobs");
        Directory.CreateDirectory(_blobsDir);
    }

    public string BlobsDir => _blobsDir;
    public string RootDirectory => _root;

    public string GetBlobPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
            throw new ArgumentException("无效的素材文件名。", nameof(name));
        return Path.Combine(_blobsDir, name);
    }

    public PersistedData Load()
    {
        try
        {
            if (!File.Exists(_dataFile)) return new PersistedData();
            var json = File.ReadAllText(_dataFile);
            if (string.IsNullOrWhiteSpace(json)) return new PersistedData();
            var data = JsonSerializer.Deserialize<PersistedData>(json, _jsonOpts) ?? new PersistedData();
            // 旧版保存时遗漏了原图尺寸，从文件头补回，避免把缩略图尺寸当成原图尺寸。
            foreach (var item in data.History.Concat(data.PinnedHistory).Concat(data.Favorites))
            {
                if (item.Kind == ClipKind.Image && !string.IsNullOrEmpty(item.ImageBlobName)
                    && (item.PixelW <= 0 || item.PixelH <= 0))
                {
                    (item.PixelW, item.PixelH) = ReadImageSize(item.ImageBlobName);
                }
            }
            return data;
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
        // 取快照和写文件必须共用同一把锁；FlushSync 也要等正在写的快照落盘。
        lock (_lock)
        {
            _debounce?.Dispose();
            _debounce = null;
            if (_pendingSnapshot is null) return;
            try
            {
                var tmp = _dataFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_pendingSnapshot, _jsonOpts));
                if (File.Exists(_dataFile)) File.Replace(tmp, _dataFile, null);
                else File.Move(tmp, _dataFile);
                _pendingSnapshot = null;
            }
            catch
            {
                // 保留待保存快照，下一次保存或退出刷新时仍可重试。
            }
        }
    }

    public string SaveImageBlob(BitmapSource image)
    {
        var name = Guid.NewGuid().ToString("N") + ".png";
        var full = Path.Combine(_blobsDir, name);
        using var fs = new FileStream(full, FileMode.Create, FileAccess.Write);
#if AVALONIA
        image.Save(fs);
#else
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(fs);
#endif
        return name;
    }

    public BitmapImage? LoadImageBlob(string name)
    {
        try
        {
            var full = Path.Combine(_blobsDir, name);
            if (!File.Exists(full)) return null;
#if AVALONIA
            using var stream = File.OpenRead(full);
            return new BitmapImage(stream);
#else
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(full);
            bi.EndInit();
            bi.Freeze();
            return bi;
#endif
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
            var (ow, oh) = ReadImageSize(name);
#if AVALONIA
            using var stream = File.OpenRead(full);
            return ow >= oh && ow > maxSide ? BitmapImage.DecodeToWidth(stream, maxSide)
                : oh > maxSide ? BitmapImage.DecodeToHeight(stream, maxSide) : new BitmapImage(stream);
#else
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            if (ow >= oh && ow > maxSide) bi.DecodePixelWidth = maxSide;
            else if (oh > ow && oh > maxSide) bi.DecodePixelHeight = maxSide;
            bi.UriSource = uri;
            bi.EndInit();
            bi.Freeze();
            return bi;
#endif
        }
        catch
        {
            return null;
        }
    }

    private (int Width, int Height) ReadImageSize(string name)
    {
        try
        {
            // 明确关闭文件流；用 Uri + CacheOption.None 创建解码器会把文件锁留给 GC。
            using var stream = File.OpenRead(Path.Combine(_blobsDir, name));
#if AVALONIA
            using var codec = SkiaSharp.SKCodec.Create(stream);
            return codec == null ? (0, 0) : (codec.Info.Width, codec.Info.Height);
#else
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
#endif
        }
        catch { return (0, 0); }
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

    // HTML 动辄几百 KB，单独存文件，避免每次保存都重写进 data.json。
    public string SaveRichBlob(RichContent rich)
    {
        var name = Guid.NewGuid().ToString("N") + ".rich.json";
        File.WriteAllText(Path.Combine(_blobsDir, name), JsonSerializer.Serialize(rich));
        return name;
    }

    public RichContent? LoadRichBlob(string? name)
    {
        try
        {
            if (string.IsNullOrEmpty(name)) return null;
            var full = GetBlobPath(name);
            return File.Exists(full) ? JsonSerializer.Deserialize<RichContent>(File.ReadAllText(full)) : null;
        }
        catch { return null; }
    }
}
