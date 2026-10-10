using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
#if AVALONIA
using BitmapSource = Avalonia.Media.Imaging.Bitmap;
using BitmapImage = Avalonia.Media.Imaging.Bitmap;
#else
using System.Windows.Media.Imaging;
#endif

namespace ClipBoard.Models;

public enum ClipKind { Text, Image, Files, Gif, VideoSticker, VectorSticker }

public class ClipItem : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ClipKind Kind { get; set; }
    public string? Text { get; set; }
    public string? ImageBlobName { get; set; }
    public string? GifBlobName { get; set; }
    public string[]? FilePaths { get; set; }
    public StickerAsset? Sticker { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    // 文本复制时一起带来的 HTML / RTF，存在 blob 文件里，粘贴时按需读取。
    private string? _richBlobName;
    public string? RichBlobName
    {
        get => _richBlobName;
        set { if (_richBlobName != value) { _richBlobName = value; OnChanged(); OnChanged(nameof(HasRichText)); OnChanged(nameof(RichVisibility)); } }
    }

    [JsonIgnore]
    public bool HasRichText => Kind == ClipKind.Text && !string.IsNullOrEmpty(_richBlobName);

    // 原始图片像素尺寸（持久化）。内存中的 Image 只是缩略图，故尺寸标签用这两个值。
    public int PixelW { get; set; }
    public int PixelH { get; set; }

    // 完整像素的签名（仅内存，结合尺寸比较最近一条是否同图）。
    [JsonIgnore]
    public long ImageSig { get; set; }

    private string? _title;
    public string? Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnChanged(); OnChanged(nameof(HasTitle)); OnChanged(nameof(TitleOrUntitled)); OnChanged(nameof(SubtitleText)); } }
    }

    [JsonIgnore]
    public bool HasTitle => !string.IsNullOrWhiteSpace(_title);

    [JsonIgnore]
    public string TitleOrUntitled => HasTitle ? _title! : "未命名";

    [JsonIgnore]
    public string SubtitleText => HasTitle ? $"\u201C{_title}\u201D" : "";

    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set { if (_isPinned != value) { _isPinned = value; OnChanged(); } }
    }

    public Guid? FolderId { get; set; }

    [JsonIgnore]
    private BitmapSource? _image;

    [JsonIgnore]
    public BitmapSource? Image
    {
        get => _image;
        set { _image = value; OnChanged(); }
    }

    [JsonIgnore]
    private byte[]? _gifBytes;

    [JsonIgnore]
    public byte[]? GifBytes
    {
        get => _gifBytes;
        set { _gifBytes = value; _gifSourceCache = null; OnChanged(); OnChanged(nameof(GifSource)); }
    }

    // 缓存解码后的 GIF 源：WPF 绑定会反复读取 GifSource，若每次都新建 BitmapImage，
    // WpfAnimatedGif 会为每次读取重新解码全部帧并启动一个永不释放的动画时钟 → 内存暴涨。
    // 缓存为冻结的单实例后，每个 GIF 只解码一次、只有一个动画时钟。
    [JsonIgnore]
    private BitmapImage? _gifSourceCache;

    [JsonIgnore]
    public BitmapImage? GifSource
    {
        get
        {
            if (_gifBytes == null || _gifBytes.Length == 0) return null;
            if (_gifSourceCache != null) return _gifSourceCache;
#if AVALONIA
            using var stream = new System.IO.MemoryStream(_gifBytes);
            var bi = new BitmapImage(stream);
#else
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = new System.IO.MemoryStream(_gifBytes);
            bi.EndInit();
            bi.Freeze();
#endif
            _gifSourceCache = bi;
            return bi;
        }
    }

    [JsonIgnore]
    private int ImageWidth =>
#if AVALONIA
        Image?.PixelSize.Width ?? 0;
#else
        Image?.PixelWidth ?? 0;
#endif
    [JsonIgnore]
    private int ImageHeight =>
#if AVALONIA
        Image?.PixelSize.Height ?? 0;
#else
        Image?.PixelHeight ?? 0;
