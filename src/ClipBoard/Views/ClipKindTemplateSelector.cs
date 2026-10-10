using System.Windows;
using System.Windows.Controls;
using ClipBoard.Models;

namespace ClipBoard.Views;

/// <summary>
/// 历史 / 收藏列表的行模板：按条目类型只建对应的那一种内容，不再每行建齐四种再隐藏三种。
/// 条目的 Kind 在显示期间不会原地改变（贴纸编辑会换成新的克隆对象，触发重新选模板）。
/// </summary>
public sealed class ClipKindTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Text { get; set; }
    public DataTemplate? Image { get; set; }
    public DataTemplate? Gif { get; set; }
    public DataTemplate? Files { get; set; }
    /// <summary>未知类型（例如新版本写入的数据）：用原来的四合一模板，显示效果与以前完全相同。</summary>
    public DataTemplate? Fallback { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        (item is not ClipItem clip ? null : clip.Kind switch
        {
            ClipKind.Text => Text,
            ClipKind.Image or ClipKind.VideoSticker or ClipKind.VectorSticker => Image,
            ClipKind.Gif => Gif,
            ClipKind.Files => Files,
            _ => null,
        }) ?? Fallback;
}
