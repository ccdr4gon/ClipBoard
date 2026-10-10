using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;

namespace ClipBoard.Services;

public static class GifHelper
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const long MaxBytes = 10 * 1024 * 1024;

    public static string? ReadGifUrlFromClipboard() => ReadGifUrlFromClipboard(Clipboard.GetDataObject);

    /// <param name="getData">取本次剪贴板通知共用的数据对象；在 try 内调用，取失败照旧当作没有 GIF。</param>
    public static string? ReadGifUrlFromClipboard(Func<IDataObject?> getData)
    {
        try
        {
            if (!Clipboard.ContainsData(DataFormats.Html)) return null;
            var html = getData()?.GetData(DataFormats.Html, false) as string;
            return string.IsNullOrEmpty(html) ? null : ExtractGifUrl(html);
        }
        catch { return null; }
    }

    public static string? ExtractGifUrl(string html)
    {
        var m = Regex.Match(html, @"<img[^>]+src=[""']([^""']+\.gif(?:\?[^""']*)?)[""']", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        var m2 = Regex.Match(html, @"[""'](https?://[^""']+\.gif(?:\?[^""']*)?)[""']", RegexOptions.IgnoreCase);
        return m2.Success ? m2.Groups[1].Value : null;
    }

    public static async Task<byte[]?> DownloadGifAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var token = timeout.Token;
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            if (resp.Content.Headers.ContentLength is long len && len > MaxBytes) return null;
            // 按声明的长度预留，免去逐步翻倍扩容；读取方式和上限检查不变。
            using var ms = new System.IO.MemoryStream(resp.Content.Headers.ContentLength is long expected && expected > 0 ? (int)expected : 0);
            using var s = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            byte[] buf = new byte[8192];
            int total = 0, n;
            while ((n = await s.ReadAsync(buf.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > MaxBytes) return null;
                ms.Write(buf, 0, n);
            }
            // 正文恰好填满内部数组（长度与声明一致）时直接使用，省去最后一次整份复制。
            var bytes = ms.Length == ms.Capacity ? ms.GetBuffer() : ms.ToArray();
            if (bytes.Length < 6) return null;
            if (!bytes.AsSpan(0, 6).SequenceEqual("GIF87a"u8)
                && !bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8)) return null;
            return bytes;
        }
        catch { return null; }
    }
}