#endif

    // 文本条目的单行预览按 Text 字符串实例缓存：Text 换成别的字符串就自动失效，无需改任何 setter。
    // 整个对象一次性替换，其它线程读到的 Src 与 Value 总是同一对。
    private sealed class PreviewMemo(string? src, string value)
    {
        public readonly string? Src = src;
        public readonly string Value = value;
    }

    [JsonIgnore]
    private PreviewMemo? _previewMemo;

    private string TextPreview()
    {
        var text = Text;
        if (_previewMemo is { } memo && ReferenceEquals(memo.Src, text)) return memo.Value;
        var value = (text ?? "").Replace("\r", "").Replace("\n", " ⏎ ");
        _previewMemo = new PreviewMemo(text, value);
        return value;
    }

    [JsonIgnore]
    public string Preview
    {
        get
        {
            return Kind switch
            {
                ClipKind.Text => TextPreview(),
                ClipKind.Files => FilePaths is null or { Length: 0 }
                    ? "(空文件列表)"
                    : FilePaths.Length == 1
                        ? System.IO.Path.GetFileName(FilePaths[0])
                        : $"{FilePaths.Length} 个文件：{System.IO.Path.GetFileName(FilePaths[0])} …",
                ClipKind.Image => $"图片 ({(PixelW > 0 ? PixelW : ImageWidth)}×{(PixelH > 0 ? PixelH : ImageHeight)})",
                ClipKind.Gif => Sticker != null ? $"GIF ({Sticker.DurationSeconds:0.##} 秒)" : $"GIF ({((_gifBytes?.Length ?? 0) / 1024)} KB)",
                ClipKind.VideoSticker => "视频贴纸 · WEBM",
                ClipKind.VectorSticker => "动画贴纸 · TGS",
                _ => ""
            };
        }
    }

    [JsonIgnore]
    public string TimeLabel => Timestamp.ToString("MM-dd HH:mm");

    [JsonIgnore]
    public string DimensionLabel => Kind switch
    {
        ClipKind.Image => $"{(PixelW > 0 ? PixelW : ImageWidth)}×{(PixelH > 0 ? PixelH : ImageHeight)} · PNG",
        ClipKind.Gif => Sticker != null ? $"GIF · {Sticker.DurationSeconds:0.##} 秒" : $"GIF · {((_gifBytes?.Length ?? 0) / 1024)} KB",
        ClipKind.VideoSticker => $"{PixelW}×{PixelH} · WEBM",
        ClipKind.VectorSticker => "512×512 · TGS",
        _ => ""
    };

#if AVALONIA
    [JsonIgnore] public bool TextVisibility => Kind == ClipKind.Text;
    [JsonIgnore] public bool ImageVisibility => Kind is ClipKind.Image or ClipKind.VideoSticker or ClipKind.VectorSticker;
    [JsonIgnore] public bool GifVisibility => Kind == ClipKind.Gif;
    [JsonIgnore] public bool FilesVisibility => Kind == ClipKind.Files;
    [JsonIgnore] public bool PinVisibility => IsPinned;
    [JsonIgnore] public bool RichVisibility => HasRichText;
#else
    [JsonIgnore]
    public System.Windows.Visibility TextVisibility =>
        Kind == ClipKind.Text ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    [JsonIgnore]
    public System.Windows.Visibility ImageVisibility =>
        Kind is ClipKind.Image or ClipKind.VideoSticker or ClipKind.VectorSticker
            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    [JsonIgnore]
    public System.Windows.Visibility GifVisibility =>
        Kind == ClipKind.Gif ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    [JsonIgnore]
    public System.Windows.Visibility FilesVisibility =>
        Kind == ClipKind.Files ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    [JsonIgnore]
    public System.Windows.Visibility PinVisibility =>
        IsPinned ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    [JsonIgnore]
    public System.Windows.Visibility RichVisibility =>
        HasRichText ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
#endif

    public event PropertyChangedEventHandler? PropertyChanged;
    [JsonIgnore]
    public string StickerStatus => Sticker?.PublishedFileId == null ? "未发布"
        : Sticker.Revision == Sticker.PublishedRevision ? "已同步" : "待更新";
    [JsonIgnore]
    public string StickerEmojiLabel => string.Join(" ", Sticker?.Emojis ?? []);

    public void NotifyMediaChanged()
    {
        foreach (var property in new[] { nameof(Kind), nameof(Image), nameof(GifSource), nameof(Preview),
            nameof(DimensionLabel), nameof(TextVisibility), nameof(ImageVisibility), nameof(GifVisibility),
            nameof(FilesVisibility), nameof(Sticker), nameof(StickerStatus), nameof(StickerEmojiLabel) })
            OnChanged(property);
    }
    private void OnChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(IsPinned))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinVisibility)));
    }

    public ClipItem Clone() => new()
    {
        Id = Guid.NewGuid(),
        Kind = Kind,
        Text = Text,
        ImageBlobName = ImageBlobName,
        GifBlobName = GifBlobName,
        RichBlobName = RichBlobName,
        FilePaths = FilePaths?.ToArray(),
        Sticker = Sticker?.Clone(),
        Timestamp = Timestamp,
        IsPinned = IsPinned,
        FolderId = FolderId,
        PixelW = PixelW,
        PixelH = PixelH,
        ImageSig = ImageSig,
        Image = Image,
        GifBytes = GifBytes,
        Title = Title,
    };
}
