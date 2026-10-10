using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    // 元数据由编译期生成的 PersistedDataJsonContext 提供，启动时不再反射建模型、发射访问器；选项本身与以前完全相同。
    private readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = PersistedDataJsonContext.Default,
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
            var data = ReadDataFile() ?? new PersistedData();
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

    // 直接解析 UTF-8 字节，省去先把整个文件解码成 UTF-16 字符串、反序列化时再转回 UTF-8 的两遍拷贝。
    // 字节解析比 ReadAllText 严格（不认 UTF-16/32 BOM、空文件、非法 UTF-8）。失败时完整走一遍原来的读法，结果与以前一致；
    // 不能直接当成空数据，否则下一次保存会把用户的历史覆盖掉。
    private PersistedData? ReadDataFile()
    {
        var bytes = File.ReadAllBytes(_dataFile);
        try
        {
            return JsonSerializer.Deserialize<PersistedData>(WithoutUtf8Bom(bytes), _jsonOpts);
        }
        catch
        {
            var json = File.ReadAllText(_dataFile);
            if (string.IsNullOrWhiteSpace(json)) return new PersistedData();
            return JsonSerializer.Deserialize<PersistedData>(json, _jsonOpts);
        }
    }

    // ReadAllText 会去掉 UTF-8 BOM，字节解析器不会。
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];
    private static ReadOnlySpan<byte> WithoutUtf8Bom(byte[] bytes)
        => bytes.AsSpan().StartsWith(Utf8Bom) ? bytes.AsSpan(3) : bytes;

    // JSON 直接按 UTF-8 写进文件，不再先生成整段字符串。打开方式与 File.WriteAllText 相同（覆盖、FileShare.Read、无 BOM）；
    // 序列化器自带缓冲，文件流不再加一层。
    private static FileStream CreateJsonFile(string path) => new(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 0);

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
                // 必须先关闭临时文件再替换，否则 Windows 上替换会因共享冲突失败。
                using (var stream = CreateJsonFile(tmp))
                    JsonSerializer.Serialize(stream, _pendingSnapshot, _jsonOpts);
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
            // blob 都是 PNG：尺寸就写在文件头的 IHDR 里，直接读出，省掉一次解码器创建。其他格式仍交给解码器。
            if (TryReadPngSize(stream, out int pngWidth, out int pngHeight)) return (pngWidth, pngHeight);
            stream.Position = 0;
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

    // PNG 规范：8 字节签名之后第一个块必须是 IHDR：长度(4) "IHDR"(4) 宽(4，大端) 高(4，大端)。
    // 块类型或尺寸不对（CgBI、损坏文件）就返回 false，交回解码器按原样处理。
    private static bool TryReadPngSize(Stream stream, out int width, out int height)
    {
        width = height = 0;
        Span<byte> header = stackalloc byte[24];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length) return false;
        if (!header[..8].SequenceEqual(PngSignature) || !header.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
        height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));
        return width > 0 && height > 0;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

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
        var full = Path.Combine(_blobsDir, name);
        try
        {
            using var stream = CreateJsonFile(full);
            JsonSerializer.Serialize(stream, rich, RichContentJsonContext.Default.RichContent);
        }
        catch
        {
            // 以前序列化失败时不会留下文件；流式写出后同样删掉写了一半的文件。
            try { File.Delete(full); } catch { }
            throw;
        }
        return name;
    }

    public RichContent? LoadRichBlob(string? name)
    {
        try
        {
            if (string.IsNullOrEmpty(name)) return null;
            var full = GetBlobPath(name);
            if (!File.Exists(full)) return null;
            var bytes = File.ReadAllBytes(full);
            // 同 data.json：字节解析失败（UTF-16 等）时按原来的方式再读一次。
            try { return JsonSerializer.Deserialize(WithoutUtf8Bom(bytes), RichContentJsonContext.Default.RichContent); }
            catch { return JsonSerializer.Deserialize(File.ReadAllText(full), RichContentJsonContext.Default.RichContent); }
        }
        catch { return null; }
    }
}

// data.json：与 _jsonOpts 相同的选项（不缩进、忽略 null），覆盖 PersistedData 引用到的全部类型。
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PersistedData))]
internal sealed partial class PersistedDataJsonContext : JsonSerializerContext { }

// 格式文件一直用默认选项写（保留 "Rtf":null），单独一个上下文，字节不变。
[JsonSerializable(typeof(RichContent))]
internal sealed partial class RichContentJsonContext : JsonSerializerContext { }
