using System.Windows;
using System.Windows.Controls;
using ClipBoard.Models;

namespace ClipBoard.Views;

/// <summary>
/// 历史 / 收藏列表的行模板：按条目类型只建对应的那一种内容，不再每行建齐四种再隐藏三种。
/// 条目的 Kind 在显示期间不会原地改变（贴纸编辑会换成新的克隆对象，触发重新选模板）。
/// 模板按资源键在第一次用到时才取：资源字典里的模板是延迟加载的，没出现过的类型不必解析它的模板。
/// </summary>
public sealed class ClipKindTemplateSelector : DataTemplateSelector
{
    public string? TextKey { get; set; }
    public string? ImageKey { get; set; }
    public string? GifKey { get; set; }
    public string? FilesKey { get; set; }
    /// <summary>未知类型（例如新版本写入的数据）：用原来的四合一模板，显示效果与以前完全相同。</summary>
    public string? FallbackKey { get; set; }

    private DataTemplate? _text, _image, _gif, _files, _fallback;

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        (item is not ClipItem clip ? null : clip.Kind switch
        {
            ClipKind.Text => Resolve(ref _text, TextKey, container),
            ClipKind.Image or ClipKind.VideoSticker or ClipKind.VectorSticker => Resolve(ref _image, ImageKey, container),
            ClipKind.Gif => Resolve(ref _gif, GifKey, container),
            ClipKind.Files => Resolve(ref _files, FilesKey, container),
            _ => null,
        }) ?? Resolve(ref _fallback, FallbackKey, container);

    private static DataTemplate? Resolve(ref DataTemplate? cache, string? key, DependencyObject container)
    {
        if (cache == null && key != null)
            cache = ((container as FrameworkElement)?.TryFindResource(key) ?? Application.Current?.TryFindResource(key)) as DataTemplate;
        return cache;
    }
}
