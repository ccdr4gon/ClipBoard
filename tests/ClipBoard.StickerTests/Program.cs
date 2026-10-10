using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipBoard.Models;
using ClipBoard.Services;
using ClipBoard.Views;
using SkiaSharp;

internal static class Program
{
    private static readonly string Repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string Tools = Path.Combine(Repo, "tools", "media");
    [STAThread]
    private static int Main(string[] args)
    {
        typeof(ClipBoard.App).GetProperty(nameof(ClipBoard.App.Settings))!.SetValue(null, new AppSettings());
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Task<int> task = args.Contains("--live") ? LiveAsync(args) : args.Contains("--history-preview") ? HistoryPreviewOnly() : SuiteAsync();
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => application.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        try { return task.GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.Message); return 1; }
        finally { application.Shutdown(); }
    }

    private static async Task<int> SuiteAsync()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("静态原件保留、512 规格和透明背景", StaticMedia),
            ("WebP 不依赖系统图片扩展解码", WebpMedia),
            ("GIF 编辑、动图预览、视频贴纸规格", AnimatedMedia),
            ("超长 GIF 要求明确截取", LongAnimation),
            ("TGS 原样输出与修改后动画转换", VectorMedia),
            ("混合贴纸包导入及重复导入保留本地修改", ImportPack),
            ("Telegram 导入提前下载，但按顺序处理、跳过和报错", ImportPipeline),
            ("创建包归属用户并只替换修改项", PublishAndReplace),
            ("替换成功但响应丢失后重试不重复添加", RetryReplace),
            ("创建成功但响应丢失后重试不重复建包", RetryCreate),
            ("满 120 张的包先删后加并保持顺序", FullPackUpdate),
            ("原件、发布对应关系、删除记录重启后保留", PersistenceRoundTrip),
            ("emoji 组合与原件恢复", RestoreAndEmoji),
            ("连接配置加密且不包含明文 Token", EncryptedConnection),
            ("原有导出入口也包含 GIF 和 TGS", ExistingExporter),
            ("取消媒体转换后原件仍可用", CancelMedia),
            ("原面板贴纸标签与启动前新增标签不重复", RenderOriginalPanel),
            ("历史直接显示缩略图且只裁切长图上下区域", RenderHistoryPreview),
        ];
        int failures = 0;
        foreach (var (name, run) in tests)
        {
            try { await run(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Fails(Func<Task> action, string message)
    {
        try { await action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    private static string Png(string root, int seed = 1, int width = 160, int height = 100, bool webp = false)
    {
        string path = Path.Combine(root, $"sample-{seed}" + (webp ? ".webp" : ".png"));
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = new SKColor((byte)(seed * 17), (byte)(seed * 47), (byte)(seed * 73)), IsAntialias = true };
        canvas.DrawRoundRect(SKRect.Create(10, 10, width - 20, height - 20), 12, 12, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(webp ? SKEncodedImageFormat.Webp : SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path); data.SaveTo(stream); return path;
    }
    private static async Task<string> Gif(string root, int seconds = 2)
    {
        string path = Path.Combine(root, $"motion-{seconds}.gif");
        await RunTool(Path.Combine(Tools, "ffmpeg.exe"), ["-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc=size=96x64:rate=10:duration={seconds}", path]);
        return path;
    }
    private static string Tgs(string root)
    {
        string path = Path.Combine(root, "vector.tgs");
        const string json = """
            {"tgs":1,"v":"5.5.2","fr":60,"ip":0,"op":120,"w":512,"h":512,"nm":"Test sticker","ddd":0,"assets":[],"layers":[{"ddd":0,"ind":1,"ty":4,"nm":"Circle","sr":1,"ks":{"o":{"a":0,"k":100},"r":{"a":0,"k":0},"p":{"a":0,"k":[256,256,0]},"a":{"a":0,"k":[0,0,0]},"s":{"a":0,"k":[100,100,100]}},"ao":0,"shapes":[{"ty":"el","d":1,"s":{"a":0,"k":[180,180]},"p":{"a":0,"k":[0,0]},"nm":"Ellipse"},{"ty":"fl","c":{"a":0,"k":[0.1,0.6,0.9,1]},"o":{"a":0,"k":100},"r":1,"nm":"Fill"}],"ip":0,"op":120,"st":0,"bm":0}]}
            """;
        using var file = File.Create(path); using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        gzip.Write(Encoding.UTF8.GetBytes(json)); return path;
    }
    private static async Task RunTool(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException(await error);
    }

    private static async Task StaticMedia()
    {
        using var f = new Fixture(); string path = Png(f.Root);
        var item = await f.Add(path); string originalHash = Hash(path);
        Check(Hash(f.Persistence.GetBlobPath(item.Sticker!.OriginalBlobName)) == originalHash, "原件被修改");
        var output = await f.Media.PrepareTelegramAsync(item, default);
        using var bitmap = SKBitmap.Decode(output.Path);
        Check(Math.Max(bitmap.Width, bitmap.Height) == 512, "小图未补足 512 像素");
        Check(bitmap.GetPixel(0, 0).Alpha == 0, "透明边缘丢失");
        Check(new FileInfo(output.Path).Length <= 512 * 1024, "静态文件超限");
    }
    private static async Task WebpMedia()
    {
        using var f = new Fixture(); var item = await f.Add(Png(f.Root, webp: true));
        Check(item.Image != null && item.Sticker!.OriginalFormat == StickerFormat.Webp, "WebP 没有正常导入");
    }
    private static async Task AnimatedMedia()
    {
        using var f = new Fixture(); var item = await f.Add(await Gif(f.Root));
        string original = Hash(f.Persistence.GetBlobPath(item.Sticker!.OriginalBlobName));
        var edited = await f.Media.EditAsync(item, new StickerEditOptions(CropWidth: 0.75, Caption: "动图测试", StartSeconds: 0.2, DurationSeconds: 1.5, Speed: 1.2), default);
        f.Library.ReplaceLocal(f.Folder, item, edited);
        var prepared = await f.Media.PrepareTelegramAsync(edited, default);
        var info = await f.Media.InspectAsync(prepared.Path, StickerFormat.Webm, default);
        Check(prepared.Format == "video" && info.Codec == "vp9" && !info.HasAudio && info.Duration <= 3.05 && info.Fps <= 30.01, "视频规格不正确");
        Check(info.Width == 512 && info.Height == 512 && new FileInfo(prepared.Path).Length <= 256 * 1024, "视频尺寸或大小超限");
        string preview = await f.Media.CreatePreviewAsync(edited, default);
        using var codec = SKCodec.Create(preview);
        Check(codec.FrameCount > 1, "预览丢失动画帧");
        Check(Hash(f.Persistence.GetBlobPath(edited.Sticker!.OriginalBlobName)) == original, "编辑覆盖了原始 GIF");
    }
    private static async Task LongAnimation()
    {
        using var f = new Fixture(); var item = await f.Add(await Gif(f.Root, 4));
        await Fails(() => f.Media.PrepareTelegramAsync(item, default), "超长 GIF 被静默截断");
    }
    private static async Task VectorMedia()
    {
        using var f = new Fixture(); var item = await f.Add(Tgs(f.Root));
        Check(item.Image != null && item.Kind == ClipKind.VectorSticker, "TGS 预览失败");
        var untouched = await f.Media.PrepareTelegramAsync(item, default);
        Check(untouched.Format == "animated" && Hash(untouched.Path) == Hash(f.Persistence.GetBlobPath(item.Sticker!.OriginalBlobName)), "TGS 未修改时没有保留原文件");
        var edited = await f.Media.EditAsync(item, new StickerEditOptions(Caption: "TGS", DurationSeconds: 1), default);
        var output = await f.Media.PrepareTelegramAsync(edited, default);
        Check(output.Format == "video", "修改后的 TGS 未转换为视频贴纸");
        using var poster = SKBitmap.Decode(f.Persistence.GetBlobPath(edited.ImageBlobName!));
        Check(poster.GetPixel(256, 256).Alpha > 0, "TGS 画面未实际渲染");
    }
    private static async Task ImportPack()
    {
        using var f = new Fixture(); using var server = new FakeTelegram(); using var http = new HttpClient(server);
        using var client = new TelegramStickerClient(FakeTelegram.Token, http);
        server.Seed("SourcePack", (File.ReadAllBytes(Png(f.Root)), "static"), (File.ReadAllBytes(Tgs(f.Root)), "animated"));
        var remote = await client.GetSetAsync("https://t.me/addstickers/SourcePack", default);
        var first = await f.Library.ImportTelegramAsync(client, remote, remote.Stickers, null, default);
        Check(first.Added == 2 && first.Errors.Count == 0, "整包导入失败");
        var item = first.Folder.Items[0];
        var edited = await f.Media.EditAsync(item, new StickerEditOptions(Caption: "保留修改"), default);
        f.Library.ReplaceLocal(first.Folder, item, edited);
        var again = await f.Library.ImportTelegramAsync(client, remote, remote.Stickers, null, default);
        Check(again.Added == 0 && again.Skipped == 2 && first.Folder.Items[0].Sticker!.Revision == 2, "重复导入覆盖了本地修改");
    }
    private static async Task ImportPipeline()
    {
        using var f = new Fixture(); using var server = new FakeTelegram(); using var slow = new SlowNetwork(server); using var http = new HttpClient(slow);
        using var client = new TelegramStickerClient(FakeTelegram.Token, http);
        var files = Enumerable.Range(1, 6).Select(i => (File.ReadAllBytes(Png(f.Root, i)), "static")).ToList();
        files.Insert(2, ([1, 2, 3], "static")); // 第 3 张无法识别
        server.Seed("PipelinePack", [.. files]);
        var remote = await client.GetSetAsync("PipelinePack", default);
        var result = await f.Library.ImportTelegramAsync(client, remote, [.. remote.Stickers, remote.Stickers[0]], null, default);
        Check(result.Added == 6 && result.Skipped == 1 && result.Errors.Count == 1 && result.Errors[0].StartsWith("第 3 张："), "导入结果不对：" + string.Join(";", result.Errors));
        Check(result.Folder.Items.Select(i => i.Title).SequenceEqual(new[] { 1, 2, 4, 5, 6, 7 }.Select(n => $"{remote.Title} {n}")), "导入顺序改变");
        Check(slow.Peak is > 1 and <= 3, $"同时进行的请求数不对：{slow.Peak}");
        var reloaded = new FavoritesStore(f.Persistence, f.Persistence.Load());
        Check(reloaded.Folders.Single(x => x.Id == result.Folder.Id).Items.Count == 6, "导入的贴纸没有全部落盘");
    }

    // 给每个请求加一点延迟并记录同时进行的请求数，用来确认下载与处理确实重叠。
    private sealed class SlowNetwork(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _active;
        public int Peak;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Peak = Math.Max(Peak, Interlocked.Increment(ref _active));
            try { await Task.Delay(40, ct); return await base.SendAsync(request, ct); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private static async Task PublishAndReplace()
    {
        using var f = new Fixture(); using var server = new FakeTelegram(); using var http = new HttpClient(server); using var client = new TelegramStickerClient(FakeTelegram.Token, http);
        await f.Add(Png(f.Root, 1)); await f.Add(Png(f.Root, 2));
        var publisher = new TelegramStickerPublisher(client, f.Media, f.Library.Save);
        var plan = await publisher.PrepareAsync(f.Folder, 123456789, "", null, default);
        await publisher.PublishAsync(plan, null, default);
        Check(server.LastOwner == 123456789, "包没有归属于指定用户");
        string unchanged = f.Folder.Items[1].Sticker!.PublishedFileId!;
        var old = f.Folder.Items[0]; var edited = await f.Media.EditAsync(old, new StickerEditOptions(Caption: "新版"), default);
        f.Library.ReplaceLocal(f.Folder, old, edited);
        plan = await publisher.PrepareAsync(f.Folder, 123456789, "", null, default);
        Check(plan.Added == 0 && plan.Replaced == 1, "修改清单不是仅一张");
        await publisher.PublishAsync(plan, null, default);
        Check(server.Replaces == 1 && server.Creates == 1 && f.Folder.Items[1].Sticker!.PublishedFileId == unchanged, "更新重复建包或影响未修改项");
        f.Folder.Items.Move(1, 0);
        await publisher.PublishAsync(await publisher.PrepareAsync(f.Folder, 123456789, "", null, default), null, default);
        Check(server.Sets[plan.SetName].Entries[0].File.Id == unchanged, "排序没有同步");
    }
    private static async Task RetryReplace()
    {
        using var f = new Fixture(); using var server = new FakeTelegram(); using var http = new HttpClient(server); using var client = new TelegramStickerClient(FakeTelegram.Token, http);
        var item = await f.Add(Png(f.Root)); var publisher = new TelegramStickerPublisher(client, f.Media, f.Library.Save);
        await publisher.PublishAsync(await publisher.PrepareAsync(f.Folder, 99, "", null, default), null, default);
        f.Library.ReplaceLocal(f.Folder, item, await f.Media.EditAsync(item, new StickerEditOptions(Caption: "重试"), default));
        server.TimeoutAfterReplace = true;
        await Fails(async () => await publisher.PublishAsync(await publisher.PrepareAsync(f.Folder, 99, "", null, default), null, default), "未模拟响应丢失");
        await publisher.PublishAsync(await publisher.PrepareAsync(f.Folder, 99, "", null, default), null, default);
        Check(server.Replaces == 1 && server.Adds == 0 && server.Sets.Values.Single().Entries.Count == 1, "重试产生了重复贴纸");
        Check(f.Folder.Items[0].StickerStatus == "已同步", "恢复后同步状态未更新");
    }
    private static async Task RetryCreate()
    {
        using var f = new Fixture(); using var server = new FakeTelegram { TimeoutAfterCreate = true }; using var http = new HttpClient(server); using var client = new TelegramStickerClient(FakeTelegram.Token, http);
        await f.Add(Png(f.Root)); var publisher = new TelegramStickerPublisher(client, f.Media, f.Library.Save);
        await Fails(async () => await publisher.PublishAsync(await publisher.PrepareAsync(f.Folder, 99, "", null, default), null, default), "未模拟创建响应丢失");
        await publisher.PublishAsync(await publisher.PrepareAsync(f.Folder, 99, "", null, default), null, default);
        Check(server.Creates == 1 && server.Sets.Count == 1, "超时后重复创建包");
    }
    private static async Task FullPackUpdate()
    {
        using var f = new Fixture(); using var server = new FakeTelegram(); using var http = new HttpClient(server); using var client = new TelegramStickerClient(FakeTelegram.Token, http);
        for (int i = 1; i <= 120; i++) await f.Add(Png(f.Root, i));
        var publisher = new TelegramStickerPublisher(client, f.Media, f.Library.Save);
        await publisher.PublishAsync(await publisher.PrepareAsync(f.Folder, 99, "", null, default), null, default);
        Check(server.Creates == 1 && server.Adds == 70, "超过 50 张的包没有分批添加");
        f.Favorites.RemoveFavorite(f.Folder.Items[0]); await f.Add(Png(f.Root, 121));
        await publisher.PublishAsync(await publisher.PrepareAsync(f.Folder, 99, "", null, default), null, default);
        Check(server.Deletes == 1 && server.Sets.Values.Single().Entries.Count == 120, "满包无法先删后加");
    }
    private static async Task PersistenceRoundTrip()
    {
        using var f = new Fixture(); var item = await f.Add(Png(f.Root));
        item.Sticker!.PublishedFileId = "published"; item.Sticker.PublishedUniqueId = "unique"; item.Sticker.PendingFileId = "pending";
        f.Folder.Telegram = new TelegramPackBinding { SetName = "test_by_bot", BotId = 42, OwnerUserId = 99, DeletedFileIds = ["deleted"] };
        f.Library.Save();
        var loaded = new FavoritesStore(f.Persistence, f.Persistence.Load()); var restored = loaded.Folders.Single().Items.Single();
        Check(restored.Sticker?.PendingFileId == "pending" && restored.Sticker.OriginalBlobName == item.Sticker.OriginalBlobName, "素材对应关系保存丢失");
        Check(loaded.Folders.Single().Telegram?.DeletedFileIds.Single() == "deleted", "待删除记录保存丢失");
        f.Favorites.RemoveFavorite(item);
        Check(!File.Exists(f.Persistence.GetBlobPath(item.Sticker.OriginalBlobName)), "素材删除后遗留无人引用的原件");
    }
    private static async Task RestoreAndEmoji()
    {
        using var f = new Fixture(); var item = await f.Add(Png(f.Root)); var edited = await f.Media.EditAsync(item, new StickerEditOptions(Caption: "修改"), default);
        f.Library.ReplaceLocal(f.Folder, item, edited);
        var restored = await f.Media.RestoreAsync(edited, default); f.Library.ReplaceLocal(f.Folder, edited, restored);
        Check(restored.PixelW == 160 && restored.PixelH == 100, "恢复原件时尺寸丢失");
        Check(StickerLibraryService.ParseEmojis("👨‍👩‍👧‍👦 👍🏽 🇸🇬").Length == 3, "组合 emoji 被拆开");
    }
    private static Task EncryptedConnection()
    {
        using var f = new Fixture(); var store = new TelegramConnectionStore(f.Root);
        store.Save(new(FakeTelegram.Token, 99, Tools));
        Check(!File.ReadAllText(Path.Combine(f.Root, "telegram-connection.json")).Contains(FakeTelegram.Token), "Token 明文落盘");
        Check(store.Load().Token == FakeTelegram.Token && store.Load().OwnerUserId == 99, "连接配置不能恢复");
        // 文件格式与旧版（反射、默认选项）写出的一致，旧版也能读取。
        store.Save(new(FakeTelegram.Token, 7, Path.Combine(Tools, "中文 \"目录\"")));
        string text = File.ReadAllText(Path.Combine(f.Root, "telegram-connection.json"));
        Check(JsonSerializer.Serialize(JsonSerializer.Deserialize<LegacyConnection>(text)) == text && store.Load().MediaToolsDirectory.EndsWith("中文 \"目录\""), "连接配置文件格式改变");
        return Task.CompletedTask;
    }
    private sealed record LegacyConnection(string ProtectedToken, long OwnerUserId, string MediaToolsDirectory);
    private static async Task ExistingExporter()
    {
        using var f = new Fixture(); await f.Add(Png(f.Root)); await f.Add(await Gif(f.Root)); await f.Add(Tgs(f.Root));
        string output = Path.Combine(f.Root, "export");
        int count = await MemePackExporter.ExportTelegramAsync(f.Folder, output, f.Persistence, default);
        Check(count == 3 && Directory.GetFiles(output).Length == 3, "旧导出入口遗漏动图");
        Check(File.Exists(Path.Combine(output, "002.webm")) && File.Exists(Path.Combine(output, "003.tgs")), "旧导出格式不正确");
    }
    private static async Task CancelMedia()
    {
        using var f = new Fixture(); var item = await f.Add(await Gif(f.Root));
        string before = Hash(f.Persistence.GetBlobPath(item.Sticker!.OriginalBlobName));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));
        bool cancelled = false;
        try { await f.Media.EditAsync(item, new StickerEditOptions(Caption: "取消测试"), cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "转换没有响应取消");
        Check(Hash(f.Persistence.GetBlobPath(item.Sticker.OriginalBlobName)) == before, "取消破坏了原件");
    }
    private static async Task RenderOriginalPanel()
    {
        using var f = new Fixture(); f.Folder.Name = "我的贴纸包";
        for (int i = 1; i <= 5; i++) { var item = await f.Add(Png(f.Root, i)); item.Title = "示例贴纸 " + i; }
        var vector = await f.Add(Tgs(f.Root)); vector.Title = "Telegram 动画";
        typeof(ClipBoard.App).GetProperty(nameof(ClipBoard.App.Favorites))!.SetValue(null, f.Favorites);
        typeof(ClipBoard.App).GetProperty(nameof(ClipBoard.App.Persistence))!.SetValue(null, f.Persistence);
        typeof(ClipBoard.App).GetProperty(nameof(ClipBoard.App.History))!.SetValue(null, f.History);
        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/ClipBoard;component/Views/HistoryItemTemplate.xaml") });
        var window = new ClipBoard.MainWindow();
        var early = f.Favorites.CreateFolder("导入的贴纸包", FolderKind.Meme);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var tabs = (System.Windows.Controls.TabControl)window.FindName("Tabs");
        Check(tabs.Items.OfType<System.Windows.Controls.TabItem>().Count(t => t.Tag == early) == 1, "首次显示前导入产生重复标签");
        typeof(ClipBoard.MainWindow).GetMethod("SelectStickerFolder", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [f.Folder]);
        Check(((System.Windows.Controls.TabItem)tabs.SelectedItem).Tag == f.Folder, "导入包没有切到对应标签");
        Check(typeof(ClipBoard.MainWindow).Assembly.GetType("ClipBoard.Views.StickerManagerWindow") == null, "仍存在独立管理窗口");
        var content = (FrameworkElement)window.Content; content.Width = 780; content.Height = 640;
        content.Measure(new Size(780, 640)); content.Arrange(new Rect(0, 0, 780, 640)); content.UpdateLayout();
        var image = new RenderTargetBitmap(780, 640, 96, 96, PixelFormats.Pbgra32); image.Render(content);
        string output = Path.Combine(Repo, "out", "sticker-tabs-preview.png"); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var stream = File.Create(output)) encoder.Save(stream);
        window.Close(); Console.WriteLine("UI preview: " + output);
    }

    private static async Task<int> HistoryPreviewOnly()
    {
        await RenderHistoryPreview();
        Console.WriteLine("PASS 历史缩略图：长图裁切、宽图完整、原图保留");
        return 0;
    }

    private static Task RenderHistoryPreview()
    {
        using var f = new Fixture();
        BitmapSource Bands(int width, int height, bool vertical)
        {
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                var colors = new[] { Brushes.Red, Brushes.Lime, Brushes.Blue };
                for (int i = 0; i < 3; i++) dc.DrawRectangle(colors[i], null, vertical
                    ? new Rect(0, i * height / 3.0, width, height / 3.0)
                    : new Rect(i * width / 3.0, 0, width / 3.0, height));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing); bitmap.Freeze(); return bitmap;
        }
        // Insertion is newest-first: long image, wide image, ordinary image, then text.
        f.History.AddText("文字条目仍保持紧凑，图片直接显示在历史中。");
        f.History.AddImage(Bands(640, 480, false));
        f.History.AddImage(Bands(1200, 200, false));
        f.History.AddImage(Bands(300, 1200, true));
        typeof(ClipBoard.App).GetProperty(nameof(ClipBoard.App.Favorites))!.SetValue(null, f.Favorites);
        typeof(ClipBoard.App).GetProperty(nameof(ClipBoard.App.Persistence))!.SetValue(null, f.Persistence);
        typeof(ClipBoard.App).GetProperty(nameof(ClipBoard.App.History))!.SetValue(null, f.History);
        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/ClipBoard;component/Views/HistoryItemTemplate.xaml") });
        var window = new ClipBoard.MainWindow();
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var tabs = (System.Windows.Controls.TabControl)window.FindName("Tabs");
        var list = (System.Windows.Controls.ListBox)((System.Windows.Controls.TabItem)tabs.Items[0]).Content;
        // Regression: preview must work even without a special Tag supplied by its host.
        list.Tag = null;
        var content = (FrameworkElement)window.Content; content.Width = 780; content.Height = 700;
        content.Measure(new Size(780, 700)); content.Arrange(new Rect(0, 0, 780, 700)); content.UpdateLayout();
        T? Find<T>(DependencyObject node) where T : DependencyObject
        {
            if (node is T match) return match;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                if (Find<T>(VisualTreeHelper.GetChild(node, i)) is { } child) return child;
            return null;
        }
        RenderTargetBitmap Preview(int index)
        {
            var row = (System.Windows.Controls.ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
            var presenter = Find<System.Windows.Controls.ContentPresenter>(row)!;
            // 历史列表按类型选行模板（ItemTemplateSelector），取这一行实际套用的那个模板来找名字。
            var template = list.ItemTemplate ?? list.ItemTemplateSelector.SelectTemplate(row.Content, presenter);
            var viewport = (FrameworkElement)template.FindName("HistoryImageViewport", presenter);
            Check(viewport.Visibility == Visibility.Visible && Math.Abs(viewport.ActualWidth / viewport.ActualHeight - 16.0 / 9) < .01,
                $"历史缩略图不可见或比例不正确：{viewport.ActualWidth}×{viewport.ActualHeight}");
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawRectangle(new VisualBrush(viewport) { Stretch = Stretch.Fill }, null, new Rect(0, 0, 176, 99));
            var image = new RenderTargetBitmap(176, 99, 96, 96, PixelFormats.Pbgra32); image.Render(visual); return image;
        }
        byte[] Pixel(BitmapSource image, int x, int y) { var pixel = new byte[4]; image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel; }
        var tall = Preview(0);
        foreach (int y in new[] { 1, 49, 97 }) Check(Pixel(tall, 88, y)[1] > 240 && Pixel(tall, 88, y)[2] < 10,
            $"长图没有居中裁掉上下区域：y={y}, BGRA={string.Join(',', Pixel(tall, 88, y))}");
        var wide = Preview(1);
        Check(Pixel(wide, 2, 49)[2] > 240 && Pixel(wide, 173, 49)[0] > 240, "宽图的左右内容被裁掉");
        using (var stream = File.OpenRead(f.Persistence.GetBlobPath(f.History.Items[0].ImageBlobName!)))
            Check(BitmapFrame.Create(stream).PixelHeight == 1200, "裁切修改了原图");
        var screenshot = new RenderTargetBitmap(780, 700, 96, 96, PixelFormats.Pbgra32); screenshot.Render(content);
        string output = Path.Combine(Repo, "out", "history-thumbnails-preview.png"); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(screenshot));
        using (var stream = File.Create(output)) encoder.Save(stream);
        window.Close(); Console.WriteLine("UI preview: " + output);
        return Task.CompletedTask;
    }

    private static async Task<int> LiveAsync(string[] args)
    {
        var connection = TelegramConnectionStore.LoadEnvironment(Repo);
        using var client = new TelegramStickerClient(connection.Token);
        var bot = await client.GetMeAsync(default);
        var owners = await client.FindOwnersAsync(default);
        string? requestedOwner = args.FirstOrDefault(a => a.StartsWith("--owner="))?.Split('=', 2)[1];
        if (!long.TryParse(requestedOwner, out long owner) || !owners.Any(o => o.Id == owner))
            throw new InvalidOperationException("真实验证必须通过 --owner=用户ID 明确指定已向机器人发送 /start 的所有者。");
        Console.WriteLine($"Live bot=@{bot.Username}, owner={owner}");
        string root = Path.Combine(Repo, "out", "telegram-live-data");
        var persistence = new PersistenceService(root);
        var favorites = new FavoritesStore(persistence, persistence.Load());
        var history = new HistoryStore(favorites); history.SetPersistence(persistence); favorites.SetHistoryStore(history);
        var media = new StickerMediaService(persistence, Tools);
        var library = new StickerLibraryService(favorites, persistence, media);
        var folder = favorites.Folders.FirstOrDefault(f => f.Name == "ClipBoard 功能验证" && f.Telegram?.SourceSetName == null)
            ?? favorites.CreateFolder("ClipBoard 功能验证", FolderKind.Meme);
        async Task AddSample(string title, Func<Task<string>> create)
        {
            if (folder.Items.Any(i => i.Title == title)) return;
            var item = await media.ImportFileAsync(await create()); item.Title = title; item.FolderId = folder.Id;
            folder.Items.Add(item); library.Save();
        }
        await AddSample("静态样例", () => Task.FromResult(Png(root, 23)));
        await AddSample("动图样例", () => Gif(root));
        await AddSample("TGS 动画样例", () => Task.FromResult(Tgs(root)));
        var publisher = new TelegramStickerPublisher(client, media, library.Save);
        var progress = new Progress<string>(Console.WriteLine);
        var plan = await publisher.PrepareAsync(folder, owner, "", progress, default);
        await publisher.PublishAsync(plan, progress, default);
        var remote = await client.GetSetAsync(plan.SetName, default);
        Check(remote.Stickers.Length == 3, "真实贴纸包数量不正确");
        Check(remote.Stickers.Any(s => s.IsVideo) && remote.Stickers.Any(s => s.IsAnimated)
            && remote.Stickers.Any(s => !s.IsVideo && !s.IsAnimated), "真实包没有同时包含静态、视频和 TGS 三种贴纸");
        Console.WriteLine("PASS 真实 Telegram 混合格式包创建");
        var imported = await library.ImportTelegramAsync(client, remote, remote.Stickers, progress, default);
        Check(imported.Errors.Count == 0 && imported.Folder.Items.Count >= 3, "真实整包下载或解析失败：" + string.Join(";", imported.Errors));
        var repeated = await library.ImportTelegramAsync(client, remote, remote.Stickers, progress, default);
        Check(repeated.Added == 0 && repeated.Skipped == 3, "真实重复导入产生重复项");
        Console.WriteLine("PASS 真实 Telegram 整包导入与重复导入");
        var changing = folder.Items.Single(i => i.Title == "动图样例");
        var unchanged = folder.Items.Where(i => i != changing).Select(i => i.Sticker!.PublishedUniqueId).ToArray();
        string? oldUnique = changing.Sticker!.PublishedUniqueId;
        var edited = await media.EditAsync(changing, new StickerEditOptions(Caption: "已修改 " + DateTime.Now.ToString("HH:mm:ss"), DurationSeconds: 1.2), default);
        library.ReplaceLocal(folder, changing, edited);
        var update = await publisher.PrepareAsync(folder, owner, "", progress, default);
        Check(update.Added == 0 && update.Replaced == 1, "真实更新清单不是仅一张");
        await publisher.PublishAsync(update, progress, default);
        var updated = await client.GetSetAsync(plan.SetName, default);
        Check(updated.Stickers.Length == 3 && updated.Stickers.All(s => s.FileUniqueId != oldUnique), "真实替换没有移除旧贴纸");
        Check(unchanged.All(id => updated.Stickers.Any(s => s.FileUniqueId == id)), "真实替换影响了其他贴纸");
        Check(updated.Stickers[folder.Items.IndexOf(edited)].FileUniqueId == edited.Sticker!.PublishedUniqueId, "真实替换改变了贴纸的位置");
        Console.WriteLine("PASS 真实 Telegram 单张修改后替换，其他贴纸和顺序保持不变");
        string export = await library.ExportAsync(folder, root, progress, default);
        var requested = await client.GetSetAsync("jiulm", default);
        var requestedImport = await library.ImportTelegramAsync(client, requested, requested.Stickers, progress, default);
        Check(requestedImport.Errors.Count == 0, "jiulm 存在导入失败：" + string.Join(";", requestedImport.Errors));
        Check(requested.Stickers.All(s => requestedImport.Folder.Items.Any(i => i.Sticker?.SourceUniqueId == s.FileUniqueId)), "jiulm 有贴纸遗漏");
        var requestedAgain = await library.ImportTelegramAsync(client, requested, requested.Stickers, progress, default);
        Check(requestedAgain.Added == 0 && requestedAgain.Skipped == requested.Stickers.Length, "jiulm 重复导入未去重");
        Console.WriteLine($"PASS 用户指定 jiulm 包完整导入：{requested.Stickers.Length} 张，重复导入无新增");
        var result = new { verifiedAt = DateTimeOffset.Now, bot = bot.Username, owner, setName = plan.SetName,
            url = "https://t.me/addstickers/" + plan.SetName, count = updated.Stickers.Length, export,
            importedSet = requested.Name, importedCount = requested.Stickers.Length, status = "passed" };
        string report = Path.Combine(Repo, "out", "telegram-live-result.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("LIVE_RESULT " + JsonSerializer.Serialize(result));
        return 0;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ClipBoard.StickerTests", Guid.NewGuid().ToString("N"));
        public PersistenceService Persistence { get; }
        public FavoritesStore Favorites { get; }
        public HistoryStore History { get; }
        public FavoriteFolder Folder { get; }
        public StickerMediaService Media { get; }
        public StickerLibraryService Library { get; }
        public Fixture()
        {
            Persistence = new(Root); Favorites = new(Persistence, new PersistedData());
            History = new HistoryStore(Favorites); History.SetPersistence(Persistence); Favorites.SetHistoryStore(History);
            Media = new(Persistence, Tools); Library = new(Favorites, Persistence, Media); Folder = Favorites.CreateFolder("测试包", FolderKind.Meme);
        }
        public async Task<ClipItem> Add(string path)
        {
            var item = await Media.ImportFileAsync(path); item.FolderId = Folder.Id; Folder.Items.Add(item); Library.Save(); return item;
        }
        public void Dispose()
        {
            Persistence.FlushSync(); string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ClipBoard.StickerTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup path");
            Directory.Delete(Root, recursive: true);
        }
    }
}
