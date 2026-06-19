using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace ClipBoard.Models;

public enum ClipKind { Text, Image, Files, Gif }

public class ClipItem : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ClipKind Kind { get; set; }
    public string? Text { get; set; }
    public string? ImageBlobName { get; set; }
    public string? GifBlobName { get; set; }
    public string[]? FilePaths { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    // 原始图片像素尺寸（持久化）。内存中的 Image 只是缩略图，故尺寸标签用这两个值。
    public int PixelW { get; set; }
    public int PixelH { get; set; }

    // 去重用的首行+尺寸签名（仅内存，比较最近一条是否同图）。
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
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = new System.IO.MemoryStream(_gifBytes);
            bi.EndInit();
            bi.Freeze();
            _gifSourceCache = bi;
            return bi;
        }
    }

    [JsonIgnore]
    public string Preview
    {
        get
        {
            return Kind switch
            {
                ClipKind.Text => (Text ?? "").Replace("\r", "").Replace("\n", " ⏎ "),
                ClipKind.Files => FilePaths is null or { Length: 0 }
                    ? "(空文件列表)"
                    : FilePaths.Length == 1
                        ? System.IO.Path.GetFileName(FilePaths[0])
                        : $"{FilePaths.Length} 个文件：{System.IO.Path.GetFileName(FilePaths[0])} …",
                ClipKind.Image => $"图片 ({(PixelW > 0 ? PixelW : Image?.PixelWidth ?? 0)}×{(PixelH > 0 ? PixelH : Image?.PixelHeight ?? 0)})",
                ClipKind.Gif => $"GIF ({((_gifBytes?.Length ?? 0) / 1024)} KB)",
                _ => ""
            };
        }
    }

    [JsonIgnore]
    public string TimeLabel => Timestamp.ToString("MM-dd HH:mm");

    [JsonIgnore]
    public string DimensionLabel => Kind switch
    {
        ClipKind.Image => $"{(PixelW > 0 ? PixelW : Image?.PixelWidth ?? 0)}×{(PixelH > 0 ? PixelH : Image?.PixelHeight ?? 0)} · PNG",
        ClipKind.Gif => $"GIF · {((_gifBytes?.Length ?? 0) / 1024)} KB",
        _ => ""
    };

    [JsonIgnore]
    public System.Windows.Visibility TextVisibility =>
        Kind == ClipKind.Text ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    [JsonIgnore]
    public System.Windows.Visibility ImageVisibility =>
        Kind == ClipKind.Image ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    [JsonIgnore]
    public System.Windows.Visibility GifVisibility =>
        Kind == ClipKind.Gif ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    [JsonIgnore]
    public System.Windows.Visibility FilesVisibility =>
        Kind == ClipKind.Files ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    [JsonIgnore]
    public System.Windows.Visibility PinVisibility =>
        IsPinned ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
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
        FilePaths = FilePaths?.ToArray(),
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
