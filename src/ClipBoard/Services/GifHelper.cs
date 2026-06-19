using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;

namespace ClipBoard.Services;

public static class GifHelper
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const long MaxBytes = 10 * 1024 * 1024;

    public static bool TryReadGifFromClipboard(out byte[]? gif)
    {
        gif = null;
        try
        {
            if (!Clipboard.ContainsData(DataFormats.Html)) return false;
            var html = Clipboard.GetData(DataFormats.Html) as string;
            if (string.IsNullOrEmpty(html)) return false;
            var url = ExtractGifUrl(html);
            if (url == null) return false;
            gif = DownloadGif(url);
            return gif != null;
        }
        catch { return false; }
    }

    public static string? ExtractGifUrl(string html)
    {
        var m = Regex.Match(html, @"<img[^>]+src=[""']([^""']+\.gif(?:\?[^""']*)?)[""']", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        var m2 = Regex.Match(html, @"[""'](https?://[^""']+\.gif(?:\?[^""']*)?)[""']", RegexOptions.IgnoreCase);
        return m2.Success ? m2.Groups[1].Value : null;
    }

    public static byte[]? DownloadGif(string url)
    {
        try
        {
            using var resp = _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return null;
            if (resp.Content.Headers.ContentLength is long len && len > MaxBytes) return null;
            using var ms = new System.IO.MemoryStream();
            using var s = resp.Content.ReadAsStream();
            byte[] buf = new byte[8192];
            int total = 0, n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
                if (total > MaxBytes) return null;
                ms.Write(buf, 0, n);
            }
            var bytes = ms.ToArray();
            if (bytes.Length < 6) return null;
            if (bytes[0] != 'G' || bytes[1] != 'I' || bytes[2] != 'F') return null;
            return bytes;
        }
        catch { return null; }
    }
}
