using ClipBoard.Models;
using static ClipBoard.Services.MacNative;

namespace ClipBoard.Services;

public sealed record ClipboardContent(string? Text = null, string[]? Files = null, byte[]? Image = null, bool IsGif = false);

internal static class MacClipboard
{
    private static nint Board => Call(Class("NSPasteboard"), "generalPasteboard");
    public static long Sequence { get { using var pool = new Pool(); return (long)Call(Board, "changeCount"); } }
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
        return text.Length == 0 ? null : new(Text: text);
    }

    public static bool Write(ClipItem item, PersistenceService persistence)
    {
        using var pool = new Pool();
        var board = Board;
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
        if (item.Kind == ClipKind.Text) return Bool(board, "setString:forType:", String(item.Text ?? ""), String("public.utf8-plain-text"));
        if (data != null) return Bool(board, "setData:forType:", Data(data), String(type));
        var array = Call(Class("NSMutableArray"), "array");
        foreach (var file in files!) Call(array, "addObject:", Call(Class("NSURL"), "fileURLWithPath:", String(Path.GetFullPath(file))));
        return Bool(board, "writeObjects:", array);
    }
}
