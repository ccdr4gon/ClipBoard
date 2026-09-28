using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using ClipBoard.Models;
using SkiaSharp;
using SkiaSharp.Skottie;

namespace ClipBoard.Services;

public sealed record StickerMediaInfo(int Width, int Height, double Duration, double Fps = 0, bool HasAudio = false, string Codec = "");
public sealed record PreparedSticker(string Path, string Format);

/// <summary>读取原件、生成预览、编辑和输出 Telegram 规格；所有重工作在后台完成。</summary>
public sealed class StickerMediaService(PersistenceService persistence, string toolsDirectory = "")
{
    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    public static string Extension(StickerFormat format) => "." + format.ToString().ToLowerInvariant();
    public static bool IsAnimated(StickerFormat format) => format is StickerFormat.Gif or StickerFormat.Webm or StickerFormat.Tgs;

    public string Tool(string name)
    {
        string executable = name + (OperatingSystem.IsWindows() ? ".exe" : "");
        var candidates = new[] { toolsDirectory, Path.Combine(AppContext.BaseDirectory, "Tools") }
            .Concat(OperatingSystem.IsMacOS() ? new[] { "/opt/homebrew/bin", "/usr/local/bin" } : [])
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p, executable));
        foreach (var path in candidates) if (File.Exists(path)) return path;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var path = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(path)) return path;
        }
        throw new IOException(OperatingSystem.IsMacOS()
            ? "缺少 FFmpeg。请运行 brew install ffmpeg，或在连接设置中填写 ffmpeg / ffprobe 所在目录。"
            : "缺少 FFmpeg 媒体组件。请运行 scripts/Setup-MediaTools.ps1 并重新构建，或在连接设置中选择组件目录。");
    }

    public static StickerFormat DetectFormat(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[12];
        int length = stream.Read(header);
        if (length >= 8 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return StickerFormat.Png;
        if (length >= 6 && (header[..6].SequenceEqual("GIF89a"u8) || header[..6].SequenceEqual("GIF87a"u8))) return StickerFormat.Gif;
        if (length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)) return StickerFormat.Webp;
        if (length >= 4 && header[..4].SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 })) return StickerFormat.Webm;
        if (length >= 2 && header[0] == 0x1f && header[1] == 0x8b) return StickerFormat.Tgs;
        // JPEG/BMP 本地素材在导入时归一化为 PNG。
        using var bitmap = SKBitmap.Decode(path);
        if (bitmap != null) return StickerFormat.Png;
        throw new IOException("无法识别素材；支持 PNG、WebP、GIF、WebM、TGS，以及普通静态图片。");
    }

    public Task<ClipItem> ImportFileAsync(string path, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new IOException("单个本地素材不能超过 64 MB。");
            var format = DetectFormat(path);
            var info = await InspectAsync(path, format, ct);
            ct.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(path).ToLowerInvariant();
            string original = NewBlob(format == StickerFormat.Png && extension is ".jpg" or ".jpeg" or ".bmp" ? extension : Extension(format));
            File.Copy(path, persistence.GetBlobPath(original));
            try
            {
                var item = await CreateItemAsync(original, format, info, ct);
                item.Title = Path.GetFileNameWithoutExtension(path);
                return item;
            }
            catch { File.Delete(persistence.GetBlobPath(original)); throw; }
        }, ct);

    public async Task<ClipItem> ImportBytesAsync(byte[] bytes, StickerFormat expected, CancellationToken ct)
    {
        using var work = new WorkDirectory(persistence.RootDirectory);
        var path = Path.Combine(work.Path, "download" + Extension(expected));
        await File.WriteAllBytesAsync(path, bytes, ct);
        return await ImportFileAsync(path, ct);
    }

    public async Task EnsureAssetAsync(ClipItem item, CancellationToken ct)
    {
        if (item.Sticker != null) return;
        var name = item.Kind == ClipKind.Gif ? item.GifBlobName : item.ImageBlobName;
        if (string.IsNullOrEmpty(name)) throw new IOException("该条目缺少原始素材文件。");
        var path = persistence.GetBlobPath(name);
        var format = DetectFormat(path);
        var info = await Task.Run(() => InspectAsync(path, format, ct), ct);
        item.Sticker = new StickerAsset
        {
            OriginalBlobName = name, WorkingBlobName = name, OriginalFormat = format, Format = format,
            DurationSeconds = info.Duration,
        };
        if (item.Image == null)
        {
            string preview = await Task.Run(() => CreatePosterAsync(path, format, ct), ct);
            item.ImageBlobName = preview;
            item.Image = persistence.LoadImageThumbnail(preview);
        }
        item.NotifyMediaChanged();
    }

    private async Task<ClipItem> CreateItemAsync(string original, StickerFormat format, StickerMediaInfo info, CancellationToken ct)
    {
        var source = persistence.GetBlobPath(original);
        string poster = await CreatePosterAsync(source, format, ct);
        string working = IsAnimated(format) ? original : poster;
        var item = new ClipItem
        {
            Kind = Kind(format), ImageBlobName = poster, GifBlobName = format == StickerFormat.Gif ? original : null,
            PixelW = info.Width, PixelH = info.Height, Image = persistence.LoadImageThumbnail(poster),
            Sticker = new StickerAsset
            {
                OriginalBlobName = original, OriginalFormat = format, WorkingBlobName = working,
                Format = IsAnimated(format) ? format : StickerFormat.Png, DurationSeconds = info.Duration,
            },
        };
        return item;
    }

    public async Task<ClipItem> EditAsync(ClipItem source, StickerEditOptions edit, CancellationToken ct)
    {
        ValidateEdit(edit);
        await EnsureAssetAsync(source, ct);
        var asset = source.Sticker!.Clone();
        return await Task.Run(async () =>
        {
            var input = persistence.GetBlobPath(asset.WorkingBlobName);
            string output;
            StickerFormat format;
            if (IsAnimated(asset.Format))
            {
                output = NewBlob(".webm");
                await EncodeVideoAsync(input, asset.Format, persistence.GetBlobPath(output), edit, ct);
                format = StickerFormat.Webm;
            }
            else
            {
                output = NewBlob(".png");
                using var bitmap = SKBitmap.Decode(input) ?? throw new IOException("无法读取图片。");
                using var edited = DrawEdited(bitmap, edit);
                SavePng(edited, persistence.GetBlobPath(output));
                format = StickerFormat.Png;
            }
            var info = await InspectAsync(persistence.GetBlobPath(output), format, ct);
            string poster = format == StickerFormat.Png ? output : await CreatePosterAsync(persistence.GetBlobPath(output), format, ct);
            var result = source.Clone();
            result.Id = source.Id;
            result.Sticker = asset;
            result.Sticker.WorkingBlobName = output;
            result.Sticker.Format = format;
            result.Sticker.DurationSeconds = info.Duration;
            result.Sticker.Revision++;
            result.Kind = Kind(format);
            result.ImageBlobName = poster;
            result.Image = persistence.LoadImageThumbnail(poster);
            result.GifBlobName = null;
            result.GifBytes = null;
            result.PixelW = info.Width;
            result.PixelH = info.Height;
            return result;
        }, ct);
    }

    public async Task<ClipItem> RestoreAsync(ClipItem item, CancellationToken ct)
    {
        await EnsureAssetAsync(item, ct);
        var old = item.Sticker!.Clone();
        var info = await InspectAsync(persistence.GetBlobPath(old.OriginalBlobName), old.OriginalFormat, ct);
        var result = await Task.Run(() => CreateItemAsync(old.OriginalBlobName, old.OriginalFormat, info, ct), ct);
        var working = result.Sticker!;
        old.WorkingBlobName = working.WorkingBlobName;
        old.Format = working.Format;
        old.DurationSeconds = working.DurationSeconds;
        old.Revision++;
        result.Sticker = old;
        result.Id = item.Id;
        result.FolderId = item.FolderId;
        result.Title = item.Title;
        return result;
    }

    public async Task<PreparedSticker> PrepareTelegramAsync(ClipItem item, CancellationToken ct)
    {
        await EnsureAssetAsync(item, ct);
        var asset = item.Sticker!.Clone();
        return await Task.Run(async () =>
        {
            var source = persistence.GetBlobPath(asset.WorkingBlobName);
            var info = await InspectAsync(source, asset.Format, ct);
            if (asset.Format == StickerFormat.Tgs)
            {
                if (info.Width != 512 || info.Height != 512 || info.Duration > 3.001 || info.Fps > 60.01
                    || new FileInfo(source).Length > 64 * 1024)
                    throw new IOException("TGS 不符合 Telegram 限制；请在编辑器中截取或转换成视频贴纸。");
                return new PreparedSticker(source, "animated");
            }
            if (IsAnimated(asset.Format))
            {
                if (info.Duration > 3.05) throw new IOException($"“{item.TitleOrUntitled}”长 {info.Duration:F1} 秒；请先在编辑器中选择不超过 3 秒的片段。");
                if (asset.Format == StickerFormat.Webm && info.Codec == "vp9" && !info.HasAudio && info.Fps <= 30.01
                    && Math.Max(info.Width, info.Height) == 512 && new FileInfo(source).Length <= 256 * 1024)
                    return new PreparedSticker(source, "video");
                string output = ExportPath(".webm");
                await EncodeVideoAsync(source, asset.Format, output, new StickerEditOptions(DurationSeconds: Math.Max(0.1, info.Duration)), ct);
                return new PreparedSticker(output, "video");
            }
            using var bitmap = SKBitmap.Decode(source) ?? throw new IOException("图片无法解码。");
            using var resized = DrawEdited(bitmap, new StickerEditOptions());
            string png = ExportPath(".png");
            SavePng(resized, png);
            if (new FileInfo(png).Length <= 512 * 1024) return new PreparedSticker(png, "static");
            string webp = Path.ChangeExtension(png, ".webp");
            using var image = SKImage.FromBitmap(resized);
            using var data = image.Encode(SKEncodedImageFormat.Webp, 95);
            using (var stream = File.Create(webp)) data.SaveTo(stream);
            if (new FileInfo(webp).Length > 512 * 1024) throw new IOException("图片压缩后仍超过 512 KB。");
            return new PreparedSticker(webp, "static");
        }, ct);
    }

    public async Task<string> CreatePreviewAsync(ClipItem item, CancellationToken ct)
    {
        await EnsureAssetAsync(item, ct);
        var asset = item.Sticker!.Clone();
        if (!IsAnimated(asset.Format)) return persistence.GetBlobPath(item.ImageBlobName!);
        string output = ExportPath(".gif");
        await Task.Run(async () =>
        {
            using var work = new WorkDirectory(persistence.RootDirectory);
            var input = persistence.GetBlobPath(asset.WorkingBlobName);
            var arguments = new List<string> { "-v", "error", "-y" };
            if (asset.Format == StickerFormat.Tgs)
            {
                await RenderTgsAsync(input, work.Path, 15, ct);
                arguments.AddRange(["-framerate", "15", "-i", Path.Combine(work.Path, "%04d.png")]);
            }
            else
            {
                if (asset.Format == StickerFormat.Webm) arguments.AddRange(["-c:v", "libvpx-vp9"]);
                arguments.AddRange(["-i", input]);
            }
            arguments.AddRange(["-t", "6", "-vf", "fps=15,scale=256:256:force_original_aspect_ratio=decrease,split[a][b];[a]palettegen=reserve_transparent=1[p];[b][p]paletteuse=alpha_threshold=128", "-loop", "0", output]);
            await RunAsync(Tool("ffmpeg"), arguments, ct);
        }, ct);
        return output;
    }

    public async Task<StickerMediaInfo> InspectAsync(string path, StickerFormat format, CancellationToken ct)
    {
        if (format == StickerFormat.Tgs)
        {
            using var animation = ReadTgs(path);
            return new((int)animation.Size.Width, (int)animation.Size.Height, animation.Duration.TotalSeconds, animation.Fps);
        }
        if (format != StickerFormat.Webm)
        {
            using var codec = SKCodec.Create(path) ?? throw new IOException("素材无法解码。");
            if (format == StickerFormat.Webp && codec.FrameCount > 1)
                throw new IOException("这是动态 WebP；这一版接受静态 WebP，请先转换成 GIF 或 WebM 再导入，以免丢失动画。");
            if ((long)codec.Info.Width * codec.Info.Height > 40_000_000) throw new IOException("图片像素过大，请先缩小到 4000 万像素以内。");
            double duration = format == StickerFormat.Gif ? codec.FrameInfo.Sum(f => Math.Max(10, f.Duration)) / 1000.0 : 0;
            return new(codec.Info.Width, codec.Info.Height, duration);
        }
        string json = await RunAsync(Tool("ffprobe"), ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], ct);
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined) throw new IOException("文件中没有视频画面。");
        double durationSeconds = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        string rate = video.GetProperty("avg_frame_rate").GetString() ?? "0/1";
        var parts = rate.Split('/');
        double fps = parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var d) && d != 0 ? n / d : 0;
        int width = video.GetProperty("width").GetInt32(), height = video.GetProperty("height").GetInt32();
        if (width <= 0 || height <= 0 || (long)width * height > 40_000_000 || !double.IsFinite(durationSeconds) || durationSeconds <= 0)
            throw new IOException("视频尺寸或时长无效。");
        return new(width, height, durationSeconds, fps, streams.Any(s => s.GetProperty("codec_type").GetString() == "audio"), video.GetProperty("codec_name").GetString() ?? "");
    }

    private async Task<string> CreatePosterAsync(string path, StickerFormat format, CancellationToken ct)
    {
        string name = NewBlob(".png"), output = persistence.GetBlobPath(name);
        if (format == StickerFormat.Webm)
            await RunAsync(Tool("ffmpeg"), ["-v", "error", "-y", "-c:v", "libvpx-vp9", "-i", path, "-frames:v", "1", "-vf", "scale=512:512:force_original_aspect_ratio=decrease", output], ct);
        else if (format == StickerFormat.Tgs)
        {
            using var animation = ReadTgs(path);
            using var bitmap = new SKBitmap(512, 512, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            animation.SeekFrameTime(0, null);
            animation.Render(canvas, SKRect.Create(512, 512));
            SavePng(bitmap, output);
        }
        else
        {
            using var bitmap = SKBitmap.Decode(path) ?? throw new IOException("无法生成素材预览。");
            SavePng(bitmap, output);
        }
        return name;
    }

    private async Task EncodeVideoAsync(string input, StickerFormat format, string output, StickerEditOptions edit, CancellationToken ct)
    {
        var info = await InspectAsync(input, format, ct);
        if (edit.StartSeconds >= info.Duration) throw new IOException("片段起点必须小于素材时长。");
        double duration = Math.Min(edit.DurationSeconds, (info.Duration - edit.StartSeconds) / edit.Speed);
        if (duration > 3.001) throw new IOException("Telegram 视频贴纸最长 3 秒，请缩短片段或提高播放速度。");
        using var work = new WorkDirectory(persistence.RootDirectory);
        var inputs = new List<string>();
        if (format == StickerFormat.Tgs)
        {
            await RenderTgsAsync(input, work.Path, 30, ct);
            inputs.AddRange(["-framerate", "30", "-ss", Number(edit.StartSeconds), "-i", Path.Combine(work.Path, "%04d.png")]);
        }
        else
        {
            if (format == StickerFormat.Webm) inputs.AddRange(["-c:v", "libvpx-vp9"]);
            inputs.AddRange(["-ss", Number(edit.StartSeconds), "-i", input]);
        }
        int x = (int)(info.Width * edit.CropX), y = (int)(info.Height * edit.CropY);
        int w = Math.Max(1, (int)(info.Width * edit.CropWidth)), h = Math.Max(1, (int)(info.Height * edit.CropHeight));
        string filter = $"crop={w}:{h}:{x}:{y}:exact=1,setpts=(PTS-STARTPTS)/{Number(edit.Speed)},scale=512:512:force_original_aspect_ratio=decrease:flags=lanczos,pad=512:512:(ow-iw)/2:(oh-ih)/2:color=0x00000000,setsar=1,fps=30";
        bool caption = !string.IsNullOrWhiteSpace(edit.Caption);
        if (caption)
        {
            using var overlay = new SKBitmap(512, 512, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(overlay);
            canvas.Clear(SKColors.Transparent);
            DrawCaption(canvas, edit.Caption);
            string overlayPath = Path.Combine(work.Path, "caption.png");
            SavePng(overlay, overlayPath);
            inputs.AddRange(["-i", overlayPath]);
        }
        foreach (int quality in new[] { 24, 32, 40, 48, 56, 63 })
        {
            var arguments = new List<string> { "-v", "error", "-y" };
            arguments.AddRange(inputs);
            if (caption) arguments.AddRange(["-filter_complex", $"[0:v]{filter}[base];[base][1:v]overlay=0:0:format=auto,format=yuva420p[out]", "-map", "[out]"]);
            else arguments.AddRange(["-vf", filter + ",format=yuva420p"]);
            arguments.AddRange(["-t", Number(duration), "-an", "-c:v", "libvpx-vp9", "-b:v", "0", "-crf", quality.ToString(), "-pix_fmt", "yuva420p", "-auto-alt-ref", "0", "-deadline", "good", "-cpu-used", "4", "-threads", "2", output]);
            await RunAsync(Tool("ffmpeg"), arguments, ct);
            if (new FileInfo(output).Length <= 256 * 1024) return;
        }
        File.Delete(output);
        throw new IOException("动画压缩后仍超过 256 KB，请缩短片段或裁剪更多画面后重试。");
    }

    private static Animation ReadTgs(string path)
    {
        using var input = File.OpenRead(path);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        while ((n = gzip.Read(buffer)) > 0)
        {
            if (data.Length + n > 8 * 1024 * 1024) throw new IOException("TGS 解压后过大，无法读取。");
            data.Write(buffer, 0, n);
        }
        data.Position = 0;
        if (!Animation.TryCreate(data, out var animation) || animation == null) throw new IOException("TGS 动画无法解析。");
        if (animation.Duration.TotalSeconds <= 0 || animation.Duration.TotalSeconds > 30)
        { animation.Dispose(); throw new IOException("TGS 动画时长无效或超过 30 秒。"); }
        return animation;
    }

    private static Task RenderTgsAsync(string path, string directory, int fps, CancellationToken ct) => Task.Run(() =>
    {
        using var animation = ReadTgs(path);
        int frames = (int)Math.Ceiling(animation.Duration.TotalSeconds * fps);
        using var bitmap = new SKBitmap(512, 512, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        for (int i = 0; i < frames; i++)
        {
            ct.ThrowIfCancellationRequested();
            canvas.Clear(SKColors.Transparent);
            animation.SeekFrameTime((double)i / fps, null);
            animation.Render(canvas, SKRect.Create(512, 512));
            SavePng(bitmap, Path.Combine(directory, $"{i:D4}.png"));
        }
    }, ct);

    private static SKBitmap DrawEdited(SKBitmap source, StickerEditOptions edit)
    {
        var bitmap = new SKBitmap(512, 512, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var crop = SKRect.Create((float)(source.Width * edit.CropX), (float)(source.Height * edit.CropY),
            (float)(source.Width * edit.CropWidth), (float)(source.Height * edit.CropHeight));
        float scale = Math.Min(512 / crop.Width, 512 / crop.Height);
        var target = SKRect.Create((512 - crop.Width * scale) / 2, (512 - crop.Height * scale) / 2, crop.Width * scale, crop.Height * scale);
        using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
        canvas.DrawBitmap(source, crop, target, paint);
        if (!string.IsNullOrWhiteSpace(edit.Caption)) DrawCaption(canvas, edit.Caption);
        return bitmap;
    }

    private static void DrawCaption(SKCanvas canvas, string caption)
    {
        using var typeface = SKTypeface.FromFamilyName(OperatingSystem.IsMacOS() ? "PingFang SC" : "Microsoft YaHei", SKFontStyle.Bold);
        using var paint = new SKPaint { Typeface = typeface, TextSize = 42, IsAntialias = true, TextAlign = SKTextAlign.Center };
        var lines = caption.Replace("\r", "").Split('\n');
        float width = lines.Max(line => paint.MeasureText(line));
        if (width > 470) paint.TextSize *= 470 / width;
        float lineHeight = paint.TextSize * 1.3f;
        for (int i = 0; i < lines.Length; i++)
        {
            float y = 488 - (lines.Length - i - 1) * lineHeight;
            paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 5; paint.Color = SKColors.Black;
            canvas.DrawText(lines[i], 256, y, paint);
            paint.Style = SKPaintStyle.Fill; paint.Color = SKColors.White;
            canvas.DrawText(lines[i], 256, y, paint);
        }
    }

    public static void ValidateEdit(StickerEditOptions edit)
    {
        double[] values = [edit.CropX, edit.CropY, edit.CropWidth, edit.CropHeight, edit.StartSeconds, edit.DurationSeconds, edit.Speed];
        if (values.Any(v => !double.IsFinite(v)) || edit.CropX < 0 || edit.CropY < 0 || edit.CropWidth <= 0 || edit.CropHeight <= 0
            || edit.CropX + edit.CropWidth > 1.000001 || edit.CropY + edit.CropHeight > 1.000001)
            throw new ArgumentException("裁剪区域必须位于原图以内，宽高不能为零。");
        if (edit.StartSeconds < 0 || edit.DurationSeconds <= 0 || edit.DurationSeconds > 3 || edit.Speed < 0.25 || edit.Speed > 4)
            throw new ArgumentException("起点不能为负，输出时长应在 0～3 秒内，速度应在 0.25～4 倍之间。");
        if (edit.Caption.Length > 200 || edit.Caption.Count(c => c == '\n') > 3)
            throw new ArgumentException("文字最多 200 字、4 行。");
    }

    private string NewBlob(string extension) => Guid.NewGuid().ToString("N") + extension;
    private string ExportPath(string extension)
    {
        string directory = Path.Combine(persistence.RootDirectory, "sticker-exports");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, NewBlob(extension));
    }
    private static ClipKind Kind(StickerFormat format) => format switch
    {
        StickerFormat.Gif => ClipKind.Gif, StickerFormat.Webm => ClipKind.VideoSticker,
        StickerFormat.Tgs => ClipKind.VectorSticker, _ => ClipKind.Image,
    };
    private static void SavePng(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动媒体转换组件。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var cancellation = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            if (ct.IsCancellationRequested) throw;
            throw new IOException("媒体转换超过 2 分钟，请缩短素材后重试。");
        }
        string error = await stderr;
        // Kill can complete the exit wait before its cancellation callback runs. Still report cancellation.
        ct.ThrowIfCancellationRequested();
        if (timeout.IsCancellationRequested) throw new IOException("媒体转换超过 2 分钟，请缩短素材后重试。");
        if (process.ExitCode != 0) throw new IOException("媒体转换失败：" + (error.Length > 1000 ? error[^1000..] : error));
        return await stdout;
    }

    private sealed class WorkDirectory : IDisposable
    {
        private readonly string _parent;
        public string Path { get; }
        public WorkDirectory(string root)
        {
            _parent = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, "sticker-work")) + System.IO.Path.DirectorySeparatorChar;
            Path = System.IO.Path.Combine(_parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            if (!System.IO.Path.GetFullPath(Path).StartsWith(_parent, StringComparison.OrdinalIgnoreCase)) return;
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
