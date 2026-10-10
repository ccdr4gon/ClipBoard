using System.Text;
using ClipBoard.Models;
using static ClipBoard.Services.MacNative;

namespace ClipBoard.Services;

public sealed record ClipboardContent(string? Text = null, string[]? Files = null, byte[]? Image = null, bool IsGif = false, RichContent? Rich = null);

internal static class MacClipboard
{
    private static nint s_boardClass, s_generalPasteboard, s_changeCount;
    private static nint Board => Send(Cls(ref s_boardClass, "NSPasteboard"), Sel(ref s_generalPasteboard, "generalPasteboard"));
    public static long Sequence { get { using var pool = new Pool(); return SequenceInPool; } }
    /// <summary>调用方须已持有自动释放池。</summary>
    internal static long SequenceInPool => (long)Send(Board, Sel(ref s_changeCount, "changeCount"));
    public static ClipboardContent? Read()
    {
        using var pool = new Pool();
        var board = Board;
        var types = Call(board, "types");
        if (Bool(types, "containsObject:", String("org.nspasteboard.ConcealedType")) ||
            Bool(types, "containsObject:", String("org.nspasteboard.TransientType"))) return null;
        var files = new List<string>();
        var items = Call(board, "pasteboardItems");
        for (int i = 0; i < (int)Call(items, "count"); i++)
        {
            var item = Call(items, "objectAtIndex:", i);
            var url = Call(item, "stringForType:", String("public.file-url"));
            if (url != 0)
            {
                var path = Text(Call(Call(Class("NSURL"), "URLWithString:", url), "path"));
                if (path.Length > 0) files.Add(path);
            }
        }
        if (files.Count > 0) return new(Files: files.ToArray());
        var gif = Bytes(Call(board, "dataForType:", String("com.compuserve.gif")));
        if (gif != null) return new(Image: gif, IsGif: true);
        var png = Bytes(Call(board, "dataForType:", String("public.png")));
        if (png == null)
        {
            var tiff = Call(board, "dataForType:", String("public.tiff"));
            if (tiff != 0)
            {
                var rep = Call(Class("NSBitmapImageRep"), "imageRepWithData:", tiff);
                png = Bytes(Call(rep, "representationUsingType:properties:", 4, Call(Class("NSDictionary"), "dictionary")));
            }
        }
        if (png != null) return new(Image: png);
        string text = Text(Call(board, "stringForType:", String("public.utf8-plain-text")));
        if (text.Length == 0) return null;
        // 网页、聊天、备忘录等复制时带的格式；RTF 按字节原样保存（Latin-1 可无损往返）。
        string html = Text(Call(board, "stringForType:", String("public.html")));
        var rtf = Bytes(Call(board, "dataForType:", String("public.rtf")));
        return new(Text: text, Rich: RichText.Create(html.Length > 0 ? html : null, rtf != null ? Encoding.Latin1.GetString(rtf) : null));
    }

    /// <param name="plainText">只写纯文本，丢弃复制时带来的 HTML / RTF 格式。</param>
    public static bool Write(ClipItem item, PersistenceService persistence, bool plainText = false)
    {
        using var pool = new Pool();
        var board = Board;
        var rich = !plainText && item.HasRichText ? persistence.LoadRichBlob(item.RichBlobName) : null;
        // Read files before clearing the pasteboard, so missing blobs don't destroy its previous contents.
        byte[]? data = null;
        string type = "public.png";
        string[]? files = item.FilePaths;
        if (item.Kind == ClipKind.Gif)
        {
            data = item.Sticker != null ? File.ReadAllBytes(persistence.GetBlobPath(item.Sticker.WorkingBlobName))
                : item.GifBytes ?? (item.GifBlobName != null ? persistence.LoadGifBlob(item.GifBlobName) : null);
            type = "com.compuserve.gif";
        }
        else if (item.Kind == ClipKind.Image && item.ImageBlobName != null) data = File.ReadAllBytes(persistence.GetBlobPath(item.ImageBlobName));
        else if (item.Kind is ClipKind.VideoSticker or ClipKind.VectorSticker && item.Sticker != null)
            files = [persistence.GetBlobPath(item.Sticker.WorkingBlobName)];
        if (item.Kind != ClipKind.Text && data == null && files is not { Length: > 0 }) return false;
        if (files?.Any(f => !File.Exists(f) && !Directory.Exists(f)) == true) throw new IOException("文件已不存在，无法复制。");
        Call(board, "clearContents");
        if (item.Kind == ClipKind.Text)
        {
            if (!Bool(board, "setString:forType:", String(item.Text ?? ""), String("public.utf8-plain-text"))) return false;
            // 同一个剪贴板项里再放 HTML / RTF：备忘录、Pages、邮件等会保留列表编号和格式，纯文本应用仍读纯文本。
            if (rich?.Html is { Length: > 0 } html) Bool(board, "setString:forType:", String(RichText.WithCharset(html)), String("public.html"));
            if (rich?.Rtf is { Length: > 0 } rtf) Bool(board, "setData:forType:", Data(Encoding.Latin1.GetBytes(rtf)), String("public.rtf"));
            return true;
        }
        if (data != null) return Bool(board, "setData:forType:", Data(data), String(type));
        var array = Call(Class("NSMutableArray"), "array");
        foreach (var file in files!) Call(array, "addObject:", Call(Class("NSURL"), "fileURLWithPath:", String(Path.GetFullPath(file))));
        return Bool(board, "writeObjects:", array);
    }
}
