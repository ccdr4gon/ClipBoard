namespace ClipBoard.Models;

/// <summary>文本条目复制时一起放进剪贴板的格式（网页、聊天、Word 等），整体保存在一个 blob 文件里。</summary>
public sealed record RichContent(string? Html, string? Rtf);
