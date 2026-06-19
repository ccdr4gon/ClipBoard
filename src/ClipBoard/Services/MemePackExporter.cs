using System.IO;
using System.Windows.Media.Imaging;
using ClipBoard.Models;
using SkiaSharp;

namespace ClipBoard.Services;

public enum ExportPreset
{
    WeChat,   // 240 PNG
    Telegram, // 512 PNG
    QQ,       // 240 PNG
    WhatsApp, // 512 WEBP, <=100KB
    Raw,      // original pixels, PNG
}

public static class MemePackExporter
{
    public static int Export(FavoriteFolder folder, ExportPreset preset, string outputDir, PersistenceService persistence)
    {
        Directory.CreateDirectory(outputDir);
        int count = 0;
        int idx = 1;
        foreach (var item in folder.Items)
        {
            if (item.Kind != ClipKind.Image) continue;
            // 优先用磁盘上的全分辨率原图导出；内存里的 item.Image 只是缩略图，仅作回退。
            BitmapSource? src = !string.IsNullOrEmpty(item.ImageBlobName)
                ? persistence.LoadImageBlob(item.ImageBlobName) : item.Image;
            if (src == null) continue;

            try
            {
                var name = $"{idx:D3}";
                idx++;
                switch (preset)
                {
                    case ExportPreset.WeChat:
                    case ExportPreset.QQ:
                        SavePng(Resize(src, 240), Path.Combine(outputDir, name + ".png"));
                        break;
                    case ExportPreset.Telegram:
                        SavePng(Resize(src, 512), Path.Combine(outputDir, name + ".png"));
                        break;
                    case ExportPreset.WhatsApp:
                        SaveWebpUnder100KB(Resize(src, 512), Path.Combine(outputDir, name + ".webp"));
                        break;
                    case ExportPreset.Raw:
                        SavePng(src, Path.Combine(outputDir, name + ".png"));
                        break;
                }
                count++;
            }
            catch { }
        }
        return count;
    }

    private static BitmapSource Resize(BitmapSource src, int maxSide)
    {
        int longest = Math.Max(src.PixelWidth, src.PixelHeight);
        if (longest <= maxSide) return src;
        double scale = (double)maxSide / longest;
        var t = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scale, scale));
        t.Freeze();
        return t;
    }

    private static void SavePng(BitmapSource src, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        enc.Save(fs);
    }

    private static void SaveWebpUnder100KB(BitmapSource src, string path)
    {
        using var ms = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        enc.Save(ms);
        ms.Position = 0;
        using var skBitmap = SKBitmap.Decode(ms);

        int lo = 50, hi = 95, best = 50;
        byte[]? bestBytes = null;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            using var img = SKImage.FromBitmap(skBitmap);
            using var data = img.Encode(SKEncodedImageFormat.Webp, mid);
            var bytes = data.ToArray();
            if (bytes.Length <= 100 * 1024)
            {
                best = mid;
                bestBytes = bytes;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        if (bestBytes == null)
        {
            using var img2 = SKImage.FromBitmap(skBitmap);
            using var data2 = img2.Encode(SKEncodedImageFormat.Webp, 20);
            bestBytes = data2.ToArray();
        }
        File.WriteAllBytes(path, bestBytes);
    }
}
